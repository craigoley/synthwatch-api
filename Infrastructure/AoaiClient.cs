using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Microsoft.Extensions.Logging;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Azure OpenAI chat transport for the API — a faithful C# port of the runner's proven path
/// (runner/aoai.ts): the SAME DefaultAzureCredential the DB/blob tokens use → a bearer token for the
/// cognitive-services scope → POST to the AOAI REST chat/completions endpoint with JSON mode, gpt-5-mini.
/// NON-FATAL: any failure (unconfigured / token / HTTP / parse / timeout) returns null and is logged, never
/// throws — a model hiccup must not 500 the request.
///
/// Driven entirely by AZURE_OPENAI_* app settings; <see cref="IsConfigured"/> is false until they're set, so
/// the feature is INERT until the deploy prereq (the MI role + settings) is done. Unlike the runner (a
/// USER-assigned MI pinned via AZURE_CLIENT_ID), the API uses its SYSTEM-assigned MI, so the bare
/// DefaultAzureCredential resolves the token — no client-id pin needed.
/// </summary>
public interface IAoaiClient
{
    /// <summary>True once AZURE_OPENAI_ENDPOINT + _DEPLOYMENT are set. Callers gate the feature on this.</summary>
    bool IsConfigured { get; }

    /// <summary>Run a chat-completion (response_format json_object) and return the raw text content, or null
    /// on ANY failure (non-fatal, logged).</summary>
    Task<string?> ChatJsonAsync(string system, string user, CancellationToken ct);
}

public sealed class AoaiClient : IAoaiClient
{
    private const string Scope = "https://cognitiveservices.azure.com/.default";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

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
        int.TryParse(Environment.GetEnvironmentVariable("AZURE_OPENAI_MAX_TOKENS"), out var n) ? n : 4000;
    private static string? ReasoningEffort => Environment.GetEnvironmentVariable("AZURE_OPENAI_REASONING_EFFORT");

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(Deployment);

    public async Task<string?> ChatJsonAsync(string system, string user, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            AoaiLog.NotConfigured(_logger);
            return null;
        }

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
            AoaiLog.Http(_logger, (int)resp.StatusCode);
            if (!resp.IsSuccessStatusCode) return null; // non-fatal

            var parsed = await resp.Content.ReadFromJsonAsync<ChatResponse>(cts.Token);
            var choices = parsed?.Choices;
            var content = choices is { Count: > 0 } ? choices[0].Message?.Content : null;
            if (string.IsNullOrWhiteSpace(content))
            {
                AoaiLog.EmptyContent(_logger);
                return null;
            }
            return content;
        }
        catch (Exception ex)
        {
            AoaiLog.Failed(_logger, ex);
            return null; // NEVER throw — the endpoint falls back to a clean "unavailable"
        }
    }

    private sealed record ChatResponse([property: JsonPropertyName("choices")] IReadOnlyList<Choice>? Choices);
    private sealed record Choice([property: JsonPropertyName("message")] Message? Message);
    private sealed record Message([property: JsonPropertyName("content")] string? Content);

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

    [LoggerMessage(EventId = 6001, Level = LogLevel.Information, Message = "AOAI model HTTP {Status}")]
    public static partial void Http(ILogger logger, int status);

    [LoggerMessage(EventId = 6002, Level = LogLevel.Warning, Message = "AOAI returned empty content (non-fatal)")]
    public static partial void EmptyContent(ILogger logger);

    [LoggerMessage(EventId = 6003, Level = LogLevel.Warning, Message = "AOAI call failed (non-fatal)")]
    public static partial void Failed(ILogger logger, Exception ex);
}
