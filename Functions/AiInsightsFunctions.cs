using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
    private readonly TokenCredential _credential;
    private readonly IAoaiClient _aoai;
    private readonly ILogger<AiInsightsFunctions> _logger;

    public AiInsightsFunctions(SynthWatchDbContext db, TokenCredential credential, IAoaiClient aoai,
        ILogger<AiInsightsFunctions> logger)
    {
        _db = db;
        _credential = credential;
        _aoai = aoai;
        _logger = logger;
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

        if (!Uri.TryCreate(traceUrl, UriKind.Absolute, out var blobUri) ||
            !blobUri.Host.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase))
            return ApiResults.NotFound($"No trace available to analyze for run {id} yet.");

        var targetHost = !string.IsNullOrEmpty(row.Target) && Uri.TryCreate(row.Target, UriKind.Absolute, out var tu)
            ? tu.Host : null;

        TraceSignalsDto signals;
        try
        {
            var blob = new BlobClient(blobUri, _credential);
            using var ms = new MemoryStream();
            await blob.DownloadToAsync(ms, ct);
            ms.Position = 0;
            signals = TraceExtractor.FromZip(ms, targetHost);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return ApiResults.NotFound($"trace blob for run {id} is no longer available.");
        }

        var run = new AiInsights.RunContext(row.CheckName, targetHost, row.Status, traceSource);
        var content = await _aoai.ChatJsonAsync(AiInsights.SystemPrompt, AiInsights.BuildUser(run, signals), ct);
        if (content is null)
            return ApiResults.Ok(AiInsightsDto.Unavailable("The AI model was unavailable — please try again."));

        var insights = AiInsights.Parse(content);
        return ApiResults.Ok(insights ?? AiInsightsDto.Unavailable("The AI response could not be parsed."));
    }

    /// <summary>Which trace to analyze: the per-run trace (a failure run) if present, else the check's
    /// last-known-good SUCCESS baseline (a success run leaves trace_url null). Returns the chosen url + a
    /// human label of its source (for the prompt), or (null, "none") when neither exists yet.</summary>
    public static (string? Url, string Source) ResolveTrace(string? perRunTraceUrl, string? successTraceUrl) =>
        !string.IsNullOrEmpty(perRunTraceUrl) ? (perRunTraceUrl, "this run")
        : !string.IsNullOrEmpty(successTraceUrl) ? (successTraceUrl, "the monitor's latest successful run (a complete, untruncated journey)")
        : (null, "none");
}
