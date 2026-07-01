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

    public ReportsFunctions(SynthWatchDbContext db) => _db = db;

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
                     percentile_cont(0.75) WITHIN GROUP (ORDER BY m.cls) AS cls_p75
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
                     percentile_cont(0.75) WITHIN GROUP (ORDER BY m.cls) AS cls_p75
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
            : new WebVitalsDto(v.VitalsCount, v.LcpP75Ms, v.FcpP75Ms, v.TtfbP75Ms, v.ClsP75);

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
