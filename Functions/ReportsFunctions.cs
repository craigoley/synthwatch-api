using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SynthWatch.Api.Data;
using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;

namespace SynthWatch.Api.Functions;

/// <summary>
/// Reporting Layer 2 (reads the daily_check_rollup from runner #88). Report endpoints grouped by a tag
/// key (or ungrouped) over a window. ★ AVAILABILITY aggregates ADDITIVELY from the daily rollup counts
/// (up/down sum cleanly). ★ Multi-day LATENCY + WEB-VITALS percentiles are RECOMPUTED FROM RAW runs over
/// the window (percentiles are NOT averageable across days — #88's non-negotiable), using the exact same
/// filter the rollup uses (UP runs = pass|warn, running-excluded, maintenance-window anti-join). The
/// daily trend series come from the rollup (availability counts; count-weighted avg latency). Read-only.
/// </summary>
public class ReportsFunctions
{
    private readonly SynthWatchDbContext _db;
    private readonly ILogger<ReportsFunctions>? _logger;

    public ReportsFunctions(SynthWatchDbContext db, ILogger<ReportsFunctions>? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    private static int? WindowDays(string w) => w switch
    {
        "" or "30d" => 30,
        "7d" => 7,
        "90d" => 90,
        _ => null,
    };

    /// <summary>
    /// GET /api/reports/deploys?host=&amp;window=7d|30d|90d — auto-detected deploy markers for a host over the
    /// window (deploy-markers v1), for overlaying ReferenceLines on the time-series charts. Read-only.
    /// ★ Tolerates the deploys table not being migrated yet (merged ≠ migrated, #114) → serves empty, never 500.
    /// </summary>
    [Function("GetDeploysReport")]
    public async Task<IActionResult> GetDeploysReport(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "reports/deploys")] HttpRequest req,
        CancellationToken ct)
    {
        var host = req.Query["host"].ToString().Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(host)) return ApiResults.BadRequest("host is required.");
        var window = req.Query["window"].ToString();
        if (WindowDays(window) is not int days) return ApiResults.BadRequest("window must be one of: 7d, 30d, 90d.");
        var normWindow = string.IsNullOrEmpty(window) ? "30d" : window;

        List<DeployRow> rows;
        try
        {
            rows = await _db.Deploys.FromSql(
                $@"SELECT target_host, sha, fingerprint, is_sha, source, deployed_at
                   FROM deploys
                   WHERE target_host = {host} AND deployed_at >= now() - ({days} * INTERVAL '1 day')
                   ORDER BY deployed_at").AsNoTracking().ToListAsync(ct);
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42P01")
        {
            // Serve empty (not a 500) — breadcrumb so a persistently-empty deploys report is one grep, not a mystery.
            if (_logger is not null) DeploysReportLog.DeploysTableAbsent(_logger, host);
            rows = new(); // deploys not migrated yet (runner PR deploys first) — serve empty, not a 500.
        }

        // sha only when it's a real commit id; the UI labels a non-sha marker "deploy detected (no commit id)".
        var deploys = rows
            .Select(r => new DeployMarkerDto(r.IsSha ? r.Sha : null, r.IsSha, r.Source, r.DeployedAt))
            .ToList();
        req.HttpContext.Response.Headers.CacheControl = "public, max-age=30";
        req.HttpContext.Response.Headers["Vary"] = "Origin";
        return ApiResults.Ok(new DeploysReportDto(host, normWindow, deploys));
    }

    /// <summary>
    /// GET /api/reports/egress?window=all|24h (default all) — per-region egress-IP stability from runs.egress_ip
    /// (0054), for the status-page egress panel (the Wegmans allowlist artifact + a live SNAT-rotation warning).
    /// Read-only, Anonymous GET. ★ The NULL filter (egress_ip IS NOT NULL) is the correctness point; a region's
    /// 2nd+ IP is SURFACED, never deduped (distinctCount &gt; 1 = a rotation, the reason the panel exists).
    /// </summary>
    [Function("GetEgressReport")]
    public async Task<IActionResult> GetEgressReport(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "reports/egress")] HttpRequest req,
        CancellationToken ct)
    {
        var window = req.Query["window"].ToString() switch
        {
            "" or "all" => "all",
            "24h" => "24h",
            _ => null,
        };
        if (window is null) return ApiResults.BadRequest("window must be one of: all, 24h.");

        // {window} is parameterized by FromSql (not interpolated) — `{window} = 'all'` short-circuits the time
        // filter; otherwise only runs from the last day count. NULL egress_ip is excluded (the correctness point).
        var rows = await _db.EgressRuns.FromSql(
            $@"SELECT location, egress_ip AS ip, count(*) AS run_count,
                      min(started_at) AS first_seen, max(started_at) AS last_seen
               FROM runs
               WHERE egress_ip IS NOT NULL
                 AND ({window} = 'all' OR started_at > now() - interval '1 day')
               GROUP BY location, egress_ip
               ORDER BY location, first_seen").AsNoTracking().ToListAsync(ct);

        var dto = EgressReportProjection.Build(window, rows);
        req.HttpContext.Response.Headers.CacheControl = "public, max-age=60";
        req.HttpContext.Response.Headers["Vary"] = "Origin";
        return ApiResults.Ok(dto);
    }

    /// <summary>
    /// GET /api/reports/region-health — per-region freshness so a SILENTLY-DEAD region becomes visible (F-4: a
    /// dead region stops writing runs, so quorum degrades gracefully and therefore INVISIBLY; max(started_at)
    /// per region going stale IS the signal). Expected regions are DECLARATIVE (locations WHERE enabled), so a
    /// configured-but-dark region still appears (stale/never_reported), never silently drops out. Freshness =
    /// MAX(check_locations.last_run_at), which the runner advances at CLAIM time on every run (pass OR fail) —
    /// a pure liveness signal, PK-indexed + fleet-sized, so NO scan of the runs table. Read-only, Anonymous GET
    /// (the /reports/* anon tier post-#162 — only channels / reconcile plan+drift / forensic artifacts are session-gated).
    /// </summary>
    [Function("GetRegionHealth")]
    public async Task<IActionResult> GetRegionHealth(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "reports/region-health")] HttpRequest req,
        CancellationToken ct)
    {
        // One row per ENABLED region. LEFT JOIN so an enabled location with NO check_locations rows (or only
        // never-claimed cursors → all last_run_at NULL) still yields a row with max=NULL → never_reported,
        // never dropped and never fabricated. Aggregate over the PK-indexed, fleet-sized check_locations
        // (one row per check-location pair), NOT a max() scan over the 20k+ runs table.
        var rows = await _db.RegionHealth.FromSql(
            $@"SELECT l.name AS location, max(cl.last_run_at) AS last_run_at
               FROM locations l
               LEFT JOIN check_locations cl ON cl.location = l.name
               WHERE l.enabled
               GROUP BY l.name
               ORDER BY l.name").AsNoTracking().ToListAsync(ct);

        // The staleness threshold keys off the fleet's MIN enabled check interval (× a named-constant
        // multiplier). coalesce to 300 so an empty fleet degrades to the interval floor rather than NULL.
        var minInterval = await _db.Checks.Where(c => c.Enabled).MinAsync(c => (int?)c.IntervalSeconds, ct) ?? 300;

        var dto = RegionHealthProjection.Build(minInterval, rows, DateTimeOffset.UtcNow);
        req.HttpContext.Response.Headers.CacheControl = "public, max-age=30";
        req.HttpContext.Response.Headers["Vary"] = "Origin";
        return ApiResults.Ok(dto);
    }

    /// <summary>
    /// Parse the repeatable <c>?tag=key:value</c> filter into a normalized "key:value"[] (distinct, trimmed).
    /// Empty array = no filter. The query clause below ANDs across all selected tags (a check must carry EVERY
    /// one) — mirrors the dashboard's multi-select-AND tag filter. Passed as a single text[] param so the same
    /// no-op-when-empty clause drops into every report query (cardinality=0 → the guard short-circuits true).
    /// </summary>
    private static string[] TagFilter(HttpRequest req) =>
        req.Query["tag"]
            .Where(t => !string.IsNullOrWhiteSpace(t) && t!.Contains(':'))
            .Select(t => t!.Trim())
            .Distinct()
            .ToArray();

    private const string Unclassified = "unclassified";
    private const string RealOutage = "real-outage";

    /// <summary>
    /// GET /api/reports/slo?window=7d|30d|90d&amp;tag=key:value (repeatable, AND) — fleet + per-check error
    /// BUDGET over the window. Only checks WITH an slo_target appear (opt-in; slo_status returns no row
    /// otherwise, so the LATERAL drops them). One CROSS JOIN LATERAL slo_status(c.id, from, to) per SLO
    /// check — reuses the per-check function, NO fleet SQL function. Run-weighted ADDITIVE fleet rollup +
    /// insufficientData honesty (see SloReportProjection). Read-only (stays open per the GET default).
    /// ★ No fast/slow-burn pills — pooled burn false-pages; location-aware burn is a follow-up PR.
    /// </summary>
    [Function("GetSloReport")]
    public async Task<IActionResult> GetSloReport(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "reports/slo")] HttpRequest req,
        CancellationToken ct)
    {
        var window = req.Query["window"].ToString();
        if (WindowDays(window) is not int days)
            return ApiResults.BadRequest("window must be one of: 7d, 30d, 90d.");
        var tags = TagFilter(req);
        var to = DateTimeOffset.UtcNow;
        var from = to - TimeSpan.FromDays(days);

        // One row per SLO-having check: LATERAL slo_status(...) gives budget/consumed/remaining/burn; the
        // tag clause ANDs across selected tags (no-op when none) — the same idiom as the other reports.
        var rows = await _db.SloReport.FromSql(
            $@"SELECT c.id AS check_id, c.name AS check_name, c.kind AS kind,
                      s.slo_target, s.total_runs, s.down_runs, s.budget, s.consumed, s.remaining, s.remaining_pct, s.burn_rate,
                      b.burn_state, b.reported_burn
               FROM checks c
               CROSS JOIN LATERAL slo_status(c.id, {from}, {to}) s
               -- ★ P5 PR2: the LOCATION-AWARE burn STATE — the SAME slo_burn_status the runner pages on
               -- (read == page). Its own fixed 1h/6h/30m windows (NOT the report window), so no from/to args.
               CROSS JOIN LATERAL slo_burn_status(c.id) b
               WHERE c.slo_target IS NOT NULL
                 -- ★ Pre-prod default-EXCLUDE (arc S1c): a non-prod check never enters the prod SLO/error-budget
                 -- fleet. coalesce() so a row written before checks.environment existed still reads as prod.
                 AND coalesce(c.environment, 'prod') = 'prod'
                 AND (cardinality({tags}) = 0 OR c.id IN (
                       SELECT ft.check_id FROM check_tags ft
                       WHERE ft.key || ':' || ft.value = ANY({tags})
                       GROUP BY ft.check_id HAVING count(DISTINCT ft.key || ':' || ft.value) = cardinality({tags})))
               ORDER BY c.name").AsNoTracking().ToListAsync(ct);

        var projection = SloReportProjection.Build(rows);
        req.HttpContext.Response.Headers.CacheControl = "public, max-age=30";
        req.HttpContext.Response.Headers["Vary"] = "Origin";
        return ApiResults.Ok(new SloReportResponseDto(
            Window: string.IsNullOrEmpty(window) ? "30d" : window,
            Fleet: projection.Fleet,
            Items: projection.Items));
    }

    /// <summary>
    /// GET /api/reports/cost — ESTIMATED monthly ACA compute cost per monitor + fleet (recon #220; reproduces
    /// #229's ~$67/mo). projected = avg_duration_s × (2,592,000/interval) × region_count × rate; measured =
    /// (7d Σduration_ms/1000) × rate × 30/7. ★ The rate is a CONFIG value (COST_RATE_* env vars, deploy-free),
    /// ECHOED in the response (rateUsed/rateSource/rateSetDate) so every figure is self-describing — an
    /// ESTIMATE, not billed truth. Anonymous GET, matching the other /reports (cost is no more sensitive than
    /// SLO). ★ Pre-prod INCLUDED (unlike SLO's prod-only filter) — a staging monitor is real spend.
    /// divergence_ratio &gt; 1.5 per check flags retry-amplification / a failing flow.
    /// </summary>
    [Function("GetCostReport")]
    public async Task<IActionResult> GetCostReport(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "reports/cost")] HttpRequest req,
        CancellationToken ct)
    {
        // One row per ENABLED check: region_count from check_locations, avg/Σ duration (float SECONDS) over the
        // last 7d. Casts pin the CLR types (int region_count, float8 seconds); the $ math is applied in C#.
        var rows = await _db.CostReport.FromSql(
            $@"SELECT c.id AS check_id, c.source_key AS source_key, c.name AS check_name, c.kind AS kind,
                      c.interval_seconds AS interval_seconds,
                      (SELECT count(*)::int FROM check_locations cl WHERE cl.check_id = c.id) AS region_count,
                      ((SELECT avg(r.duration_ms) FROM runs r
                          WHERE r.check_id = c.id AND r.started_at > now() - interval '7 days'
                            AND r.duration_ms IS NOT NULL) / 1000.0)::float8 AS avg_duration_s,
                      ((SELECT sum(r.duration_ms) FROM runs r
                          WHERE r.check_id = c.id AND r.started_at > now() - interval '7 days'
                            AND r.duration_ms IS NOT NULL) / 1000.0)::float8 AS sum_duration_s_7d
               FROM checks c
               WHERE c.enabled
               ORDER BY c.name").AsNoTracking().ToListAsync(ct);

        var dto = CostReportProjection.Build(
            rows, CostRate.PerVcpuSecond, CostRate.Source, CostRate.SetDate, DateTimeOffset.UtcNow);
        req.HttpContext.Response.Headers.CacheControl = "public, max-age=60";
        req.HttpContext.Response.Headers["Vary"] = "Origin";
        return ApiResults.Ok(dto);
    }

    /// <summary>
    /// GET /api/reports/mttr?window=7d|30d|90d&amp;tag=key:value (repeatable, AND) — fleet + per-check incident
    /// analytics: MTTR (mean + median time-to-resolve over RESOLVED incidents), the rca.classification
    /// breakdown (unclassified shown), a detection-lag PROXY (consecutive_failures × interval), and an MTTR
    /// trend. ★ Open incidents are EXCLUDED from the durations but COUNTED; too-few-resolved → null, never a
    /// fabricated time. Pure math in <see cref="MttrReportProjection"/>. Read-only (stays open per the GET default).
    /// </summary>
    [Function("GetMttrReport")]
    public async Task<IActionResult> GetMttrReport(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "reports/mttr")] HttpRequest req,
        CancellationToken ct)
    {
        var window = req.Query["window"].ToString();
        if (WindowDays(window) is not int days)
            return ApiResults.BadRequest("window must be one of: 7d, 30d, 90d.");
        var tags = TagFilter(req);

        // One row per incident opened in the window (+ its check), resolved OR open. The tag clause ANDs
        // across selected tags via the incident's check_id — the same idiom as the other reports.
        var rows = await _db.MttrIncidents.FromSql(
            $@"SELECT i.check_id, c.name AS check_name, c.kind AS kind, i.status,
                      i.opened_at, i.resolved_at,
                      coalesce(i.rca->>'classification', {Unclassified}) AS classification,
                      i.consecutive_failures, c.interval_seconds
               FROM incidents i
               JOIN checks c ON c.id = i.check_id
               WHERE i.opened_at >= now() - ({days} * INTERVAL '1 day')
                 -- ★ Pre-prod default-EXCLUDE (arc S1c): a non-prod check's incidents never enter the prod MTTR.
                 AND coalesce(c.environment, 'prod') = 'prod'
                 AND (cardinality({tags}) = 0 OR i.check_id IN (
                       SELECT ft.check_id FROM check_tags ft
                       WHERE ft.key || ':' || ft.value = ANY({tags})
                       GROUP BY ft.check_id HAVING count(DISTINCT ft.key || ':' || ft.value) = cardinality({tags})))
               ORDER BY i.opened_at").AsNoTracking().ToListAsync(ct);

        var p = MttrReportProjection.Build(rows, days);
        req.HttpContext.Response.Headers.CacheControl = "public, max-age=30";
        req.HttpContext.Response.Headers["Vary"] = "Origin";
        return ApiResults.Ok(new MttrReportResponseDto(
            Window: string.IsNullOrEmpty(window) ? "30d" : window,
            Fleet: p.Fleet, Items: p.Items, Classification: p.Classification, Trend: p.Trend));
    }

    /// <summary>
    /// GET /api/reports/incident-breakdown?window=7d|30d|90d&tag=key:value (repeatable, AND) — the verdict-taxonomy breakdown over the window:
    /// count per rca.classification (real-outage | flaky-transient | selector-drift | environment-regional |
    /// perf-regression), an explicit <c>unclassified</c> bucket (incidents with no RCA yet — never dropped),
    /// and the ALERT-PRECISION headline = real-outage / classified. One GROUP BY over incidents.rca.
    /// </summary>
    [Function("GetIncidentBreakdown")]
    public async Task<IActionResult> GetIncidentBreakdown(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "reports/incident-breakdown")] HttpRequest req,
        CancellationToken ct)
    {
        var window = req.Query["window"].ToString();
        if (WindowDays(window) is not int days) return ApiResults.BadRequest("window must be one of: 7d, 30d, 90d.");
        var normWindow = string.IsNullOrEmpty(window) ? "30d" : window;
        var tags = TagFilter(req);

        // GROUP BY the rca classification over incidents OPENED in the window. coalesce → an explicit
        // "unclassified" bucket so incidents with no RCA classification are counted, never silently dropped.
        // Tag filter: restrict to incidents whose CHECK carries all selected tags (no-op when none selected).
        var rows = await _db.IncidentBreakdown.FromSql(
            $@"SELECT coalesce(rca->>'classification', {Unclassified}) AS classification, count(*)::bigint AS count
               FROM incidents
               WHERE opened_at >= now() - ({days} * INTERVAL '1 day')
                 AND (cardinality({tags}) = 0 OR check_id IN (
                       SELECT ft.check_id FROM check_tags ft
                       WHERE ft.key || ':' || ft.value = ANY({tags})
                       GROUP BY ft.check_id HAVING count(DISTINCT ft.key || ':' || ft.value) = cardinality({tags})))
               GROUP BY 1").AsNoTracking().ToListAsync(ct);

        var total = rows.Sum(r => r.Count);
        var unclassified = rows.Where(r => r.Classification == Unclassified).Sum(r => r.Count);
        var classified = total - unclassified;
        var realOutages = rows.Where(r => r.Classification == RealOutage).Sum(r => r.Count);
        // ALERT PRECISION = genuine real-outages / CLASSIFIED reds (not /total — unclassified can't be judged).
        // null when nothing is classified yet — an honest empty, not a misleading 0%.
        decimal? precision = classified > 0 ? Math.Round((decimal)realOutages / classified, 4) : null;

        var buckets = rows
            .Select(r => new IncidentBreakdownBucketDto(
                r.Classification ?? Unclassified, r.Count,
                total > 0 ? Math.Round((decimal)r.Count / total, 4) : 0m))
            // count desc, but the unclassified "unknown" bucket always last.
            .OrderBy(b => b.Classification == Unclassified).ThenByDescending(b => b.Count)
            .ToList();

        return ApiResults.Ok(new IncidentBreakdownDto(
            normWindow, total, classified, unclassified, realOutages, precision, buckets));
    }

    /// <summary>
    /// §D1 GET /api/reports/trust?window=7d|30d|90d — the monitor-trust scorecard ("every green shown with
    /// its proof"): one row per ENABLED check. ★ NO synthesized 0-100 score — each field is a measured fact
    /// with a sample size, and <c>trust</c> is a chip derived from STATED, AUDITABLE rules (TrustReportProjection).
    /// One SQL statement joining runs (retry + last-green aggregates), incidents (RCA verdict counts), and the
    /// latest run's spec provenance. ★ redTest.captured is a hard false — a visible v2 contract slot, never faked.
    /// Read-only.
    /// </summary>
    [Function("GetTrustReport")]
    public async Task<IActionResult> GetTrustReport(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "reports/trust")] HttpRequest req,
        CancellationToken ct)
    {
        var window = req.Query["window"].ToString();
        if (WindowDays(window) is not int days) return ApiResults.BadRequest("window must be one of: 7d, 30d, 90d.");
        var normWindow = string.IsNullOrEmpty(window) ? "30d" : window;

        var rows = await _db.TrustMonitors.FromSql(TrustFleetSql(days)).AsNoTracking().ToListAsync(ct);

        var asOf = DateTimeOffset.UtcNow;
        var monitors = rows.Select(r => TrustReportProjection.ToDto(r, asOf)).ToList();

        req.HttpContext.Response.Headers.CacheControl = "public, max-age=30";
        req.HttpContext.Response.Headers["Vary"] = "Origin";
        return ApiResults.Ok(new TrustReportDto(normWindow, monitors));
    }

    /// <summary>
    /// §D1 GET /api/reports/trust/{checkId}?window=7d|30d|90d — one monitor's trust row (same shape as the
    /// fleet) plus its daily retry-rate series for the detail sparkline. 404 when the check does not exist.
    /// Read-only.
    /// </summary>
    [Function("GetTrustMonitorDetail")]
    public async Task<IActionResult> GetTrustMonitorDetail(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "reports/trust/{checkId:long}")] HttpRequest req,
        long checkId,
        CancellationToken ct)
    {
        var window = req.Query["window"].ToString();
        if (WindowDays(window) is not int days) return ApiResults.BadRequest("window must be one of: 7d, 30d, 90d.");
        var normWindow = string.IsNullOrEmpty(window) ? "30d" : window;

        var row = (await _db.TrustMonitors.FromSql(TrustDetailSql(days, checkId)).AsNoTracking().ToListAsync(ct))
            .FirstOrDefault();
        if (row is null) return ApiResults.NotFound($"Monitor {checkId} not found.");

        var series = await _db.TrustRetryDays.FromSql(
            $@"SELECT d.day::date AS day,
                      coalesce(rc.run_count, 0) AS run_count,
                      coalesce(rc.retry_count, 0) AS retry_count
               FROM generate_series((CURRENT_DATE - ({days} - 1))::date, CURRENT_DATE, INTERVAL '1 day') d(day)
               LEFT JOIN LATERAL (
                   SELECT count(*)::bigint AS run_count,
                          count(*) FILTER (WHERE r.retry_count > 1 /* attempt count (runner migration 0048): 1 = first try / NO retry; > 1 = an ACTUAL retry. > 0 would count every clean pass as retried. */)::bigint AS retry_count
                   FROM runs r
                   WHERE r.check_id = {checkId} AND r.started_at::date = d.day::date
               ) rc ON true
               ORDER BY d.day").AsNoTracking().ToListAsync(ct);

        var asOf = DateTimeOffset.UtcNow;
        var monitor = TrustReportProjection.ToDto(row, asOf);
        var points = series
            .Select(s => new TrustRetryPointDto(s.Day, s.RunCount, s.RetryCount,
                TrustReportProjection.RetryRate(s.RetryCount, s.RunCount)))
            .ToList();

        req.HttpContext.Response.Headers.CacheControl = "public, max-age=30";
        req.HttpContext.Response.Headers["Vary"] = "Origin";
        return ApiResults.Ok(new TrustMonitorDetailDto(normWindow, monitor, points));
    }

    // The trust-row SQL, shared by the fleet + detail endpoints. Per-check aggregates via LEFT JOIN LATERAL so
    // a check with zero runs / zero incidents still yields a row (coalesced to 0 — never dropped). last_green_at
    // stays NULL when the check never passed in the window (a first-class "never verified" state). The RCA
    // buckets ALL reconcile to incident_total (perf-regression is its own bucket; nothing counted goes unshown).
    // The taxonomy strings are the fixed runner enum (reconcile.ts), not user input — safe as SQL literals.
    private static FormattableString TrustFleetSql(int days) =>
        $@"SELECT c.id AS check_id, c.name AS check_name, c.sensitive AS sensitive,
                  c.interval_seconds AS interval_seconds, c.last_run_at AS last_run_at,
                  rc.last_green_at AS last_green_at,
                  coalesce(rc.run_count, 0) AS run_count, coalesce(rc.retry_count, 0) AS retry_count,
                  coalesce(rc.retried_passes, 0) AS retried_passes,
                  coalesce(ic.total, 0) AS incident_total,
                  coalesce(ic.real_outage, 0) AS real_outage,
                  coalesce(ic.flaky_transient, 0) AS flaky_transient,
                  coalesce(ic.selector_drift, 0) AS selector_drift,
                  coalesce(ic.environment_regional, 0) AS environment_regional,
                  coalesce(ic.perf_regression, 0) AS perf_regression,
                  coalesce(ic.unclassified, 0) AS unclassified,
                  sp.executed_sha256 AS executed_sha256, sp.spec_path AS spec_path,
                  rt.red_tested_at AS red_tested_at, rt.red_test_method AS red_test_method
           FROM checks c
           LEFT JOIN LATERAL (
               SELECT count(*)::bigint AS run_count,
                      count(*) FILTER (WHERE r.retry_count > 1 /* attempt count (runner migration 0048): 1 = first try / NO retry; > 1 = an ACTUAL retry. > 0 would count every clean pass as retried. */)::bigint AS retry_count,
                      -- ★ degrading-but-green early warning: a PASS/WARN run that STILL needed a real retry. A
                      -- DISPLAY-ONLY annotation — NEVER an input to DeriveChip (the #152 class must not recur).
                      -- Counted over the SAME window as retry_count.
                      count(*) FILTER (WHERE r.status IN ('pass','warn') AND r.retry_count > 1 /* attempt count (runner migration 0048): 1 = first try / NO retry; > 1 = an ACTUAL retry. > 0 would count every clean pass as retried. */)::bigint AS retried_passes,
                      max(r.started_at) FILTER (WHERE r.status = 'pass') AS last_green_at
               FROM runs r
               WHERE r.check_id = c.id AND r.started_at >= now() - ({days} * INTERVAL '1 day')
           ) rc ON true
           LEFT JOIN LATERAL (
               SELECT count(*)::bigint AS total,
                      count(*) FILTER (WHERE i.rca->>'classification' = 'real-outage')::bigint AS real_outage,
                      count(*) FILTER (WHERE i.rca->>'classification' = 'flaky-transient')::bigint AS flaky_transient,
                      count(*) FILTER (WHERE i.rca->>'classification' = 'selector-drift')::bigint AS selector_drift,
                      count(*) FILTER (WHERE i.rca->>'classification' = 'environment-regional')::bigint AS environment_regional,
                      count(*) FILTER (WHERE i.rca->>'classification' = 'perf-regression')::bigint AS perf_regression,
                      count(*) FILTER (WHERE i.rca->>'classification' IS NULL)::bigint AS unclassified
               FROM incidents i
               WHERE i.check_id = c.id AND i.opened_at >= now() - ({days} * INTERVAL '1 day')
           ) ic ON true
           LEFT JOIN LATERAL (
               SELECT r.spec_provenance->>'executed_sha256' AS executed_sha256,
                      r.spec_provenance->>'spec_path' AS spec_path
               FROM runs r
               WHERE r.check_id = c.id AND r.spec_provenance->>'executed_sha256' IS NOT NULL
               ORDER BY r.started_at DESC
               LIMIT 1
           ) sp ON true
           -- §D1 v2 (0057): the latest HARNESS-CONFIRMED red-test (outcome='red') for this monitor — flips
           -- redTest.captured=true. NOT windowed: a red-test is a durable capability proof, not a window metric.
           LEFT JOIN LATERAL (
               SELECT rt0.tested_at AS red_tested_at, rt0.method AS red_test_method
               FROM red_tests rt0
               WHERE rt0.check_id = c.id AND rt0.outcome = 'red'
               ORDER BY rt0.tested_at DESC
               LIMIT 1
           ) rt ON true
           WHERE c.enabled = true
             -- ★ Pre-prod default-EXCLUDE (arc S1c): the trust scorecard is the PROD fleet only. The
             -- single-check detail (TrustDetailSql) is deliberately NOT excluded — a caller asking for one
             -- monitor by id has already scoped it.
             AND coalesce(c.environment, 'prod') = 'prod'
           ORDER BY c.name";

    // Same row shape as the fleet SQL, scoped to one check (enabled or not — a detail view resolves disabled
    // monitors too). Returns 0 or 1 row.
    private static FormattableString TrustDetailSql(int days, long checkId) =>
        $@"SELECT c.id AS check_id, c.name AS check_name, c.sensitive AS sensitive,
                  c.interval_seconds AS interval_seconds, c.last_run_at AS last_run_at,
                  rc.last_green_at AS last_green_at,
                  coalesce(rc.run_count, 0) AS run_count, coalesce(rc.retry_count, 0) AS retry_count,
                  coalesce(rc.retried_passes, 0) AS retried_passes,
                  coalesce(ic.total, 0) AS incident_total,
                  coalesce(ic.real_outage, 0) AS real_outage,
                  coalesce(ic.flaky_transient, 0) AS flaky_transient,
                  coalesce(ic.selector_drift, 0) AS selector_drift,
                  coalesce(ic.environment_regional, 0) AS environment_regional,
                  coalesce(ic.perf_regression, 0) AS perf_regression,
                  coalesce(ic.unclassified, 0) AS unclassified,
                  sp.executed_sha256 AS executed_sha256, sp.spec_path AS spec_path,
                  rt.red_tested_at AS red_tested_at, rt.red_test_method AS red_test_method
           FROM checks c
           LEFT JOIN LATERAL (
               SELECT count(*)::bigint AS run_count,
                      count(*) FILTER (WHERE r.retry_count > 1 /* attempt count (runner migration 0048): 1 = first try / NO retry; > 1 = an ACTUAL retry. > 0 would count every clean pass as retried. */)::bigint AS retry_count,
                      -- ★ degrading-but-green early warning: a PASS/WARN run that STILL needed a real retry. A
                      -- DISPLAY-ONLY annotation — NEVER an input to DeriveChip (the #152 class must not recur).
                      -- Counted over the SAME window as retry_count.
                      count(*) FILTER (WHERE r.status IN ('pass','warn') AND r.retry_count > 1 /* attempt count (runner migration 0048): 1 = first try / NO retry; > 1 = an ACTUAL retry. > 0 would count every clean pass as retried. */)::bigint AS retried_passes,
                      max(r.started_at) FILTER (WHERE r.status = 'pass') AS last_green_at
               FROM runs r
               WHERE r.check_id = c.id AND r.started_at >= now() - ({days} * INTERVAL '1 day')
           ) rc ON true
           LEFT JOIN LATERAL (
               SELECT count(*)::bigint AS total,
                      count(*) FILTER (WHERE i.rca->>'classification' = 'real-outage')::bigint AS real_outage,
                      count(*) FILTER (WHERE i.rca->>'classification' = 'flaky-transient')::bigint AS flaky_transient,
                      count(*) FILTER (WHERE i.rca->>'classification' = 'selector-drift')::bigint AS selector_drift,
                      count(*) FILTER (WHERE i.rca->>'classification' = 'environment-regional')::bigint AS environment_regional,
                      count(*) FILTER (WHERE i.rca->>'classification' = 'perf-regression')::bigint AS perf_regression,
                      count(*) FILTER (WHERE i.rca->>'classification' IS NULL)::bigint AS unclassified
               FROM incidents i
               WHERE i.check_id = c.id AND i.opened_at >= now() - ({days} * INTERVAL '1 day')
           ) ic ON true
           LEFT JOIN LATERAL (
               SELECT r.spec_provenance->>'executed_sha256' AS executed_sha256,
                      r.spec_provenance->>'spec_path' AS spec_path
               FROM runs r
               WHERE r.check_id = c.id AND r.spec_provenance->>'executed_sha256' IS NOT NULL
               ORDER BY r.started_at DESC
               LIMIT 1
           ) sp ON true
           -- §D1 v2 (0057): the latest HARNESS-CONFIRMED red-test (outcome='red') for this monitor — flips
           -- redTest.captured=true. NOT windowed: a red-test is a durable capability proof, not a window metric.
           LEFT JOIN LATERAL (
               SELECT rt0.tested_at AS red_tested_at, rt0.method AS red_test_method
               FROM red_tests rt0
               WHERE rt0.check_id = c.id AND rt0.outcome = 'red'
               ORDER BY rt0.tested_at DESC
               LIMIT 1
           ) rt ON true
           WHERE c.id = {checkId}";

    /// <summary>GET /api/reports/availability?window=&amp;groupBy=&lt;tagKey&gt;&amp;tag=key:value (repeatable, AND-filter) — availability by group (from the rollup).</summary>
    [Function("GetAvailabilityReport")]
    public async Task<IActionResult> GetAvailabilityReport(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "reports/availability")] HttpRequest req,
        CancellationToken ct)
    {
        var window = req.Query["window"].ToString();
        if (WindowDays(window) is not int days) return ApiResults.BadRequest("window must be one of: 7d, 30d, 90d.");
        var offset = days - 1;
        var groupBy = req.Query["groupBy"].ToString();
        var grouped = IsGrouped(groupBy);
        var tags = TagFilter(req);

        // Per-(group, check) availability summed from the daily rollup counts (additive — NOT averaged %).
        // Tag filter (no-op when empty): restrict to checks carrying all selected tags.
        var rows = grouped
            ? await _db.AvailabilityReport.FromSql(
                $@"SELECT ct.value AS group_value, r.check_id AS check_id, c.name AS check_name,
                          sum(r.up_count) AS up_count, sum(r.down_count) AS down_count, sum(r.total_count) AS total_count,
                          sum(r.downtime_minutes) AS downtime_minutes, sum(r.incidents_opened) AS incidents_opened
                   FROM daily_check_rollup r
                   JOIN checks c ON c.id = r.check_id
                   JOIN check_tags ct ON ct.check_id = r.check_id AND ct.key = {groupBy}
                   WHERE r.day >= CURRENT_DATE - {offset}
                     AND (cardinality({tags}) = 0 OR r.check_id IN (
                           SELECT ft.check_id FROM check_tags ft WHERE ft.key || ':' || ft.value = ANY({tags})
                           GROUP BY ft.check_id HAVING count(DISTINCT ft.key || ':' || ft.value) = cardinality({tags})))
                   GROUP BY ct.value, r.check_id, c.name").AsNoTracking().ToListAsync(ct)
            : await _db.AvailabilityReport.FromSql(
                $@"SELECT NULL::text AS group_value, r.check_id AS check_id, c.name AS check_name,
                          sum(r.up_count) AS up_count, sum(r.down_count) AS down_count, sum(r.total_count) AS total_count,
                          sum(r.downtime_minutes) AS downtime_minutes, sum(r.incidents_opened) AS incidents_opened
                   FROM daily_check_rollup r
                   JOIN checks c ON c.id = r.check_id
                   WHERE r.day >= CURRENT_DATE - {offset}
                     AND (cardinality({tags}) = 0 OR r.check_id IN (
                           SELECT ft.check_id FROM check_tags ft WHERE ft.key || ':' || ft.value = ANY({tags})
                           GROUP BY ft.check_id HAVING count(DISTINCT ft.key || ':' || ft.value) = cardinality({tags})))
                   GROUP BY r.check_id, c.name").AsNoTracking().ToListAsync(ct);

        var series = grouped
            ? await _db.AvailabilityReportSeries.FromSql(
                $@"SELECT ct.value AS group_value, r.day AS day, sum(r.up_count) AS up_count, sum(r.down_count) AS down_count
                   FROM daily_check_rollup r
                   JOIN check_tags ct ON ct.check_id = r.check_id AND ct.key = {groupBy}
                   WHERE r.day >= CURRENT_DATE - {offset}
                     AND (cardinality({tags}) = 0 OR r.check_id IN (
                           SELECT ft.check_id FROM check_tags ft WHERE ft.key || ':' || ft.value = ANY({tags})
                           GROUP BY ft.check_id HAVING count(DISTINCT ft.key || ':' || ft.value) = cardinality({tags})))
                   GROUP BY ct.value, r.day ORDER BY r.day").AsNoTracking().ToListAsync(ct)
            : await _db.AvailabilityReportSeries.FromSql(
                $@"SELECT NULL::text AS group_value, r.day AS day, sum(r.up_count) AS up_count, sum(r.down_count) AS down_count
                   FROM daily_check_rollup r
                   WHERE r.day >= CURRENT_DATE - {offset}
                     AND (cardinality({tags}) = 0 OR r.check_id IN (
                           SELECT ft.check_id FROM check_tags ft WHERE ft.key || ':' || ft.value = ANY({tags})
                           GROUP BY ft.check_id HAVING count(DISTINCT ft.key || ':' || ft.value) = cardinality({tags})))
                   GROUP BY r.day ORDER BY r.day").AsNoTracking().ToListAsync(ct);

        var seriesByGroup = series.ToLookup(s => s.GroupValue);
        var groups = rows.GroupBy(r => r.GroupValue).Select(g =>
        {
            long up = g.Sum(r => r.UpCount), down = g.Sum(r => r.DownCount), total = g.Sum(r => r.TotalCount);
            var checks = g.OrderBy(r => r.CheckName, StringComparer.Ordinal).Select(r => new AvailabilityCheckDto(
                r.CheckId, r.CheckName, Pct(r.UpCount, r.DownCount), r.UpCount, r.DownCount, r.DowntimeMinutes, r.IncidentsOpened)).ToList();
            var pts = seriesByGroup[g.Key].Select(s => new AvailabilityPointDtoR(s.Day, Pct(s.UpCount, s.DownCount), s.UpCount, s.DownCount)).ToList();
            return new AvailabilityGroupDto(g.Key, Pct(up, down), up, down, total,
                g.Sum(r => r.DowntimeMinutes), g.Sum(r => r.IncidentsOpened), checks, pts);
        }).OrderBy(g => g.Group, StringComparer.Ordinal).ToList();

        return ApiResults.Ok(new AvailabilityReportDto(string.IsNullOrEmpty(window) ? "30d" : window, grouped ? groupBy : null, groups));
    }

    /// <summary>GET /api/reports/performance?window=&amp;groupBy=&amp;tag=key:value (repeatable, AND-filter) — latency (p50/p95/p99 RECOMPUTED FROM RAW) + browser web-vitals.</summary>
    [Function("GetPerformanceReport")]
    public async Task<IActionResult> GetPerformanceReport(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "reports/performance")] HttpRequest req,
        CancellationToken ct)
    {
        var window = req.Query["window"].ToString();
        if (WindowDays(window) is not int days) return ApiResults.BadRequest("window must be one of: 7d, 30d, 90d.");
        var offset = days - 1;
        var groupBy = req.Query["groupBy"].ToString();
        var grouped = IsGrouped(groupBy);
        var tags = TagFilter(req);

        // Latency over the window, RECOMPUTED FROM RAW (#88's MW-excluded / running-excluded / UP-runs
        // filter). GROUPING SETS yields both per-check rows AND the group-level aggregate (check_id NULL)
        // in one pass — each percentile recomputed from raw at its own level (per-check pXX can't combine).
        var latency = grouped
            ? await _db.LatencyReport.FromSql(
                $@"WITH mwx AS (
                     SELECT r.id, r.check_id, r.status, r.duration_ms FROM runs r
                     WHERE r.started_at >= (CURRENT_DATE - {offset})::timestamptz AND r.status <> 'running'
                       AND NOT EXISTS (SELECT 1 FROM maintenance_windows mw
                          WHERE (mw.check_id = r.check_id OR mw.check_id IS NULL)
                            AND r.started_at >= mw.starts_at AND r.started_at < mw.ends_at)
                       AND (cardinality({tags}) = 0 OR r.check_id IN (
                             SELECT ft.check_id FROM check_tags ft WHERE ft.key || ':' || ft.value = ANY({tags})
                             GROUP BY ft.check_id HAVING count(DISTINCT ft.key || ':' || ft.value) = cardinality({tags}))))
                   SELECT ct.value AS group_value, mwx.check_id AS check_id,
                     count(mwx.duration_ms) FILTER (WHERE mwx.status IN ('pass','warn')) AS latency_count,
                     round(avg(mwx.duration_ms) FILTER (WHERE mwx.status IN ('pass','warn')), 1) AS avg_ms,
                     round(percentile_cont(0.5)  WITHIN GROUP (ORDER BY mwx.duration_ms) FILTER (WHERE mwx.status IN ('pass','warn')))::int AS p50_ms,
                     round(percentile_cont(0.95) WITHIN GROUP (ORDER BY mwx.duration_ms) FILTER (WHERE mwx.status IN ('pass','warn')))::int AS p95_ms,
                     round(percentile_cont(0.99) WITHIN GROUP (ORDER BY mwx.duration_ms) FILTER (WHERE mwx.status IN ('pass','warn')))::int AS p99_ms
                   FROM mwx JOIN check_tags ct ON ct.check_id = mwx.check_id AND ct.key = {groupBy}
                   GROUP BY GROUPING SETS ((ct.value, mwx.check_id), (ct.value))").AsNoTracking().ToListAsync(ct)
            : await _db.LatencyReport.FromSql(
                $@"WITH mwx AS (
                     SELECT r.id, r.check_id, r.status, r.duration_ms FROM runs r
                     WHERE r.started_at >= (CURRENT_DATE - {offset})::timestamptz AND r.status <> 'running'
                       AND NOT EXISTS (SELECT 1 FROM maintenance_windows mw
                          WHERE (mw.check_id = r.check_id OR mw.check_id IS NULL)
                            AND r.started_at >= mw.starts_at AND r.started_at < mw.ends_at)
                       AND (cardinality({tags}) = 0 OR r.check_id IN (
                             SELECT ft.check_id FROM check_tags ft WHERE ft.key || ':' || ft.value = ANY({tags})
                             GROUP BY ft.check_id HAVING count(DISTINCT ft.key || ':' || ft.value) = cardinality({tags}))))
                   SELECT NULL::text AS group_value, mwx.check_id AS check_id,
                     count(mwx.duration_ms) FILTER (WHERE mwx.status IN ('pass','warn')) AS latency_count,
                     round(avg(mwx.duration_ms) FILTER (WHERE mwx.status IN ('pass','warn')), 1) AS avg_ms,
                     round(percentile_cont(0.5)  WITHIN GROUP (ORDER BY mwx.duration_ms) FILTER (WHERE mwx.status IN ('pass','warn')))::int AS p50_ms,
                     round(percentile_cont(0.95) WITHIN GROUP (ORDER BY mwx.duration_ms) FILTER (WHERE mwx.status IN ('pass','warn')))::int AS p95_ms,
                     round(percentile_cont(0.99) WITHIN GROUP (ORDER BY mwx.duration_ms) FILTER (WHERE mwx.status IN ('pass','warn')))::int AS p99_ms
                   FROM mwx
                   GROUP BY GROUPING SETS ((mwx.check_id), ())").AsNoTracking().ToListAsync(ct);

        var vitals = grouped
            ? await _db.VitalsReport.FromSql(
                $@"WITH mwx AS (
                     SELECT r.id, r.check_id, r.status FROM runs r
                     WHERE r.started_at >= (CURRENT_DATE - {offset})::timestamptz AND r.status <> 'running'
                       AND NOT EXISTS (SELECT 1 FROM maintenance_windows mw
                          WHERE (mw.check_id = r.check_id OR mw.check_id IS NULL)
                            AND r.started_at >= mw.starts_at AND r.started_at < mw.ends_at)
                       AND (cardinality({tags}) = 0 OR r.check_id IN (
                             SELECT ft.check_id FROM check_tags ft WHERE ft.key || ':' || ft.value = ANY({tags})
                             GROUP BY ft.check_id HAVING count(DISTINCT ft.key || ':' || ft.value) = cardinality({tags}))))
                   SELECT ct.value AS group_value, mwx.check_id AS check_id, count(m.run_id) AS vitals_count,
                     round(percentile_cont(0.75) WITHIN GROUP (ORDER BY m.lcp_ms))::int  AS lcp_p75_ms,
                     round(percentile_cont(0.75) WITHIN GROUP (ORDER BY m.fcp_ms))::int  AS fcp_p75_ms,
                     round(percentile_cont(0.75) WITHIN GROUP (ORDER BY m.ttfb_ms))::int AS ttfb_p75_ms,
                     percentile_cont(0.75) WITHIN GROUP (ORDER BY m.cls) AS cls_p75,
                     count(m.inp_ms) AS inp_count,
                     round(percentile_cont(0.75) WITHIN GROUP (ORDER BY m.inp_ms))::int AS inp_p75_ms,
                     round(avg(m.resource_count))::int AS resource_count
                   FROM mwx JOIN run_metrics m ON m.run_id = mwx.id
                            JOIN check_tags ct ON ct.check_id = mwx.check_id AND ct.key = {groupBy}
                   WHERE mwx.status IN ('pass','warn')
                   GROUP BY GROUPING SETS ((ct.value, mwx.check_id), (ct.value))").AsNoTracking().ToListAsync(ct)
            : await _db.VitalsReport.FromSql(
                $@"WITH mwx AS (
                     SELECT r.id, r.check_id, r.status FROM runs r
                     WHERE r.started_at >= (CURRENT_DATE - {offset})::timestamptz AND r.status <> 'running'
                       AND NOT EXISTS (SELECT 1 FROM maintenance_windows mw
                          WHERE (mw.check_id = r.check_id OR mw.check_id IS NULL)
                            AND r.started_at >= mw.starts_at AND r.started_at < mw.ends_at)
                       AND (cardinality({tags}) = 0 OR r.check_id IN (
                             SELECT ft.check_id FROM check_tags ft WHERE ft.key || ':' || ft.value = ANY({tags})
                             GROUP BY ft.check_id HAVING count(DISTINCT ft.key || ':' || ft.value) = cardinality({tags}))))
                   SELECT NULL::text AS group_value, mwx.check_id AS check_id, count(m.run_id) AS vitals_count,
                     round(percentile_cont(0.75) WITHIN GROUP (ORDER BY m.lcp_ms))::int  AS lcp_p75_ms,
                     round(percentile_cont(0.75) WITHIN GROUP (ORDER BY m.fcp_ms))::int  AS fcp_p75_ms,
                     round(percentile_cont(0.75) WITHIN GROUP (ORDER BY m.ttfb_ms))::int AS ttfb_p75_ms,
                     percentile_cont(0.75) WITHIN GROUP (ORDER BY m.cls) AS cls_p75,
                     count(m.inp_ms) AS inp_count,
                     round(percentile_cont(0.75) WITHIN GROUP (ORDER BY m.inp_ms))::int AS inp_p75_ms,
                     round(avg(m.resource_count))::int AS resource_count
                   FROM mwx JOIN run_metrics m ON m.run_id = mwx.id
                   WHERE mwx.status IN ('pass','warn')
                   GROUP BY GROUPING SETS ((mwx.check_id), ())").AsNoTracking().ToListAsync(ct);

        // Daily trend: count-weighted avg latency from the rollup (avg IS aggregatable; percentiles are not).
        var latSeries = grouped
            ? await _db.LatencyReportSeries.FromSql(
                $@"SELECT ct.value AS group_value, r.day AS day,
                          round(sum(r.duration_avg_ms * r.latency_count) / nullif(sum(r.latency_count), 0), 1) AS avg_ms
                   FROM daily_check_rollup r
                   JOIN check_tags ct ON ct.check_id = r.check_id AND ct.key = {groupBy}
                   WHERE r.day >= CURRENT_DATE - {offset} AND r.latency_count > 0
                     AND (cardinality({tags}) = 0 OR r.check_id IN (
                           SELECT ft.check_id FROM check_tags ft WHERE ft.key || ':' || ft.value = ANY({tags})
                           GROUP BY ft.check_id HAVING count(DISTINCT ft.key || ':' || ft.value) = cardinality({tags})))
                   GROUP BY ct.value, r.day ORDER BY r.day").AsNoTracking().ToListAsync(ct)
            : await _db.LatencyReportSeries.FromSql(
                $@"SELECT NULL::text AS group_value, r.day AS day,
                          round(sum(r.duration_avg_ms * r.latency_count) / nullif(sum(r.latency_count), 0), 1) AS avg_ms
                   FROM daily_check_rollup r
                   WHERE r.day >= CURRENT_DATE - {offset} AND r.latency_count > 0
                     AND (cardinality({tags}) = 0 OR r.check_id IN (
                           SELECT ft.check_id FROM check_tags ft WHERE ft.key || ':' || ft.value = ANY({tags})
                           GROUP BY ft.check_id HAVING count(DISTINCT ft.key || ':' || ft.value) = cardinality({tags})))
                   GROUP BY r.day ORDER BY r.day").AsNoTracking().ToListAsync(ct);

        var names = await _db.Checks.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var vitLookup = vitals.ToLookup(v => (v.GroupValue, v.CheckId));
        var seriesByGroup = latSeries.ToLookup(s => s.GroupValue);

        var groups = latency.GroupBy(r => r.GroupValue).Select(g =>
        {
            var groupRow = g.FirstOrDefault(r => r.CheckId is null);
            var checks = g.Where(r => r.CheckId is not null)
                .OrderBy(r => names.GetValueOrDefault(r.CheckId!.Value, ""), StringComparer.Ordinal)
                .Select(r => new PerformanceCheckDto(
                    r.CheckId!.Value, names.GetValueOrDefault(r.CheckId.Value, ""),
                    Lat(r), Vit(vitLookup[(g.Key, r.CheckId)].FirstOrDefault()))).ToList();
            var pts = seriesByGroup[g.Key].Select(s => new LatencyPointDto(s.Day, s.AvgMs)).ToList();
            return new PerformanceGroupDto(g.Key, Lat(groupRow), Vit(vitLookup[(g.Key, null)].FirstOrDefault()), checks, pts);
        }).OrderBy(g => g.Group, StringComparer.Ordinal).ToList();

        return ApiResults.Ok(new PerformanceReportDto(string.IsNullOrEmpty(window) ? "30d" : window, grouped ? groupBy : null, groups));
    }

    // ★ The dashboard sends groupBy="none" (not "") for the UNGROUPED report. Treat that sentinel (and
    // empty) as ungrouped — otherwise we JOIN check_tags ON key='none', which matches no rows, so the report
    // returns {"groups":[]} even though the rollup has data. That was the "reports empty despite data" bug.
    private static bool IsGrouped(string groupBy) =>
        !string.IsNullOrWhiteSpace(groupBy) && !groupBy.Equals("none", StringComparison.OrdinalIgnoreCase);

    private static decimal? Pct(long up, long down) =>
        up + down > 0 ? Math.Round(100m * up / (up + down), 4) : null;

    private static LatencyDto Lat(LatencyReportRow? r) =>
        r is null ? new LatencyDto(0, null, null, null, null)
                  : new LatencyDto(r.LatencyCount, r.AvgMs, r.P50Ms, r.P95Ms, r.P99Ms);

    private static WebVitalsDto? Vit(VitalsReportRow? v) =>
        v is null || v.VitalsCount == 0 ? null
            : new WebVitalsDto(v.VitalsCount, v.LcpP75Ms, v.FcpP75Ms, v.TtfbP75Ms, v.ClsP75,
                v.InpP75Ms, v.InpCount, v.ResourceCount);

    /// <summary>
    /// GET /api/reports/narrative?scope=fleet|monitor&amp;key=&lt;checkId|'fleet'&gt;&amp;window=7d|30d|90d —
    /// serve the LATEST runner-generated narrative (Layer 3) read-only. No generation here (the AOAI lives
    /// in the runner). Missing row → 404 so the dashboard hides the card cleanly.
    /// </summary>
    [Function("GetNarrative")]
    public async Task<IActionResult> GetNarrative(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "reports/narrative")] HttpRequest req,
        CancellationToken ct)
    {
        var scope = req.Query["scope"].ToString();
        if (scope is not ("fleet" or "monitor"))
            return ApiResults.BadRequest("scope must be 'fleet' or 'monitor'.");
        var window = req.Query["window"].ToString();
        if (WindowDays(window) is not int days)
            return ApiResults.BadRequest("window must be one of: 7d, 30d, 90d.");

        // Contract (runner is source of truth): the FLEET narrative is keyed by an EMPTY scope_key — there
        // is exactly one fleet narrative per window, so the ?key param is meaningless for fleet and forced
        // to ''. monitor narratives are keyed by the check id (?key, required).
        var key = scope == "fleet" ? "" : req.Query["key"].ToString();
        if (scope == "monitor" && string.IsNullOrWhiteSpace(key))
            return ApiResults.BadRequest("key (the check id) is required when scope=monitor.");

        // highlights + fact_pack as raw jsonb text → re-emitted verbatim (cited numbers pass through).
        var row = (await _db.ReportNarratives.FromSql(
            $@"SELECT scope_type, scope_key, ""window"", generated_at, headline, body,
                      highlights::text AS highlights, fact_pack::text AS fact_pack, model
               FROM report_narratives
               WHERE scope_type = {scope} AND scope_key = {key} AND ""window"" = {window}
               ORDER BY generated_at DESC
               LIMIT 1").AsNoTracking().ToListAsync(ct)).FirstOrDefault();

        if (row is null)
            return ApiResults.NotFound($"No narrative for scope='{scope}', key='{key}', window='{window}'.");

        var stale = DateTimeOffset.UtcNow - row.GeneratedAt > TimeSpan.FromDays(days);
        return ApiResults.Ok(new NarrativeDto(scope, key, window, row.Headline, row.Body,
            SafeStringList(row.Highlights), row.GeneratedAt, stale, row.Model, SafeJson(row.FactPack)));
    }

    // Parse runner-written jsonb (read as text) defensively — a malformed value degrades, never throws.
    private static IReadOnlyList<string> SafeStringList(string? json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(string.IsNullOrWhiteSpace(json) ? "[]" : json) ?? new List<string>(); }
        catch (JsonException) { return Array.Empty<string>(); }
    }

    private static JsonElement SafeJson(string? json)
    {
        try { return JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(json) ? "{}" : json); }
        catch (JsonException) { return JsonSerializer.Deserialize<JsonElement>("{}"); }
    }
}

/// <summary>High-performance (CA1848) log delegates for ReportsFunctions tolerance paths.</summary>
internal static partial class DeploysReportLog
{
    [LoggerMessage(EventId = 7001, Level = LogLevel.Debug,
        Message = "deploys table absent (42P01) — /reports/deploys serving empty for host {Host} (expected pre-migration; merged≠migrated)")]
    public static partial void DeploysTableAbsent(ILogger logger, string host);
}
