using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using SynthWatch.Api.Data;
using SynthWatch.Api.Infrastructure;

namespace SynthWatch.Api.Functions;

/// <summary>
/// Runner-written artifact proxying — Playwright traces, the per-monitor success-trace baseline, failure
/// screenshots, and the server-side trace-signals extraction. All read blobs from the artifacts account
/// through the API's managed identity (the account blocks public access), via the shared <see cref="IArtifactReader"/>
/// (one blob-host allowlist + download + 404/non-404 classification, previously duplicated 3×). Split out of
/// RunsFunctions so that file holds only non-artifact run responsibilities. Routes + Function names + auth
/// posture are unchanged from when these lived in RunsFunctions.
/// </summary>
public class ArtifactsFunctions
{
    private readonly SynthWatchDbContext _db;
    private readonly IArtifactReader _artifacts;

    public ArtifactsFunctions(SynthWatchDbContext db, IArtifactReader artifacts)
    {
        _db = db;
        _artifacts = artifacts;
    }

    /// <summary>
    /// GET /api/runs/{id}/trace — streams the run's Playwright trace.zip from Blob (the API proxies
    /// it with its managed identity; the trace blob is never publicly exposed). 404 when the run
    /// has no trace. Keeps trace access behind the same API auth model as everything else.
    /// </summary>
    [Function("GetRunTrace")]
    public async Task<IActionResult> GetRunTrace(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "runs/{id:long}/trace")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        var url = await _db.Runs.AsNoTracking()
            .Where(r => r.Id == id).Select(r => r.TraceUrl).FirstOrDefaultAsync(ct);
        return await ProxyAsync(req, id, $"run {id}", url, "trace", "application/zip",
            $"attachment; filename=\"trace-run-{id}.zip\"", ct);
    }

    /// <summary>
    /// GET /api/checks/{id}/success-trace — streams a MONITOR's last-known-good (most-recent-success)
    /// Playwright trace.zip from Blob, via the same managed-identity proxy as run traces. This is the
    /// per-monitor baseline at the stable, purge-exempt `success-latest/check-&lt;id&gt;.zip` key
    /// (checks.success_trace_url), overwritten on each success — NOT a per-run trace. 404 until the
    /// monitor has had a success with capture enabled. Lets the dashboard embed it like any trace.
    /// </summary>
    [Function("GetCheckSuccessTrace")]
    public async Task<IActionResult> GetCheckSuccessTrace(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "checks/{id:long}/success-trace")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        var url = await _db.Checks.AsNoTracking()
            .Where(c => c.Id == id).Select(c => c.SuccessTraceUrl).FirstOrDefaultAsync(ct);
        return await ProxyAsync(req, id, $"monitor {id}", url, "success trace", "application/zip",
            $"attachment; filename=\"success-trace-check-{id}.zip\"", ct);
    }

    /// <summary>
    /// GET /api/runs/{id}/screenshot — streams the run's failure screenshot from Blob via the same
    /// proxy as traces (the artifacts account blocks public access, so the raw blob URL 409s). 404
    /// when the run has no screenshot. Served inline so the dashboard &lt;img&gt; renders it.
    /// </summary>
    [Function("GetRunScreenshot")]
    public async Task<IActionResult> GetRunScreenshot(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "runs/{id:long}/screenshot")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        var url = await _db.Runs.AsNoTracking()
            .Where(r => r.Id == id).Select(r => r.ScreenshotUrl).FirstOrDefaultAsync(ct);
        return await ProxyAsync(req, id, $"run {id}", url, "screenshot", "image/png",
            $"inline; filename=\"screenshot-run-{id}.png\"", ct);
    }

    /// <summary>
    /// GET /api/runs/{id}/trace-signals — the compact, FILTERED summary extracted from the run's Playwright
    /// trace (network waterfall + real site console errors), NOT the ~18 MB trace itself. The API reads the
    /// trace blob with its managed identity (same proxy posture as /trace) and parses it server-side. A GET
    /// (read-only). Non-fatal: no trace → 404; an unparseable trace → a 200 empty summary, never a 500.
    /// </summary>
    [Function("GetTraceSignals")]
    public async Task<IActionResult> GetTraceSignals(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "runs/{id:long}/trace-signals")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        var row = await _db.Runs.AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new { r.TraceUrl, Target = r.Check!.TargetUrl })
            .FirstOrDefaultAsync(ct);

        if (row is null) return ApiResults.NotFound($"Run {id} not found.");
        if (string.IsNullOrEmpty(row.TraceUrl)) return ApiResults.NotFound($"No trace for run {id}.");

        var targetHost = !string.IsNullOrEmpty(row.Target) && Uri.TryCreate(row.Target, UriKind.Absolute, out var tu)
            ? tu.Host : null;

        var blob = await _artifacts.DownloadToMemoryAsync(row.TraceUrl, "trace", id, ct);
        switch (blob.Status)
        {
            case ArtifactStatus.Missing:
                return ApiResults.NotFound($"No trace for run {id}.");
            case ArtifactStatus.Gone:
                return ApiResults.NotFound($"trace blob for run {id} is no longer available.");
            case ArtifactStatus.Unavailable:
                return ApiResults.ServiceUnavailable($"the trace for run {id} is temporarily unavailable.");
            default:
                using (blob.Content!)
                    return ApiResults.Ok(TraceExtractor.FromZip(blob.Content!, targetHost)); // bad zip → empty summary
        }
    }

    /// <summary>Stream a runner-written artifact blob to the client through the shared reader. 404 for a
    /// missing url/blob, a clean 503 for a transient blob error (NOT a 500), else the file stream.</summary>
    private async Task<IActionResult> ProxyAsync(
        HttpRequest req, long id, string subject, string? blobUrl, string artifact, string contentType,
        string contentDisposition, CancellationToken ct)
    {
        var blob = await _artifacts.OpenStreamAsync(blobUrl, artifact, id, ct);
        switch (blob.Status)
        {
            case ArtifactStatus.Missing:
                return ApiResults.NotFound($"No {artifact} for {subject}.");
            case ArtifactStatus.Gone:
                return ApiResults.NotFound($"{artifact} blob for {subject} is no longer available.");
            case ArtifactStatus.Unavailable:
                return ApiResults.ServiceUnavailable($"the {artifact} for {subject} is temporarily unavailable.");
            default:
                // Set Content-Disposition explicitly (inline for images, attachment for downloads); do NOT use
                // FileStreamResult.FileDownloadName, which would force attachment.
                req.HttpContext.Response.Headers.ContentDisposition = contentDisposition;
                return new FileStreamResult(blob.Content!, contentType);
        }
    }
}
