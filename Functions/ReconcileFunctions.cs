using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
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

    private readonly IAuthPrincipal? _auth;

    // _auth optional for test convenience only — DI always injects it; the read gate is inert flag-off.
    private readonly ILogger<ReconcileFunctions>? _logger;

    public ReconcileFunctions(SynthWatchDbContext db, IRunnerJobTrigger trigger, IOptions<RunnerJobOptions> jobOptions,
        IAuthPrincipal? auth = null, ILogger<ReconcileFunctions>? logger = null)
    {
        _db = db;
        _trigger = trigger;
        _jobOptions = jobOptions.Value;
        _auth = auth;
        _logger = logger;
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
        // Session-gated read (#154 pattern): drift detail carries verbatim before/after config diffs.
        if (await SessionReadGate.RequireSessionAsync(_auth, req, ct) is { } denied)
            return denied;

        var rows = await _db.ReconcileDrift.FromSql(
            $@"SELECT source_key, drift_type, detail::text AS detail, detected_at
               FROM reconcile_drift
               ORDER BY drift_type, source_key").AsNoTracking().ToListAsync(ct);

        var items = rows.Select(r =>
            new ReconcileDriftItemDto(r.SourceKey, r.DriftType, SafeJson(r.Detail, _logger, r.SourceKey), r.DetectedAt)).ToList();

        // The latest detected_at = when the last reconcile ran; null when there's no drift.
        DateTimeOffset? detectedAt = rows.Count == 0 ? null : rows.Max(r => r.DetectedAt);

        // Session-gated response: never publicly cacheable (a shared cache must not serve one caller's
        // gated body to another; the old public,max-age=30 predates the gate).
        req.HttpContext.Response.Headers.CacheControl = "no-store";
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
        // Session-gated read (#154 pattern): the plan carries the verbatim runner-emitted SQL (statement
        // text + values, incl. redact-pattern config) that /reconcile/apply executes.
        if (await SessionReadGate.RequireSessionAsync(_auth, req, ct) is { } denied)
            return denied;

        var rows = await _db.ReconcileApplyPlan.FromSql(
            $@"SELECT source_key, drift_type, status, plan::text AS plan, computed_at
               FROM reconcile_apply_plan
               ORDER BY status, drift_type, source_key").AsNoTracking().ToListAsync(ct);

        var items = rows.Select(r =>
            new ReconcileApplyPlanItemDto(r.SourceKey, r.DriftType, r.Status, SafeJson(r.Plan, _logger, r.SourceKey), r.ComputedAt)).ToList();

        DateTimeOffset? computedAt = rows.Count == 0 ? null : rows.Max(r => r.ComputedAt);

        // Session-gated response: never publicly cacheable (see GetReconcileDrift).
        req.HttpContext.Response.Headers.CacheControl = "no-store";
        return ApiResults.Ok(new ReconcileApplyPlanDto(items, computedAt));
    }

    // ───────────────────────────────────────────────────────────────────────────────────────────────
    // Reconcile-apply PHASE 1 — approve / reject / APPLY. The middleware editor-gates + audits every POST.
    // ★ This is the first thing that writes LIVE monitor config — safety leads (see the per-method ★ notes).
    // ───────────────────────────────────────────────────────────────────────────────────────────────

    private const int ApplyCap = 5; // ★ max plans applied per call — one buggy apply can't rewrite the fleet.

    // ★ The drift_types ApplyReconcilePlans can actually EXECUTE — the SINGLE source of truth for BOTH the
    // apply WHERE filter (`drift_type = ANY(...)`) and the approve gate, so an operator can't approve a plan
    // apply would silently ignore. 'new' = materialize; 'missing' = soft-disable; 'changed' = reconverge the
    // drifted NON-redaction git-auth fields (redaction stays on the redaction_mismatch path — the runner emits
    // a redaction-EXCLUDED statement, PR2a, and ApplyChangedAsync's shape-guard refuses one that isn't). Widen
    // ONLY when a per-type branch is wired in the loop below.
    private static readonly HashSet<string> ApplyExecutableDriftTypes = new(StringComparer.Ordinal) { "new", "missing", "changed" };

    /// <summary>POST /api/reconcile/approve — pending → approved. ★ A 'blocked' plan (a redaction strip) can
    /// NEVER be approved (409); nor can a drift_type apply can't yet execute (changed/missing) — that would be
    /// an indefinite silent no-op at apply time. Body {sourceKey, driftType}.</summary>
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
        var (body, bodyError) = await RequestJson.ReadAsync<ReconcileDecisionRequest>(req, ct);
        if (bodyError is not null) return bodyError;
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
        // ★ Mirror that fail-safe for executability: only drift_types apply can EXECUTE may be APPROVED. Approving
        // a non-executable type (changed/missing) would move it to 'approved' where apply (WHERE drift_type='new')
        // silently ignores it forever — an indefinite no-op. Honest 409 now beats a silent swallow later. Reject
        // is always allowed (you can reject anything that can't be applied).
        if (decision == "approved" && !ApplyExecutableDriftTypes.Contains(current.DriftType))
            return ApiResults.Conflict(
                $"drift_type '{current.DriftType}' is not yet executable by apply (only 'new' is). Reject it, or wait for the executor to support it.");
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

    /// <summary>POST /api/reconcile/apply — execute the APPROVED plans (cap 5). ★ Each plan in ONE transaction:
    /// 'new' materializes the check (sensitive INLINE, enabled=FALSE) + seeds check_locations; 'missing'
    /// SOFT-DISABLES it (enabled=false, never delete — history preserved); 'changed' reconverges the drifted
    /// non-redaction git-auth field(s) (redaction-EXCLUDED — the runner scopes it, PR2a). The write + the
    /// plan→'applied' flip commit together; ROLLBACK on any error (the plan stays 'approved', re-appliable).
    /// Executable types are ApplyExecutableDriftTypes.</summary>
    [Function("ApplyReconcilePlans")]
    public async Task<IActionResult> ApplyReconcilePlans(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "reconcile/apply")] HttpRequest req,
        CancellationToken ct)
    {
        var executable = ApplyExecutableDriftTypes.ToArray();
        var approved = await _db.ReconcileApplyPlan.FromSql(
            $@"SELECT source_key, drift_type, status, plan::text AS plan, computed_at
               FROM reconcile_apply_plan WHERE status = 'approved' AND drift_type = ANY({executable})
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
                if (p.DriftType == "missing")
                {
                    // ★ MISSING — a Git-deleted spec. SOFT-DISABLE only (enabled=false), executed AS-EMITTED
                    //   from the plan. NEVER a delete: the check row + its runs/incidents/history survive, so a
                    //   re-added spec can re-enable it later.
                    await ApplyMissingAsync(doc, tx, ct);
                }
                else if (p.DriftType == "changed")
                {
                    // ★ CHANGED — reconverge the drifted NON-redaction git-auth fields to the manifest, executed
                    //   AS-EMITTED (PR2a's scoped, redaction-EXCLUDED UPDATE). Redaction (sensitive/redact_patterns)
                    //   belongs to the redaction_mismatch path; the shape-guard REFUSES any statement touching it.
                    await ApplyChangedAsync(doc, tx, ct);
                }
                else
                {
                // === 'new' — materialize (unchanged) ===
                var v = doc?.Statements?.FirstOrDefault()?.Values;
                if (v is null || v.Count < 9) throw new InvalidOperationException("plan has no materialize values");
                string src = v[0].GetString()!, name = v[1].GetString()!, kind = v[2].GetString()!,
                       url = v[3].GetString()!, flow = v[4].GetString()!, redact = v[6].GetString()!;
                bool sensitive = v[5].GetBoolean();
                int interval = v[7].GetInt32();
                // ★ spec_path (plan value index 9) — the REAL runtime resolution path for a browser check: the
                //   runner fetches + compiles this Git spec. WITHOUT it a browser check falls back to
                //   loadFlow(flow_name) -> a baked-in dist/checks/<name>.js a manifest monitor never has, so it
                //   can ONLY fail ("Cannot find module"). Older plans (pre-spec_path) lack v[9] -> null.
                string? specPath = v.Count > 9 ? v[9].GetString() : null;
                // ★ FAIL-CLOSED gate: never materialize a browser check with no spec_path — it could only fail.
                //   Throw -> the txn rolls back, the plan stays 'approved', and the next reconcile recomputes a
                //   plan that carries spec_path. (The runner's computeApplyPlan blocks this upstream too.)
                if (kind == "browser" && string.IsNullOrEmpty(specPath))
                    throw new InvalidOperationException(
                        "browser check plan has no spec_path; refusing to materialize an unresolvable check");

                // ★ ATOMIC SENSITIVE + ENABLED=FALSE: one INSERT sets sensitive inline and enabled hard-coded
                //   false; ON CONFLICT (the partial index) makes a re-apply idempotent (updates git-auth +
                //   spec_path, so a row materialized before this fix self-heals from NULL -> the manifest spec).
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $@"INSERT INTO checks (source_key, name, kind, target_url, flow_name, sensitive, redact_patterns, interval_seconds, enabled, spec_path)
                       VALUES ({src}, {name}, {kind}, {url}, {flow}, {sensitive}, {redact}::jsonb, {interval}, false, {specPath})
                       ON CONFLICT (source_key) WHERE source_key IS NOT NULL DO UPDATE SET
                         name = EXCLUDED.name, kind = EXCLUDED.kind, target_url = EXCLUDED.target_url,
                         flow_name = EXCLUDED.flow_name, sensitive = EXCLUDED.sensitive, redact_patterns = EXCLUDED.redact_patterns,
                         spec_path = EXCLUDED.spec_path", ct);

                // The check now exists (same txn/connection) — read its id by the unique source_key to seed locations.
                long newId = await _db.Checks.AsNoTracking().Where(c => c.SourceKey == src).Select(c => c.Id).FirstAsync(ct);

                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $@"INSERT INTO check_locations (check_id, location)
                       SELECT {newId}, name FROM locations WHERE enabled
                       ON CONFLICT (check_id, location) DO NOTHING", ct);
                }

                // ★ plan → applied (generic drift_type — both 'new' materialize and 'missing' soft-disable),
                //   in the SAME txn, so an apply is atomic: the write + the status flip commit together.
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $@"UPDATE reconcile_apply_plan SET status = 'applied', applied_at = now()
                       WHERE source_key = {p.SourceKey} AND drift_type = {p.DriftType} AND status = 'approved'", ct);

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

    /// <summary>
    /// Apply a 'missing' plan: SOFT-DISABLE the check (enabled=false) by executing the plan's statement
    /// AS-EMITTED by the runner (plan-as-contract — not hand-constructed). ★ Guarded to the single soft-disable
    /// UPDATE by source_key: if the plan isn't exactly that (or contains a delete), REFUSE (throw → the caller
    /// rolls back). A 'missing' apply must NEVER delete the check — the row + its runs/incidents/history are
    /// preserved so a re-added spec can re-enable it later.
    /// </summary>
    private async Task ApplyMissingAsync(PlanDoc? doc, IDbContextTransaction tx, CancellationToken ct)
    {
        var stmt = doc?.Statements?.FirstOrDefault();
        var text = stmt?.Text;
        var normalized = text is null ? null : new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        // ★ Contract: exactly ONE statement — a soft-disable UPDATE by source_key with one value, never a delete.
        if (text is null || stmt!.Values is not { Count: 1 } vals || normalized is null
            || !normalized.Contains("UPDATEchecksSETenabled=falseWHEREsource_key=$1", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("DELETE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "missing plan is not the expected single soft-disable statement — refusing to apply.");

        // Execute the runner-emitted statement verbatim ($1 = source_key from the plan values), on this txn.
        var conn = _db.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = text;
        cmd.Transaction = tx.GetDbTransaction();
        var pr = cmd.CreateParameter();
        pr.Value = vals[0].GetString() ?? (object)DBNull.Value;
        cmd.Parameters.Add(pr);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Apply a 'changed' plan: RECONVERGE the drifted non-redaction git-authoritative field(s) to the manifest
    /// by executing the runner's scoped statement AS-EMITTED (plan-as-contract — PR2a emits a redaction-EXCLUDED
    /// `UPDATE checks SET … WHERE source_key = $1`). ★ STRIP-SAFETY shape-guard (defense-in-depth over PR2a): the
    /// statement MUST be a scoped update by source_key whose SET NEVER touches sensitive/redact_patterns (that's
    /// the redaction_mismatch path's job, with the strip allowance) and is never a delete — else REFUSE (throw →
    /// the caller rolls back). So even a strip-bypassing statement that somehow reached the API can't execute.
    /// </summary>
    private async Task ApplyChangedAsync(PlanDoc? doc, IDbContextTransaction tx, CancellationToken ct)
    {
        var stmt = doc?.Statements?.FirstOrDefault();
        var text = stmt?.Text;
        var normalized = text is null ? null : new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        // ★ Contract + STRIP-SAFETY: a scoped `UPDATE checks SET … WHERE source_key = $1` that NEVER touches the
        //   redaction columns and is never a delete. Anything else → refuse.
        if (text is null || stmt!.Values is not { Count: > 0 } vals || normalized is null
            || !normalized.StartsWith("UPDATEchecksSET", StringComparison.OrdinalIgnoreCase)
            || !normalized.Contains("WHEREsource_key=$1", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("sensitive", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("redact_patterns", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("DELETE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "changed plan is not the expected scoped, redaction-excluded UPDATE — refusing to apply.");

        // Execute the runner-emitted statement verbatim ($1 = source_key, then the drifted values + spec_path).
        var conn = _db.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = text;
        cmd.Transaction = tx.GetDbTransaction();
        foreach (var val in vals)
        {
            var pr = cmd.CreateParameter();
            pr.Value = val.ValueKind == JsonValueKind.Null ? DBNull.Value : (object?)val.GetString() ?? DBNull.Value;
            cmd.Parameters.Add(pr);
        }
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // The runner-written plan jsonb shape (only the bits the executor reads).
    private sealed record PlanDoc(List<PlanStmt>? Statements);
    private sealed record PlanStmt(string? Text, List<JsonElement>? Values, List<string>? Regions);

    // Parse runner-written jsonb (read as text) defensively — a malformed value degrades to {}, never throws.
    private static JsonElement SafeJson(string? json, ILogger? logger, string? sourceKey)
    {
        try { return JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(json) ? "{}" : json); }
        catch (JsonException)
        {
            // Malformed runner-written jsonb → degrade to {} (unchanged); log so a corrupt stored row is visible.
            if (logger is not null) ReconcileLog.MalformedPlanJson(logger, sourceKey ?? "(unknown)");
            return JsonSerializer.Deserialize<JsonElement>("{}");
        }
    }
}

/// <summary>High-performance (CA1848) log delegates for ReconcileFunctions tolerance paths.</summary>
internal static partial class ReconcileLog
{
    [LoggerMessage(EventId = 7003, Level = LogLevel.Information,
        Message = "malformed runner-written reconcile jsonb for source_key {SourceKey} — degraded to {{}} (corrupt stored row)")]
    public static partial void MalformedPlanJson(ILogger logger, string sourceKey);
}
