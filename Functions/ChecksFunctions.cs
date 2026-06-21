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

public class ChecksFunctions
{
    private readonly SynthWatchDbContext _db;

    public ChecksFunctions(SynthWatchDbContext db) => _db = db;

    // Per-check dashboard-parity metrics, ported verbatim from the dashboard's old route
    // handler (lateral joins on the runs_check_started_idx hot path). Parameterless — only
    // NOW()/intervals/constants, no user input. json_agg is cast to text and deserialized in C#.
    private const string MetricsSql = """
        SELECT
          c.id AS check_id,
          stats.p50_ms,
          stats.p95_ms,
          COALESCE(stats.runs_24h, 0)          AS runs_24h,
          COALESCE(oi.open_incident_count, 0)  AS open_incident_count,
          oi.max_open_severity,
          COALESCE(spark.points, '[]'::json)::text AS spark
        FROM checks c
        LEFT JOIN LATERAL (
          SELECT
            percentile_cont(0.5)  WITHIN GROUP (ORDER BY r.duration_ms) AS p50_ms,
            percentile_cont(0.95) WITHIN GROUP (ORDER BY r.duration_ms) AS p95_ms,
            COUNT(*)::int AS runs_24h
          FROM runs r
          WHERE r.check_id = c.id
            AND r.started_at >= NOW() - INTERVAL '24 hours'
            AND r.duration_ms IS NOT NULL
        ) stats ON TRUE
        LEFT JOIN LATERAL (
          SELECT
            COUNT(*)::int AS open_incident_count,
            (
              SELECT i2.severity FROM incidents i2
              WHERE i2.check_id = c.id AND i2.resolved_at IS NULL
              ORDER BY (i2.severity = 'critical') DESC, i2.opened_at DESC
              LIMIT 1
            ) AS max_open_severity
          FROM incidents i
          WHERE i.check_id = c.id AND i.resolved_at IS NULL
        ) oi ON TRUE
        LEFT JOIN LATERAL (
          SELECT json_agg(p ORDER BY p.t) AS points
          FROM (
            SELECT r.started_at AS t, r.duration_ms AS d, r.status AS s
            FROM runs r
            WHERE r.check_id = c.id
            ORDER BY r.started_at DESC
            LIMIT 30
          ) p
        ) spark ON TRUE
        """;

    /// <summary>GET /api/checks — list with derived current status + dashboard-parity metrics.</summary>
    [Function("ListChecks")]
    public async Task<IActionResult> ListChecks(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "checks")] HttpRequest req,
        CancellationToken ct)
    {
        var checks = await _db.Checks.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
        var ids = checks.Select(c => c.Id).ToList();

        // Latest run per check in one round-trip.
        var latestRuns = await _db.Runs.AsNoTracking()
            .Where(r => ids.Contains(r.CheckId))
            .GroupBy(r => r.CheckId)
            .Select(g => g.OrderByDescending(r => r.StartedAt).First())
            .ToListAsync(ct);
        var latestByCheck = latestRuns.ToDictionary(r => r.CheckId);

        // Ported lateral-join metrics (p50/p95, runs24h, sparkline, open-incident rollup),
        // one round-trip for all checks. Open-incident count also backs hasOpenIncident.
        var metricRows = await _db.CheckMetrics.FromSqlRaw(MetricsSql).AsNoTracking().ToListAsync(ct);
        var metricsByCheck = metricRows.ToDictionary(m => m.CheckId, m => new CheckMetricsDto(
            m.P50Ms, m.P95Ms, m.Runs24h, m.OpenIncidentCount, m.MaxOpenSeverity,
            JsonSerializer.Deserialize<List<SparkPoint>>(m.Spark) ?? new List<SparkPoint>()));

        var result = checks.Select(c => CheckSummaryDto.From(
            c,
            latestByCheck.GetValueOrDefault(c.Id),
            metricsByCheck.GetValueOrDefault(c.Id, CheckMetricsDto.Empty)));

        // Short cache so the dashboard's polling doesn't hit the DB every tick; current status
        // moves run-to-run, so keep it brief (10s).
        req.HttpContext.Response.Headers.CacheControl = "public, max-age=10";
        return ApiResults.Ok(result);
    }

    /// <summary>GET /api/checks/{id} — one check plus recent runs.</summary>
    [Function("GetCheck")]
    public async Task<IActionResult> GetCheck(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "checks/{id:long}")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        var check = await _db.Checks.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (check is null)
            return ApiResults.NotFound($"Check {id} not found.");

        var recentRuns = await _db.Runs.AsNoTracking()
            .Where(r => r.CheckId == id)
            .OrderByDescending(r => r.StartedAt)
            .Take(20)
            .ToListAsync(ct);

        return ApiResults.Ok(CheckDetailDto.From(check, recentRuns));
    }

    /// <summary>POST /api/checks — create (id omitted; generated by the DB).</summary>
    [Function("CreateCheck")]
    public async Task<IActionResult> CreateCheck(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "checks")] HttpRequest req,
        CancellationToken ct)
    {
        CreateCheckRequest? body;
        try
        {
            body = await req.ReadFromJsonAsync<CreateCheckRequest>(ct);
        }
        catch (JsonException)
        {
            return ApiResults.BadRequest("Request body is not valid JSON.");
        }

        if (body is null)
            return ApiResults.BadRequest("Request body is required.");

        if (!CheckValidation.TryBuildNew(body, out var check, out var errors))
            return ApiResults.ValidationError(errors);

        _db.Checks.Add(check);
        await _db.SaveChangesAsync(ct);

        return ApiResults.Created($"/api/checks/{check.Id}", CheckDetailDto.From(check, Array.Empty<Run>()));
    }

    /// <summary>PATCH /api/checks/{id} — partial edit / pause (enabled).</summary>
    [Function("UpdateCheck")]
    public async Task<IActionResult> UpdateCheck(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "checks/{id:long}")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        UpdateCheckRequest? body;
        try
        {
            body = await req.ReadFromJsonAsync<UpdateCheckRequest>(ct);
        }
        catch (JsonException)
        {
            return ApiResults.BadRequest("Request body is not valid JSON.");
        }

        if (body is null)
            return ApiResults.BadRequest("Request body is required.");

        var check = await _db.Checks.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (check is null)
            return ApiResults.NotFound($"Check {id} not found.");

        var errors = CheckValidation.ApplyPatch(body, check);
        if (errors.Count > 0)
            return ApiResults.ValidationError(errors);

        await _db.SaveChangesAsync(ct);

        var recentRuns = await _db.Runs.AsNoTracking()
            .Where(r => r.CheckId == id)
            .OrderByDescending(r => r.StartedAt)
            .Take(20)
            .ToListAsync(ct);

        return ApiResults.Ok(CheckDetailDto.From(check, recentRuns));
    }

    /// <summary>DELETE /api/checks/{id} — soft delete (enabled=false) by default; ?hard=true removes the row.</summary>
    [Function("DeleteCheck")]
    public async Task<IActionResult> DeleteCheck(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "checks/{id:long}")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        var check = await _db.Checks.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (check is null)
            return ApiResults.NotFound($"Check {id} not found.");

        var hard = string.Equals(req.Query["hard"], "true", StringComparison.OrdinalIgnoreCase);
        if (hard)
        {
            // FKs cascade (runs, run_steps, run_metrics, incidents all ON DELETE CASCADE).
            _db.Checks.Remove(check);
        }
        else
        {
            check.Enabled = false;
        }

        await _db.SaveChangesAsync(ct);
        return ApiResults.NoContent();
    }

    /// <summary>GET /api/checks/{id}/runs — paginated run history.</summary>
    [Function("ListCheckRuns")]
    public async Task<IActionResult> ListCheckRuns(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "checks/{id:long}/runs")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        if (!await _db.Checks.AnyAsync(c => c.Id == id, ct))
            return ApiResults.NotFound($"Check {id} not found.");

        var (page, pageSize) = Paging.Parse(req);

        var query = _db.Runs.AsNoTracking().Where(r => r.CheckId == id);
        var total = await query.LongCountAsync(ct);
        var runs = (await query
            .OrderByDescending(r => r.StartedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct))
            .Select(RunDto.From)
            .ToList();

        return ApiResults.Ok(new PagedResult<RunDto>(runs, page, pageSize, total));
    }

    /// <summary>GET /api/checks/{id}/metrics — run_metrics time series for this check.</summary>
    [Function("ListCheckMetrics")]
    public async Task<IActionResult> ListCheckMetrics(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "checks/{id:long}/metrics")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        if (!await _db.Checks.AnyAsync(c => c.Id == id, ct))
            return ApiResults.NotFound($"Check {id} not found.");

        var (page, pageSize) = Paging.Parse(req);

        // Metrics joined to their run so we can order chronologically and scope to the check.
        var query = from m in _db.RunMetrics.AsNoTracking()
                    join r in _db.Runs.AsNoTracking() on m.RunId equals r.Id
                    where r.CheckId == id
                    orderby r.StartedAt descending
                    select m;

        var total = await query.LongCountAsync(ct);
        var metrics = (await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct))
            .Select(RunMetricDto.From)
            .ToList();

        return ApiResults.Ok(new PagedResult<RunMetricDto>(metrics, page, pageSize, total));
    }
}
