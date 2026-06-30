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

    // ───────────────────────────────────────────────────────────────────────────────────────────────
    // Reconcile-apply PHASE 1 — approve / reject / APPLY. The middleware editor-gates + audits every POST.
    // ★ This is the first thing that writes LIVE monitor config — safety leads (see the per-method ★ notes).
    // ───────────────────────────────────────────────────────────────────────────────────────────────

    private const int ApplyCap = 5; // ★ max plans applied per call — one buggy apply can't rewrite the fleet.

    /// <summary>POST /api/reconcile/approve — pending → approved. ★ A 'blocked' plan (a redaction strip) can
    /// NEVER be approved (422). Body {sourceKey, driftType}.</summary>
    [Function("ApproveReconcilePlan")]
    public async Task<IActionResult> ApproveReconcilePlan(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "reconcile/approve")] HttpRequest req,
        CancellationToken ct) => await DecideAsync(req, "approved", ct);

    /// <summary>POST /api/reconcile/reject — pending → rejected. Body {sourceKey, driftType}.</summary>
    [Function("RejectReconcilePlan")]
    public async Task<IActionResult> RejectReconcilePlan(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "reconcile/reject")] HttpRequest req,
        CancellationToken ct) => await DecideAsync(req, "rejected", ct);

    private async Task<IActionResult> DecideAsync(HttpRequest req, string decision, CancellationToken ct)
    {
        ReconcileDecisionRequest? body;
        try { body = await req.ReadFromJsonAsync<ReconcileDecisionRequest>(ct); }
        catch (JsonException) { return ApiResults.BadRequest("Request body is not valid JSON."); }
        if (body?.SourceKey is null || body.DriftType is null)
            return ApiResults.BadRequest("sourceKey and driftType are required.");

        var current = (await _db.ReconcileApplyPlan.FromSql(
            $@"SELECT source_key, drift_type, status, plan::text AS plan, computed_at
               FROM reconcile_apply_plan WHERE source_key = {body.SourceKey} AND drift_type = {body.DriftType}")
            .AsNoTracking().ToListAsync(ct)).FirstOrDefault();
        if (current is null) return ApiResults.NotFound("No plan for that source_key + drift_type.");
        // ★ The B10 fail-safe: a blocked redaction-strip can never be approved/rejected into action.
        if (current.Status == "blocked")
            return ApiResults.Conflict("This plan is BLOCKED (reconcile cannot strip redaction) and cannot be approved.");
        if (current.Status != "pending")
            return ApiResults.Conflict($"Plan is already '{current.Status}', not pending.");

        var who = (req.HttpContext.Items.TryGetValue("principal", out var p) ? p as Principal : null)?.Email;
        // Gate on status='pending' so a concurrent decision can't double-apply (rowCount 0 = lost the race).
        var n = await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE reconcile_apply_plan SET status = {decision}, decided_at = now(), decided_by = {who}
               WHERE source_key = {body.SourceKey} AND drift_type = {body.DriftType} AND status = 'pending'", ct);
        if (n == 0) return ApiResults.Conflict("Plan is no longer pending.");
        return ApiResults.Ok(new { sourceKey = body.SourceKey, driftType = body.DriftType, status = decision });
    }

    /// <summary>POST /api/reconcile/apply — execute the APPROVED plans (cap 5). ★ Each plan in ONE transaction;
    /// materialize the check with sensitive INLINE (atomic — no non-sensitive window) and enabled=FALSE
    /// (Phase 1 never enables), then seed check_locations. ROLLBACK on any error (the plan stays 'approved',
    /// re-appliable). Idempotent (ON CONFLICT). Only 'new' (materialize) is wired (the only pending type).</summary>
    [Function("ApplyReconcilePlans")]
    public async Task<IActionResult> ApplyReconcilePlans(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "reconcile/apply")] HttpRequest req,
        CancellationToken ct)
    {
        var approved = await _db.ReconcileApplyPlan.FromSql(
            $@"SELECT source_key, drift_type, status, plan::text AS plan, computed_at
               FROM reconcile_apply_plan WHERE status = 'approved' AND drift_type = 'new'
               ORDER BY source_key LIMIT {ApplyCap}").AsNoTracking().ToListAsync(ct);

        var applied = new List<string>();
        var failed = new List<string>();
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        foreach (var p in approved)
        {
            // ★ ONE transaction per plan — all-or-nothing (no check without locations, no non-sensitive window).
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                var doc = JsonSerializer.Deserialize<PlanDoc>(p.Plan, opts);
                var v = doc?.Statements?.FirstOrDefault()?.Values;
                if (v is null || v.Count < 9) throw new InvalidOperationException("plan has no materialize values");
                string src = v[0].GetString()!, name = v[1].GetString()!, kind = v[2].GetString()!,
                       url = v[3].GetString()!, flow = v[4].GetString()!, redact = v[6].GetString()!;
                bool sensitive = v[5].GetBoolean();
                int interval = v[7].GetInt32();

                // ★ ATOMIC SENSITIVE + ENABLED=FALSE: one INSERT sets sensitive inline and enabled hard-coded
                //   false; ON CONFLICT (the partial index) makes a re-apply idempotent (updates git-auth only).
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $@"INSERT INTO checks (source_key, name, kind, target_url, flow_name, sensitive, redact_patterns, interval_seconds, enabled)
                       VALUES ({src}, {name}, {kind}, {url}, {flow}, {sensitive}, {redact}::jsonb, {interval}, false)
                       ON CONFLICT (source_key) WHERE source_key IS NOT NULL DO UPDATE SET
                         name = EXCLUDED.name, kind = EXCLUDED.kind, target_url = EXCLUDED.target_url,
                         flow_name = EXCLUDED.flow_name, sensitive = EXCLUDED.sensitive, redact_patterns = EXCLUDED.redact_patterns", ct);

                // The check now exists (same txn/connection) — read its id by the unique source_key to seed locations.
                long newId = await _db.Checks.AsNoTracking().Where(c => c.SourceKey == src).Select(c => c.Id).FirstAsync(ct);

                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $@"INSERT INTO check_locations (check_id, location)
                       SELECT {newId}, name FROM locations WHERE enabled
                       ON CONFLICT (check_id, location) DO NOTHING", ct);

                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $@"UPDATE reconcile_apply_plan SET status = 'applied', applied_at = now()
                       WHERE source_key = {p.SourceKey} AND drift_type = 'new' AND status = 'approved'", ct);

                await tx.CommitAsync(ct);
                applied.Add(p.SourceKey);
            }
            catch
            {
                await tx.RollbackAsync(ct); // ★ never half-apply — the plan stays 'approved' for retry.
                failed.Add(p.SourceKey);
            }
        }
        return ApiResults.Ok(new ReconcileApplyResultDto(applied, failed, ApplyCap));
    }

    // The runner-written plan jsonb shape (only the bits the executor reads).
    private sealed record PlanDoc(List<PlanStmt>? Statements);
    private sealed record PlanStmt(string? Text, List<JsonElement>? Values, List<string>? Regions);

    // Parse runner-written jsonb (read as text) defensively — a malformed value degrades to {}, never throws.
    private static JsonElement SafeJson(string? json)
    {
        try { return JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(json) ? "{}" : json); }
        catch (JsonException) { return JsonSerializer.Deserialize<JsonElement>("{}"); }
    }
}
