using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using SynthWatch.Api.Data;
using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;

namespace SynthWatch.Api.Functions;

/// <summary>
/// Channel test-send (Option A): verify a channel delivers WITHOUT creating an incident or touching
/// routing/history, by triggering the RUNNER's REAL dispatch path — not a C# replica. POST enqueues a
/// test_send_requests row ('pending') and starts the runner Container App Job on-demand; the runner
/// drains the row and sends a [TEST] alert through the same transport real alerts use, then advances the
/// row's status (sending -> delivered|failed). GET reports that runner-owned status. The API only
/// INSERTs the pending row + READs status — it never sends, never mutates the lifecycle.
/// </summary>
public class ChannelTestFunctions
{
    private readonly SynthWatchDbContext _db;
    private readonly IRunnerJobTrigger _runnerJob;

    public ChannelTestFunctions(SynthWatchDbContext db, IRunnerJobTrigger runnerJob)
    {
        _db = db;
        _runnerJob = runnerJob;
    }

    /// <summary>
    /// POST /api/channels/{id}/test — enqueue a test-send + kick the runner. 404 if the channel is
    /// unknown; otherwise 202 { requestId }. A failed job-start does NOT fail the request: the row stays
    /// 'pending' and a cron tick drains it as a fallback (we still return 202 with the requestId).
    /// </summary>
    [Function("TestChannel")]
    public async Task<IActionResult> TestChannel(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "channels/{id:long}/test")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        var exists = await _db.Channels.AsNoTracking().AnyAsync(c => c.Id == id, ct);
        if (!exists) return ApiResults.NotFound($"Channel {id} not found.");

        var request = new TestSendRequest { ChannelId = id, Status = "pending" };
        _db.TestSendRequests.Add(request);
        await _db.SaveChangesAsync(ct);

        // Best-effort on-demand start. If it fails, the pending row remains for the cron-tick fallback;
        // we still return 202 with the requestId so the dashboard can poll status either way.
        await _runnerJob.StartAsync(ct);

        return ApiResults.Accepted(new ChannelTestAcceptedDto(request.Id));
    }

    /// <summary>
    /// GET /api/channels/{id}/test/status?requestId={requestId} — the runner-owned status of one
    /// test-send. 404 if the requestId doesn't exist OR doesn't belong to this channel.
    /// </summary>
    [Function("TestChannelStatus")]
    public async Task<IActionResult> TestChannelStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "channels/{id:long}/test/status")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        if (!long.TryParse(req.Query["requestId"], out var requestId))
            return ApiResults.BadRequest("requestId query parameter is required.");

        var row = await _db.TestSendRequests.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == requestId && r.ChannelId == id, ct);
        if (row is null) return ApiResults.NotFound($"Test-send request {requestId} not found for channel {id}.");

        return ApiResults.Ok(new ChannelTestStatusDto(row.Status, row.Detail, row.RequestedAt, row.CompletedAt));
    }
}
