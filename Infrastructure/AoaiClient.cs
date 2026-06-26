using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Microsoft.Extensions.Logging;

namespace SynthWatch.Api.Infrastructure;

/// <summary>Why a chat call did/didn't yield usable content — so the caller can give an HONEST, distinct
/// message (transient → retry helps; deterministic → it won't) instead of one undifferentiated "unavailable".</summary>
public enum AoaiOutcome
{
    Ok,             // 200 + non-empty content (finish_reason stop)
    NotConfigured,  // AZURE_OPENAI_* absent
    Truncated,      // finish_reason "length" — hit max_completion_tokens (reasoning + output); deterministic
    Filtered,       // finish_reason "content_filter" — Azure blocked this content; deterministic
    EmptyContent,   // 200 but no content (and not length/filter)
    HttpError,      // non-2xx from AOAI (429/5xx transient; others not)
    Timeout,        // our 30s cap tripped
    Faulted,        // token/network/parse exception
}

/// <summary>Token usage from the AOAI response (logged so we can SEE how close to the budget a call ran).</summary>
public sealed record AoaiUsage(int PromptTokens, int CompletionTokens, int ReasoningTokens, int TotalTokens);

/// <summary>The full outcome of a chat call (not just the content) so the endpoint can branch + log.</summary>
public sealed record AoaiResult(AoaiOutcome Outcome, string? Content, string? FinishReason, int HttpStatus, AoaiUsage? Usage)
{
    /// <summary>Retrying might help (busy/timeout); a deterministic outcome (truncated/filtered/parse) won't.</summary>
    public bool Transient => Outcome == AoaiOutcome.Timeout ||
        (Outcome == AoaiOutcome.HttpError && HttpStatus is 429 or 500 or 502 or 503 or 504);
}

/// <summary>
/// Azure OpenAI chat transport for the API — a faithful C# port of the runner's proven path
/// (runner/aoai.ts): the SAME DefaultAzureCredential the DB/blob tokens use → a bearer token for the
/// cognitive-services scope → POST to the AOAI REST chat/completions endpoint with JSON mode, gpt-5-mini.
/// NON-FATAL: every failure becomes an <see cref="AoaiResult"/> (never throws) AND is logged with
/// finish_reason + token usage + HTTP status, so a deterministic "unavailable" (truncation / content-filter /
/// parse) is distinguishable from a transient one. Retries ONCE on a transient error.
///
/// Driven entirely by AZURE_OPENAI_* app settings; <see cref="IsConfigured"/> is false until they're set.
/// The API uses its SYSTEM-assigned MI, so the bare DefaultAzureCredential resolves the token (no client-id pin).
/// </summary>
public interface IAoaiClient
{
    /// <summary>True once AZURE_OPENAI_ENDPOINT + _DEPLOYMENT are set. Callers gate the feature on this.</summary>
    bool IsConfigured { get; }

    /// <summary>Run a chat-completion (response_format json_object) and return the full outcome (content +
    /// finish_reason + usage + status). NEVER throws.</summary>
    Task<AoaiResult> ChatJsonAsync(string system, string user, CancellationToken ct);
}

public sealed class AoaiClient : IAoaiClient
{
    private const string Scope = "https://cognitiveservices.azure.com/.default";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromMilliseconds(750);

    private readonly HttpClient _http;
    private readonly TokenCredential _credential;
    private readonly ILogger<AoaiClient> _logger;

    public AoaiClient(HttpClient http, TokenCredential credential, ILogger<AoaiClient> logger)
    {
        _http = http;
        _credential = credential;
        _logger = logger;
    }

    private static string? Endpoint => Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
    private static string? Deployment => Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");
    private static string ApiVersion => Env("AZURE_OPENAI_API_VERSION", "2025-04-01-preview");
    private static int MaxTokens =>
        int.TryParse(Environment.GetEnvironmentVariable("AZURE_OPENAI_MAX_TOKENS"), out var n) ? n : 16000;
    private static string? ReasoningEffort => Environment.GetEnvironmentVariable("AZURE_OPENAI_REASONING_EFFORT");

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(Deployment);

    public async Task<AoaiResult> ChatJsonAsync(string system, string user, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            AoaiLog.NotConfigured(_logger);
            return new AoaiResult(AoaiOutcome.NotConfigured, null, null, 0, null);
        }

        var result = await OnceAsync(system, user, ct);
        if (result.Transient)
        {
            AoaiLog.Retrying(_logger, result.Outcome, result.HttpStatus);
            try { await Task.Delay(RetryBackoff, ct); } catch (OperationCanceledException) { return result; }
            result = await OnceAsync(system, user, ct);
        }
        return result;
    }

    private async Task<AoaiResult> OnceAsync(string system, string user, CancellationToken ct)
    {
        try
        {
            var token = await _credential.GetTokenAsync(new TokenRequestContext([Scope]), ct);
            var url = $"{Endpoint!.TrimEnd('/')}/openai/deployments/{Deployment}/chat/completions?api-version={ApiVersion}";

            var body = new Dictionary<string, object?>
            {
                ["messages"] = new[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = user },
                },
                ["max_completion_tokens"] = MaxTokens,
                ["response_format"] = new { type = "json_object" }, // JSON mode
            };
            if (!string.IsNullOrWhiteSpace(ReasoningEffort)) body["reasoning_effort"] = ReasoningEffort;

            using var msg = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);

            using var resp = await _http.SendAsync(msg, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                AoaiLog.HttpError(_logger, (int)resp.StatusCode);
                return new AoaiResult(AoaiOutcome.HttpError, null, null, (int)resp.StatusCode, null);
            }

            var parsed = await resp.Content.ReadFromJsonAsync<ChatResponse>(cts.Token);
            var choice = parsed?.Choices is { Count: > 0 } c ? c[0] : null;
            var content = choice?.Message?.Content;
            var finish = choice?.FinishReason;
            var usage = parsed?.Usage is { } u
                ? new AoaiUsage(u.PromptTokens, u.CompletionTokens, u.Details?.ReasoningTokens ?? 0, u.TotalTokens)
                : null;

            // ★ The diagnostic line the self-analysis prescribed: finish_reason + usage + content length.
            AoaiLog.Completed(_logger, (int)resp.StatusCode, finish ?? "none",
                usage?.PromptTokens ?? 0, usage?.CompletionTokens ?? 0, usage?.ReasoningTokens ?? 0,
                content?.Length ?? 0);

            // Classify deterministically so the caller's message is honest.
            if (string.Equals(finish, "content_filter", StringComparison.OrdinalIgnoreCase))
                return new AoaiResult(AoaiOutcome.Filtered, content, finish, (int)resp.StatusCode, usage);
            if (string.IsNullOrWhiteSpace(content))
                return new AoaiResult(
                    string.Equals(finish, "length", StringComparison.OrdinalIgnoreCase) ? AoaiOutcome.Truncated : AoaiOutcome.EmptyContent,
                    null, finish, (int)resp.StatusCode, usage);
            if (string.Equals(finish, "length", StringComparison.OrdinalIgnoreCase))
                // Non-empty but truncated → the JSON is almost certainly incomplete; treat as truncated.
                return new AoaiResult(AoaiOutcome.Truncated, content, finish, (int)resp.StatusCode, usage);

            return new AoaiResult(AoaiOutcome.Ok, content, finish, (int)resp.StatusCode, usage);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            AoaiLog.Timeout(_logger);
            return new AoaiResult(AoaiOutcome.Timeout, null, null, 0, null);
        }
        catch (Exception ex)
        {
            AoaiLog.Faulted(_logger, ex);
            return new AoaiResult(AoaiOutcome.Faulted, null, null, 0, null);
        }
    }

    private sealed record ChatResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<Choice>? Choices,
        [property: JsonPropertyName("usage")] Usage? Usage);
    private sealed record Choice(
        [property: JsonPropertyName("message")] Message? Message,
        [property: JsonPropertyName("finish_reason")] string? FinishReason);
    private sealed record Message([property: JsonPropertyName("content")] string? Content);
    private sealed record Usage(
        [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
        [property: JsonPropertyName("total_tokens")] int TotalTokens,
        [property: JsonPropertyName("completion_tokens_details")] UsageDetails? Details);
    private sealed record UsageDetails([property: JsonPropertyName("reasoning_tokens")] int ReasoningTokens);

    private static string Env(string name, string fallback)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? fallback : v;
    }
}

internal static partial class AoaiLog
{
    [LoggerMessage(EventId = 6000, Level = LogLevel.Information, Message = "AOAI not configured (AZURE_OPENAI_* absent) — skipped")]
    public static partial void NotConfigured(ILogger logger);

    // ★ The structured diagnostic line: finish_reason + token usage + content length. finish_reason=length →
    // truncation (raise budget / cap input); content_filter → blocked; reasoning_tokens near the budget → tight.
    [LoggerMessage(EventId = 6001, Level = LogLevel.Information,
        Message = "AOAI completed: http={Status} finish_reason={FinishReason} prompt_tokens={PromptTokens} completion_tokens={CompletionTokens} reasoning_tokens={ReasoningTokens} content_len={ContentLen}")]
    public static partial void Completed(ILogger logger, int status, string finishReason,
        int promptTokens, int completionTokens, int reasoningTokens, int contentLen);

    [LoggerMessage(EventId = 6002, Level = LogLevel.Warning, Message = "AOAI HTTP error {Status} (non-fatal)")]
    public static partial void HttpError(ILogger logger, int status);

    [LoggerMessage(EventId = 6003, Level = LogLevel.Warning, Message = "AOAI call faulted (non-fatal)")]
    public static partial void Faulted(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 6004, Level = LogLevel.Warning, Message = "AOAI timed out after the 30s cap (non-fatal)")]
    public static partial void Timeout(ILogger logger);

    [LoggerMessage(EventId = 6005, Level = LogLevel.Information, Message = "AOAI transient {Outcome} (http={Status}) — retrying once")]
    public static partial void Retrying(ILogger logger, AoaiOutcome outcome, int status);
}
