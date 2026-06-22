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

public class RunsFunctions
{
    private readonly SynthWatchDbContext _db;
    private readonly TokenCredential _credential;
    private readonly ILogger<RunsFunctions> _logger;

    public RunsFunctions(SynthWatchDbContext db, TokenCredential credential, ILogger<RunsFunctions> logger)
    {
        _db = db;
        _credential = credential;
        _logger = logger;
    }

    /// <summary>GET /api/runs/{id}/steps — ordered funnel steps for a run.</summary>
    [Function("ListRunSteps")]
    public async Task<IActionResult> ListRunSteps(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "runs/{id:long}/steps")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        if (!await _db.Runs.AnyAsync(r => r.Id == id, ct))
            return ApiResults.NotFound($"Run {id} not found.");

        var steps = (await _db.RunSteps.AsNoTracking()
            .Where(s => s.RunId == id)
            .OrderBy(s => s.StepIndex)
            .ToListAsync(ct))
            .Select(RunStepDto.From)
            .ToList();

        return ApiResults.Ok(steps);
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
        return await StreamRunArtifact(req, id, url, "trace", "application/zip",
            $"attachment; filename=\"trace-run-{id}.zip\"", ct);
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
        return await StreamRunArtifact(req, id, url, "screenshot", "image/png",
            $"inline; filename=\"screenshot-run-{id}.png\"", ct);
    }

    /// <summary>
    /// Streams a run artifact blob through the API using its managed identity, so artifacts stay
    /// behind the API (the artifacts account has public access disabled). Validates the blob host
    /// before attaching the token. 404 for no/invalid url or a missing blob; other blob errors are
    /// logged and shielded as a generic 500.
    /// </summary>
    private async Task<IActionResult> StreamRunArtifact(
        HttpRequest req, long id, string? blobUrl, string artifact, string contentType,
        string contentDisposition, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(blobUrl))
            return ApiResults.NotFound($"No {artifact} for run {id}.");

        // Defence-in-depth: the *_url is runner-written, but never attach the API's managed-identity
        // token to an arbitrary host. Only proxy Azure Blob endpoints.
        if (!Uri.TryCreate(blobUrl, UriKind.Absolute, out var blobUri) ||
            !blobUri.Host.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase))
        {
            ArtifactLog.InvalidUrl(_logger, artifact, id);
            return ApiResults.NotFound($"No {artifact} for run {id}.");
        }

        try
        {
            var blob = new BlobClient(blobUri, _credential);
            var resp = await blob.DownloadStreamingAsync(cancellationToken: ct);
            // Set Content-Disposition explicitly (inline for images, attachment for downloads); do
            // NOT use FileStreamResult.FileDownloadName, which would force attachment.
            req.HttpContext.Response.Headers.ContentDisposition = contentDisposition;
            return new FileStreamResult(resp.Value.Content, contentType);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // url recorded but the blob is gone (retention/cleanup).
            return ApiResults.NotFound($"{artifact} blob for run {id} is no longer available.");
        }
        catch (RequestFailedException ex)
        {
            ArtifactLog.BlobError(_logger, artifact, id, ex.Status, ex);
            throw; // surfaced as a generic 500 by the exception middleware (no blob detail leaked)
        }
    }
}

/// <summary>High-performance (CA1848) log delegates for the artifact (trace/screenshot) proxy.</summary>
internal static partial class ArtifactLog
{
    [LoggerMessage(EventId = 4000, Level = LogLevel.Error,
        Message = "{Artifact} blob download failed for run {RunId} (status {Status})")]
    public static partial void BlobError(ILogger logger, string artifact, long runId, int status, Exception ex);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Warning,
        Message = "Run {RunId} {Artifact} url is not an Azure Blob endpoint; refusing to proxy it")]
    public static partial void InvalidUrl(ILogger logger, string artifact, long runId);
}
