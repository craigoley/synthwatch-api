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

    /// <summary>GET /api/reports/availability?window=7d|30d|90d&amp;groupBy=&lt;tagKey&gt; — availability by group (from the rollup).</summary>
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

        // Per-(group, check) availability summed from the daily rollup counts (additive — NOT averaged %).
        var rows = grouped
            ? await _db.AvailabilityReport.FromSql(
                $@"SELECT ct.value AS group_value, r.check_id AS check_id, c.name AS check_name,
                          sum(r.up_count) AS up_count, sum(r.down_count) AS down_count, sum(r.total_count) AS total_count,
                          sum(r.downtime_minutes) AS downtime_minutes, sum(r.incidents_opened) AS incidents_opened
                   FROM daily_check_rollup r
                   JOIN checks c ON c.id = r.check_id
                   JOIN check_tags ct ON ct.check_id = r.check_id AND ct.key = {groupBy}
                   WHERE r.day >= CURRENT_DATE - {offset}
                   GROUP BY ct.value, r.check_id, c.name").AsNoTracking().ToListAsync(ct)
            : await _db.AvailabilityReport.FromSql(
                $@"SELECT NULL::text AS group_value, r.check_id AS check_id, c.name AS check_name,
                          sum(r.up_count) AS up_count, sum(r.down_count) AS down_count, sum(r.total_count) AS total_count,
                          sum(r.downtime_minutes) AS downtime_minutes, sum(r.incidents_opened) AS incidents_opened
                   FROM daily_check_rollup r
                   JOIN checks c ON c.id = r.check_id
                   WHERE r.day >= CURRENT_DATE - {offset}
                   GROUP BY r.check_id, c.name").AsNoTracking().ToListAsync(ct);

        var series = grouped
            ? await _db.AvailabilityReportSeries.FromSql(
                $@"SELECT ct.value AS group_value, r.day AS day, sum(r.up_count) AS up_count, sum(r.down_count) AS down_count
                   FROM daily_check_rollup r
                   JOIN check_tags ct ON ct.check_id = r.check_id AND ct.key = {groupBy}
                   WHERE r.day >= CURRENT_DATE - {offset}
                   GROUP BY ct.value, r.day ORDER BY r.day").AsNoTracking().ToListAsync(ct)
            : await _db.AvailabilityReportSeries.FromSql(
                $@"SELECT NULL::text AS group_value, r.day AS day, sum(r.up_count) AS up_count, sum(r.down_count) AS down_count
                   FROM daily_check_rollup r
                   WHERE r.day >= CURRENT_DATE - {offset}
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

    /// <summary>GET /api/reports/performance?window=&amp;groupBy= — latency (p50/p95/p99 RECOMPUTED FROM RAW) + browser web-vitals.</summary>
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
                            AND r.started_at >= mw.starts_at AND r.started_at < mw.ends_at))
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
                            AND r.started_at >= mw.starts_at AND r.started_at < mw.ends_at))
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
                            AND r.started_at >= mw.starts_at AND r.started_at < mw.ends_at))
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
                            AND r.started_at >= mw.starts_at AND r.started_at < mw.ends_at))
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
                   GROUP BY ct.value, r.day ORDER BY r.day").AsNoTracking().ToListAsync(ct)
            : await _db.LatencyReportSeries.FromSql(
                $@"SELECT NULL::text AS group_value, r.day AS day,
                          round(sum(r.duration_avg_ms * r.latency_count) / nullif(sum(r.latency_count), 0), 1) AS avg_ms
                   FROM daily_check_rollup r
                   WHERE r.day >= CURRENT_DATE - {offset} AND r.latency_count > 0
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
            return new PerformanceGroupDto(g.Key, Lat(groupRow), Vit(vitLookup[(g.Key, (long?)null)].FirstOrDefault()), checks, pts);
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
