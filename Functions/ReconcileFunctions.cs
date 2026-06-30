using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SynthWatch.Api.Data;
using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;

namespace SynthWatch.Api.Functions;

/// <summary>
/// Monitors-as-code drift surface (Phase 6b). Serves the runner-owned reconcile_drift table (migration
/// 0031) READ-ONLY: the reconcile job diffs the synthwatch-monitors manifest against live checks and
/// UPSERTs what differs; this API never reconciles and never applies (apply is a later runner capability —
/// reconcile runs in report mode). The dashboard reads this to show "N monitors differ from Git".
/// </summary>
public class ReconcileFunctions
{
    private readonly SynthWatchDbContext _db;
    private readonly IRunnerJobTrigger _trigger;
    private readonly RunnerJobOptions _jobOptions;

    public ReconcileFunctions(SynthWatchDbContext db, IRunnerJobTrigger trigger, IOptions<RunnerJobOptions> jobOptions)
    {
        _db = db;
        _trigger = trigger;
        _jobOptions = jobOptions.Value;
    }

    /// <summary>
    /// POST /api/reconcile/trigger — ARM-start the reconcile ACA job NOW (don't wait for its hourly cron),
    /// reusing the #101-fixed job-start ({} body + application/json). Reconcile is start-and-run (no claim
    /// loop), so there is NO request table — just the bare immediate start. A POST (write verb), so the
    /// Phase 12 AuthorizationMiddleware requires editor/admin — correct for a manual, compute-spending action.
    /// Non-fatal: a failed start is LOGGED inside the trigger (ARM status/error) and surfaces as a clean 503,
    /// never an unhandled 500. 202 on success (fire-and-forget — the job runs async).
    /// </summary>
    [Function("TriggerReconcile")]
    public async Task<IActionResult> TriggerReconcile(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "reconcile/trigger")] HttpRequest req,
        CancellationToken ct)
    {
        var started = await _trigger.StartAsync(_jobOptions.ReconcileJobName, ct);
        if (!started)
            return ApiResults.ServiceUnavailable("Couldn't start the reconcile job — please try again.");
        return ApiResults.Accepted(new ReconcileTriggeredDto(true));
    }

    /// <summary>
    /// GET /api/reconcile/drift — the latest reconcile snapshot (the full current drift set). Empty items
    /// = live monitors are in sync with Git. Ordered drift_type then source_key for a stable display.
    /// detail jsonb is read as text and re-emitted verbatim (a 'changed' row's per-field before/after diff
    /// passes through unchanged).
    /// </summary>
    [Function("GetReconcileDrift")]
    public async Task<IActionResult> GetReconcileDrift(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "reconcile/drift")] HttpRequest req,
        CancellationToken ct)
    {
        var rows = await _db.ReconcileDrift.FromSql(
            $@"SELECT source_key, drift_type, detail::text AS detail, detected_at
               FROM reconcile_drift
               ORDER BY drift_type, source_key").AsNoTracking().ToListAsync(ct);

        var items = rows.Select(r =>
            new ReconcileDriftItemDto(r.SourceKey, r.DriftType, SafeJson(r.Detail), r.DetectedAt)).ToList();

        // The latest detected_at = when the last reconcile ran; null when there's no drift.
        DateTimeOffset? detectedAt = rows.Count == 0 ? null : rows.Max(r => r.DetectedAt);

        req.HttpContext.Response.Headers.CacheControl = "public, max-age=30";
        req.HttpContext.Response.Headers["Vary"] = "Origin";
        return ApiResults.Ok(new ReconcileDriftDto(items, detectedAt));
    }

    /// <summary>
    /// GET /api/reconcile/plan — the DRY-RUN apply plan per drift row (reconcile-apply Phase 0). For each
    /// drift the runner computed the statement(s) apply WOULD run; this serves them read-only. ★ Nothing is
    /// applied, and (this phase) nothing is approved/rejected — preview only. plan jsonb passes through verbatim.
    /// </summary>
    [Function("GetReconcilePlan")]
    public async Task<IActionResult> GetReconcilePlan(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "reconcile/plan")] HttpRequest req,
        CancellationToken ct)
    {
        var rows = await _db.ReconcileApplyPlan.FromSql(
            $@"SELECT source_key, drift_type, status, plan::text AS plan, computed_at
               FROM reconcile_apply_plan
               ORDER BY status, drift_type, source_key").AsNoTracking().ToListAsync(ct);

        var items = rows.Select(r =>
            new ReconcileApplyPlanItemDto(r.SourceKey, r.DriftType, r.Status, SafeJson(r.Plan), r.ComputedAt)).ToList();

        DateTimeOffset? computedAt = rows.Count == 0 ? null : rows.Max(r => r.ComputedAt);

        req.HttpContext.Response.Headers.CacheControl = "public, max-age=30";
        req.HttpContext.Response.Headers["Vary"] = "Origin";
        return ApiResults.Ok(new ReconcileApplyPlanDto(items, computedAt));
    }

    // Parse runner-written jsonb (read as text) defensively — a malformed value degrades to {}, never throws.
    private static JsonElement SafeJson(string? json)
    {
        try { return JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(json) ? "{}" : json); }
        catch (JsonException) { return JsonSerializer.Deserialize<JsonElement>("{}"); }
    }
}
