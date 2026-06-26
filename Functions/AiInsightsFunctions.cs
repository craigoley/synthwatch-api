using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using SynthWatch.Api.Data;
using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;

namespace SynthWatch.Api.Functions;

/// <summary>
/// Trace AI Insights — slice 2. POST /api/runs/{id}/ai-insights extracts the slice-1 trace summary and sends
/// it to gpt-5-mini for structured, categorized insights about the monitored site. A POST because it SPENDS
/// AOAI tokens — so the AuthorizationMiddleware verb-gate requires an editor/admin session (natural cost
/// control) when enforcement is on. INERT until the deploy prereq is done: if AZURE_OPENAI_* is unset, it
/// returns a clean "not configured" response (200), never a 500. Non-fatal throughout (model down / bad
/// trace → a clean response, never a 500).
/// </summary>
public class AiInsightsFunctions
{
    private readonly SynthWatchDbContext _db;
    private readonly IArtifactReader _artifacts;
    private readonly IAoaiClient _aoai;

    public AiInsightsFunctions(SynthWatchDbContext db, IArtifactReader artifacts, IAoaiClient aoai)
    {
        _db = db;
        _artifacts = artifacts;
        _aoai = aoai;
    }

    [Function("GetAiInsights")]
    public async Task<IActionResult> GetAiInsights(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "runs/{id:long}/ai-insights")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        // INERT until the deploy prereq (MI role + AZURE_OPENAI_* settings) is done — clean, not a 500.
        if (!_aoai.IsConfigured)
            return ApiResults.Ok(AiInsightsDto.NotConfigured);

        var row = await _db.Runs.AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new
            {
                r.TraceUrl, r.Status, CheckName = r.Check!.Name, Target = r.Check!.TargetUrl,
                SuccessTraceUrl = r.Check!.SuccessTraceUrl,
            })
            .FirstOrDefaultAsync(ct);

        if (row is null) return ApiResults.NotFound($"Run {id} not found.");

        // Resolve the trace from the RIGHT source: a failure run has its own per-run trace; a SUCCESS run
        // leaves trace_url null (#113) and its baseline lives in the check's success-trace slot — and success
        // insights are richer (the complete journey, not truncated at a failure). Fall back accordingly.
        var (traceUrl, traceSource) = ResolveTrace(row.TraceUrl, row.SuccessTraceUrl);
        if (string.IsNullOrEmpty(traceUrl))
            // Success run whose baseline hasn't been captured yet (6h throttle / first success), or a run
            // with no artifact at all — a clean "nothing to analyze", never a 500.
            return ApiResults.NotFound($"No trace available to analyze for run {id} yet.");

        var targetHost = !string.IsNullOrEmpty(row.Target) && Uri.TryCreate(row.Target, UriKind.Absolute, out var tu)
            ? tu.Host : null;

        var blob = await _artifacts.DownloadToMemoryAsync(traceUrl, "trace", id, ct);
        TraceSignalsDto signals;
        switch (blob.Status)
        {
            case ArtifactStatus.Missing:
            case ArtifactStatus.Gone:
                return ApiResults.NotFound($"No trace available to analyze for run {id} yet.");
            case ArtifactStatus.Unavailable:
                // ★ A transient blob error is now a clean, HONEST retryable response (was an unhandled 500).
                return ApiResults.Ok(AiInsightsDto.Unavailable(
                    "Couldn't read this run's trace right now — please try again in a moment.", retryable: true));
            default:
                using (blob.Content!)
                    signals = TraceExtractor.FromZip(blob.Content!, targetHost);
                break;
        }

        var run = new AiInsights.RunContext(row.CheckName, targetHost, row.Status, traceSource);
        var result = await _aoai.ChatJsonAsync(AiInsights.SystemPrompt, AiInsights.BuildUser(run, signals), ct);

        if (result.Outcome != AoaiOutcome.Ok)
            return ApiResults.Ok(MapFailure(result)); // distinct, HONEST message (transient vs deterministic)

        var insights = AiInsights.Parse(result.Content!);
        return ApiResults.Ok(insights ?? AiInsightsDto.Unavailable(
            "The AI returned an unexpected response format for this run. Re-running is unlikely to help.", retryable: false));
    }

    /// <summary>Map a non-Ok AOAI outcome to an HONEST, distinct user message — so a deterministic failure
    /// (truncation / content-filter / parse) doesn't masquerade as a transient one the user re-runs in vain.</summary>
    public static AiInsightsDto MapFailure(AoaiResult r) => r.Outcome switch
    {
        AoaiOutcome.Truncated => AiInsightsDto.Unavailable(
            "This run's trace was too complex for the model to analyze in one pass. Re-running won't help.", retryable: false),
        AoaiOutcome.Filtered => AiInsightsDto.Unavailable(
            "The AI could not analyze this run's content (it was blocked by a content filter).", retryable: false),
        AoaiOutcome.EmptyContent => AiInsightsDto.Unavailable(
            "The AI returned no analysis for this run.", retryable: false),
        AoaiOutcome.Timeout => AiInsightsDto.Unavailable(
            "The AI service didn't respond in time — please try again in a moment.", retryable: true),
        AoaiOutcome.HttpError when r.Transient => AiInsightsDto.Unavailable(
            "The AI service is busy right now — please try again in a moment.", retryable: true),
        _ => AiInsightsDto.Unavailable("AI insights are unavailable for this run right now.", retryable: false),
    };

    /// <summary>Which trace to analyze: the per-run trace (a failure run) if present, else the check's
    /// last-known-good SUCCESS baseline (a success run leaves trace_url null). Returns the chosen url + a
    /// human label of its source (for the prompt), or (null, "none") when neither exists yet.</summary>
    public static (string? Url, string Source) ResolveTrace(string? perRunTraceUrl, string? successTraceUrl) =>
        !string.IsNullOrEmpty(perRunTraceUrl) ? (perRunTraceUrl, "this run")
        : !string.IsNullOrEmpty(successTraceUrl) ? (successTraceUrl, "the monitor's latest successful run (a complete, untruncated journey)")
        : (null, "none");
}
