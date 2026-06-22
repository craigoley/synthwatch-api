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
        var traceUrl = await _db.Runs.AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => r.TraceUrl)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrEmpty(traceUrl))
            return ApiResults.NotFound($"No trace for run {id}.");

        try
        {
            var blob = new BlobClient(new Uri(traceUrl), _credential);
            var resp = await blob.DownloadStreamingAsync(cancellationToken: ct);
            return new FileStreamResult(resp.Value.Content, "application/zip")
            {
                FileDownloadName = $"trace-run-{id}.zip"
            };
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // trace_url recorded but the blob is gone (retention/cleanup).
            return ApiResults.NotFound($"Trace blob for run {id} is no longer available.");
        }
        catch (RequestFailedException ex)
        {
            TraceLog.BlobError(_logger, id, ex.Status, ex);
            throw; // surfaced as a generic 500 by the exception middleware (no blob detail leaked)
        }
    }
}

/// <summary>High-performance (CA1848) log delegates for the trace proxy.</summary>
internal static partial class TraceLog
{
    [LoggerMessage(EventId = 4000, Level = LogLevel.Error,
        Message = "Trace blob download failed for run {RunId} (status {Status})")]
    public static partial void BlobError(ILogger logger, long runId, int status, Exception ex);
}
