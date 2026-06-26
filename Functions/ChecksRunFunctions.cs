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
/// On-demand "Run now" — trigger a single monitor immediately instead of waiting for its */5 timer.
/// Mirrors the channel test-send path: POST enqueues a run_requests row ('pending') and starts the runner
/// Container App Job on-demand (the SAME IRunnerJobTrigger), so the runner force-runs the check NOW through
/// its normal run path; the run shows up in the check's run history. The cron tick is the fallback if the
/// job-start fails. The API only INSERTs the pending row + READs to coalesce — the runner owns the lifecycle.
///
/// ★ Gating: a POST is a mutating verb, so AuthGate (fail-closed-by-verb) requires an editor/admin session
/// automatically — no per-endpoint check needed. (It spends compute → it's a write.)
/// </summary>
public class ChecksRunFunctions
{
    private readonly SynthWatchDbContext _db;
    private readonly IRunnerJobTrigger _runnerJob;

    public ChecksRunFunctions(SynthWatchDbContext db, IRunnerJobTrigger runnerJob)
    {
        _db = db;
        _runnerJob = runnerJob;
    }

    /// <summary>
    /// POST /api/checks/{id}/run — enqueue an on-demand run + kick the runner. 404 if the check is
    /// unknown; 409 if it's paused (a disabled check won't run — clear feedback beats a silent no-op).
    /// Otherwise 202 { requestId }. IDEMPOTENT: a second request while one is still pending coalesces onto
    /// the existing one (the partial unique index rejects the duplicate insert → we return the pending id).
    /// A failed job-start does NOT fail the request: the row stays 'pending' for the cron-tick fallback.
    /// </summary>
    [Function("RunCheckNow")]
    public async Task<IActionResult> RunCheckNow(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "checks/{id:long}/run")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        var check = await _db.Checks.AsNoTracking()
            .Where(c => c.Id == id).Select(c => new { c.Id, c.Enabled }).FirstOrDefaultAsync(ct);
        if (check is null) return ApiResults.NotFound($"Check {id} not found.");
        if (!check.Enabled) return ApiResults.Conflict("Monitor is paused — resume it before running on-demand.");

        var request = new RunRequest { CheckId = id, Status = "pending" };
        _db.RunRequests.Add(request);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The partial unique index (one pending per check) rejected this insert — a run is already
            // queued. Idempotent: return the existing pending request instead of erroring.
            _db.Entry(request).State = EntityState.Detached;
            var pendingId = await _db.RunRequests.AsNoTracking()
                .Where(r => r.CheckId == id && r.Status == "pending")
                .Select(r => r.Id).FirstOrDefaultAsync(ct);
            if (pendingId != 0) return ApiResults.Accepted(new RunNowAcceptedDto(pendingId));
            throw; // not the coalesce case — surface it
        }

        // Best-effort on-demand start. If it fails, the pending row remains for the cron-tick fallback;
        // we still return 202 with the requestId so the dashboard can track the run either way.
        await _runnerJob.StartAsync(ct);

        return ApiResults.Accepted(new RunNowAcceptedDto(request.Id));
    }
}
