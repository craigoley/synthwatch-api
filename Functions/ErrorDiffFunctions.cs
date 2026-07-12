using System.Text.Json;
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
/// Error-diff (P2): GET /api/checks/{id}/error-diff — the errors THIS run has that are NEW / PERSISTENT /
/// RESOLVED vs the last-N settled runs. Computed entirely from persisted <c>runs.trace_signals</c> (no zip
/// re-parse); the pure diff lives in <see cref="ErrorDiff"/>. Same forensic auth gate as the trace-signals
/// endpoint (it surfaces the same error text) — <see cref="SessionReadGate"/> (#154).
/// </summary>
public class ErrorDiffFunctions
{
    private readonly SynthWatchDbContext _db;
    private readonly IAuthPrincipal _auth;

    // Runner-written trace_signals is camelCase JSON — same options GetTraceSignals uses.
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public ErrorDiffFunctions(SynthWatchDbContext db, IAuthPrincipal auth)
    {
        _db = db;
        _auth = auth;
    }

    [Function("GetCheckErrorDiff")]
    public async Task<IActionResult> GetCheckErrorDiff(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "checks/{id:long}/error-diff")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        // Forensic gate: error text is the same class of data as /runs/{id}/trace-signals → require a session.
        if (await SessionReadGate.RequireSessionAsync(_auth, req, ct) is { } denied) return denied;

        // ?baseline=N overrides the anti-flap window (clamped); default = the named constant.
        var n = int.TryParse(req.Query["baseline"], out var bn) ? Math.Clamp(bn, 1, 10) : ErrorDiff.BaselineRuns;

        // Target: an explicit ?runId (any run of THIS check), else the latest SETTLED run WITH signals.
        Run? target;
        if (long.TryParse(req.Query["runId"], out var rid))
            target = await _db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rid && r.CheckId == id, ct);
        else
            target = await _db.Runs.AsNoTracking()
                .Where(r => r.CheckId == id && r.Status != "running" && r.TraceSignals != null)
                .OrderByDescending(r => r.StartedAt)
                .FirstOrDefaultAsync(ct);

        if (target is null)
            return ApiResults.NotFound($"No settled run with trace signals for check {id}.");

        // Baseline: the N settled runs WITH signals, SAME LOCATION, strictly before the target, newest first.
        // (Same-location keeps it apples-to-apples for a multi-location check; sandbox runs are INCLUDED — a
        // paused monitor's on-demand validations are real captures, unlike the SLO rollups that exclude them.)
        var q = _db.Runs.AsNoTracking()
            .Where(r => r.CheckId == id && r.Status != "running" && r.TraceSignals != null
                        && r.StartedAt < target.StartedAt && r.Id != target.Id);
        q = target.Location is null ? q.Where(r => r.Location == null) : q.Where(r => r.Location == target.Location);
        var baselineRuns = await q.OrderByDescending(r => r.StartedAt).Take(n).ToListAsync(ct);

        var dto = ErrorDiff.Compute(
            id, target.Id, target.StartedAt, target.Location,
            ToSignals(target), baselineRuns.Select(ToSignals).ToList());

        // Session-gated forensic data → NEVER publicly cacheable (a shared cache keyed on Origin, not
        // Authorization, could serve the authed 200 to a later anonymous caller — CWE-525). Matches the
        // sibling reconcile/trace-signals reads.
        req.HttpContext.Response.Headers.CacheControl = "no-store";
        return ApiResults.Ok(dto);
    }

    // Parse a run's persisted signals (drifted/absent JSON → no signals, never a 500 — same tolerance as
    // GetTraceSignals) and compute its truncation flag.
    private static ErrorDiff.RunSignals ToSignals(Run r)
    {
        TraceSignalsDto? sig = null;
        if (!string.IsNullOrEmpty(r.TraceSignals))
        {
            try { sig = JsonSerializer.Deserialize<TraceSignalsDto>(r.TraceSignals, WebJson); }
            catch (JsonException) { sig = null; }
        }
        return new ErrorDiff.RunSignals(r.Id, sig, ErrorDiff.IsTruncated(sig));
    }
}
