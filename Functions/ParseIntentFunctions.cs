using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;

namespace SynthWatch.Api.Functions;

/// <summary>
/// POST /api/checks/parse-intent — chat-to-prefill for NON-BROWSER monitors. Free text → gpt-5-mini (MI-authed,
/// json-mode) → a monitor spec → ★ run through the SAME CheckValidation.TryBuildNew that POST /checks uses
/// (validate-don't-trust) → return {fields, valid, fieldErrors, redirect?}. The dashboard PREFILLS the create
/// modal; the HUMAN clicks Create. This endpoint NEVER persists a check. Editor-gated by the POST verb-gate;
/// INERT (NotConfigured, never 500) until AZURE_OPENAI_* + the MI role are set, exactly like the AOAI endpoints.
/// </summary>
public class ParseIntentFunctions
{
    private readonly IAoaiClient _aoai;

    public ParseIntentFunctions(IAoaiClient aoai) => _aoai = aoai;

    public sealed class ParseIntentRequest { public string? Text { get; set; } }

    [Function("ParseMonitorIntent")]
    public async Task<IActionResult> ParseMonitorIntent(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "checks/parse-intent")] HttpRequest req,
        CancellationToken ct)
    {
        if (!_aoai.IsConfigured)
            return ApiResults.Ok(ParseIntentDto.NotConfigured()); // inert until the deploy prereq — clean, not a 500

        var (body, bodyError) = await RequestJson.ReadAsync<ParseIntentRequest>(req, ct);
        if (bodyError is not null) return bodyError;
        if (string.IsNullOrWhiteSpace(body?.Text))
            return ApiResults.BadRequest("text is required.");

        var result = await _aoai.ChatJsonAsync(ParseIntent.SystemPrompt, ParseIntent.BuildUser(body.Text), ct);
        if (result.Outcome != AoaiOutcome.Ok)
            return ApiResults.Ok(MapFailure(result)); // honest transient-vs-deterministic message

        var s = ParseIntent.Parse(result.Content!);
        if (s is null)
            return ApiResults.Ok(ParseIntentDto.Unavailable(
                "The AI returned an unexpected response format. Re-running is unlikely to help.", retryable: false));

        // A browser/multistep ask → redirect, never a fabricated prefill.
        if (!string.IsNullOrWhiteSpace(s.Redirect))
            return ApiResults.Ok(ParseIntentDto.RedirectTo(s.Redirect!,
                s.Reason ?? "Browser monitors are authored as code in the monitors repo, then set up from the Catalog."));

        // ★ VALIDATE-DON'T-TRUST: the suggestion goes through the EXACT validator a real create uses. Return the
        // parsed fields ALWAYS (so the form prefills what parsed) + the field-keyed errors the form renders.
        var fields = new CreateCheckRequest
        {
            Name = s.Name,
            Kind = s.Kind,
            TargetUrl = s.TargetUrl,
            IntervalSeconds = s.IntervalSeconds,
            TimeoutMs = s.TimeoutMs,
            CertExpiryWarnDays = s.CertExpiryWarnDays,
            NetConfig = s.NetConfig,
            // #158: carry the request fields the model captured (http only). TryBuildNew below validates them
            // exactly as the form does — a bad method/assertion/auth becomes a fieldError, never a silent drop.
            Method = s.Method,
            ExpectedStatus = s.ExpectedStatus,
            RequestHeaders = s.RequestHeaders,
            RequestBody = s.RequestBody,
            BodyMustContain = s.BodyMustContain,
            Assertions = s.Assertions,
            Auth = s.Auth,
        };
        var valid = CheckValidation.TryBuildNew(fields, out _, out var errors);
        return ApiResults.Ok(ParseIntentDto.Parsed(fields, valid, errors, s.Notes));
    }

    /// <summary>Map a non-Ok AOAI outcome to an honest, distinct message (transient vs deterministic).</summary>
    public static ParseIntentDto MapFailure(AoaiResult r) => r.Outcome switch
    {
        AoaiOutcome.Truncated => ParseIntentDto.Unavailable(
            "Your request was too complex for the model to parse in one pass — try rephrasing it more simply.", retryable: false),
        AoaiOutcome.Filtered => ParseIntentDto.Unavailable(
            "The AI could not parse this request (it was blocked by a content filter).", retryable: false),
        AoaiOutcome.EmptyContent => ParseIntentDto.Unavailable(
            "The AI returned nothing for this request — try rephrasing it.", retryable: false),
        AoaiOutcome.Timeout => ParseIntentDto.Unavailable(
            "The AI service didn't respond in time — please try again in a moment.", retryable: true),
        AoaiOutcome.HttpError when r.Transient => ParseIntentDto.Unavailable(
            "The AI service is busy right now — please try again in a moment.", retryable: true),
        _ => ParseIntentDto.Unavailable("Monitor-prefill is unavailable right now.", retryable: false),
    };
}
