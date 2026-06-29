using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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

        // Latest run per (check, location) in one round-trip. Gives the per-location rollup AND,
        // by taking the most recent across a check's locations, the overall latest run.
        var latestPerLocation = await _db.Runs.AsNoTracking()
            .Where(r => ids.Contains(r.CheckId))
            .GroupBy(r => new { r.CheckId, r.Location })
            .Select(g => g.OrderByDescending(r => r.StartedAt).First())
            .ToListAsync(ct);
        var byCheck = latestPerLocation.GroupBy(r => r.CheckId).ToDictionary(g => g.Key, g => g.ToList());
        // Overall latest run per check = the most recent across its locations (unchanged semantics).
        var latestByCheck = byCheck.ToDictionary(
            kv => kv.Key, kv => kv.Value.OrderByDescending(r => r.StartedAt).First());
        // Per-location rollup: one {location, status} per location, ordered by location name.
        var rollupByCheck = byCheck.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<LocationStatusDto>)kv.Value
                .Select(r => new LocationStatusDto(
                    string.IsNullOrEmpty(r.Location) ? "default" : r.Location, r.Status))
                .OrderBy(d => d.Location, StringComparer.Ordinal).ToList());

        // Ported lateral-join metrics (p50/p95, runs24h, sparkline, open-incident rollup),
        // one round-trip for all checks. Open-incident count also backs hasOpenIncident.
        var metricRows = await _db.CheckMetrics.FromSqlRaw(MetricsSql).AsNoTracking().ToListAsync(ct);
        var metricsByCheck = metricRows.ToDictionary(m => m.CheckId, m => new CheckMetricsDto(
            m.P50Ms, m.P95Ms, m.Runs24h, m.OpenIncidentCount, m.MaxOpenSeverity,
            JsonSerializer.Deserialize<List<SparkPoint>>(m.Spark) ?? new List<SparkPoint>()));

        // key:value tags for all listed checks in one round-trip → grouped per check.
        var tagsByCheck = (await _db.CheckTags.AsNoTracking()
                .Where(t => ids.Contains(t.CheckId))
                .OrderBy(t => t.Key).ThenBy(t => t.Value)
                .ToListAsync(ct))
            .GroupBy(t => t.CheckId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TagDto>)g.Select(t => new TagDto(t.Key, t.Value)).ToList());

        var emptyRollup = (IReadOnlyList<LocationStatusDto>)Array.Empty<LocationStatusDto>();
        var emptyTags = (IReadOnlyList<TagDto>)Array.Empty<TagDto>();
        var result = checks.Select(c => CheckSummaryDto.From(
            c,
            latestByCheck.GetValueOrDefault(c.Id),
            metricsByCheck.GetValueOrDefault(c.Id, CheckMetricsDto.Empty),
            rollupByCheck.GetValueOrDefault(c.Id, emptyRollup),
            tagsByCheck.GetValueOrDefault(c.Id, emptyTags)));

        // Short cache so the dashboard's polling doesn't hit the DB every tick; current status
        // moves run-to-run, so keep it brief (10s). Vary on Origin since platform CORS echoes a
        // per-origin Access-Control-Allow-Origin and these responses are publicly cacheable.
        req.HttpContext.Response.Headers.CacheControl = "public, max-age=10";
        req.HttpContext.Response.Headers["Vary"] = "Origin";
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

        return ApiResults.Ok(CheckDetailDto.From(check, recentRuns, await CheckTagsAsync(id, ct), await BuildSloAsync(id, check.SloTarget, ct)));
    }

    // SLO error-budget + burn. Mirrors the SLA function-read pattern (keyless entity + raw SQL),
    // calling slo_status per window: 30d for budget/burn, 1h + 6h for the multi-window burn alerts.
    // Guard: compute ONLY when slo_target is a meaningful fraction in (0,1). null => no SLO (opt-in).
    // target = 1.0 would make slo_status divide by (1 - target) = 0 -> the function errors -> GetCheck
    // 500s; target outside (0,1) is nonsensical. The guard also skips the 3 slo_status round-trips for
    // the common no-SLO check. (The runner-owned slo_status SHOULD also guard slo_target < 1 / constrain
    // the column — this is the API-side defense so a check config can never 500 the detail endpoint.)
    private async Task<SloDto?> BuildSloAsync(long checkId, float? sloTarget, CancellationToken ct)
    {
        if (sloTarget is not (> 0f and < 1f))
            return null;

        var to = DateTimeOffset.UtcNow;
        var primary = await SloAtAsync(checkId, to - TimeSpan.FromDays(30), to, ct);
        if (primary is null)
            return null;

        var fast = await SloAtAsync(checkId, to - TimeSpan.FromHours(1), to, ct);
        var slow = await SloAtAsync(checkId, to - TimeSpan.FromHours(6), to, ct);

        // Google-SRE multi-window burn thresholds for a 30d SLO: 1h@14.4x (page), 6h@6x (sustained).
        return new SloDto(
            Target: primary.SloTarget,
            Budget: primary.Budget,
            Consumed: primary.Consumed,
            Remaining: primary.Remaining,
            BurnRate: primary.BurnRate,
            FastBurn: (fast?.BurnRate ?? 0m) >= 14.4m,
            SlowBurn: (slow?.BurnRate ?? 0m) >= 6m);
    }

    private async Task<SloStatusRow?> SloAtAsync(long checkId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        await _db.SloStatus
            .FromSql($"SELECT * FROM slo_status({checkId}, {from}, {to})")
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

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

        // ★ B10 defense-in-depth: the create DTO does NOT expose `sensitive` (it's Git-authoritative via
        // reconcile), so a created check is always sensitive=false → this never fires today. But it ENSURES
        // the create path can never seed an unwired sensitive check: if a future DTO field lets the API set
        // sensitive=true with no redact_patterns, it's rejected here — same gate as PUT /locations + reconcile.
        if (CheckValidation.SensitiveNeedsRedaction(check.Sensitive, check.RedactPatterns))
            return ApiResults.BadRequest("Cannot create a sensitive check without redaction (B10): declare redact_patterns.");

        _db.Checks.Add(check);

        // Seed per-location cadence cursors in the SAME transaction as the check insert, so a check is
        // never persisted without them. This REPLICATES the runner's assignDefaultLocations() (#73,
        // locations.ts) in C# across the language boundary (runner-owns-schema / API-serves-writes):
        // one check_locations row per ACTIVE registry location, columns (check_id, location) only ->
        // last_run_at stays NULL, which the #68 claim loop's IS-NULL arm treats as due-now (identical to
        // a never-run check). ON CONFLICT DO NOTHING makes it idempotent, so it coexists with the
        // runner's lazy-insert fallback (claim()'s ON CONFLICT DO UPDATE) without colliding. With only
        // 'default' active, this seeds exactly one 'default' cursor — identical to today's implicit
        // lazy-insert-on-first-run, just made explicit at create. (Enforcement + lazy-insert removal are
        // a later step; this is create-path seeding only.)
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            await _db.SaveChangesAsync(ct);   // generates check.Id
        }
        catch (DbUpdateException ex) when (IsSourceKeyConflict(ex))
        {
            // The partial unique index checks_source_key_uniq rejected a SECOND live check for this
            // manifest id — a duplicate activation. Surface it as a clean 409, never the bare 500 a
            // constraint violation would otherwise become. The tx rolls back on dispose (no commit).
            return ApiResults.Conflict($"A monitor for spec '{check.SourceKey}' already exists.");
        }
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO check_locations (check_id, location)
               SELECT {check.Id}, name FROM locations WHERE enabled
               ON CONFLICT (check_id, location) DO NOTHING", ct);
        await tx.CommitAsync(ct);

        // A freshly created check has no tags yet (tags are set via PUT /api/checks/{id}/tags).
        return ApiResults.Created($"/api/checks/{check.Id}", CheckDetailDto.From(check, Array.Empty<Run>(), Array.Empty<TagDto>()));
    }

    // A unique-violation (23505) on checks_source_key_uniq = a duplicate source_key activation → 409.
    private static bool IsSourceKeyConflict(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg &&
        pg.ConstraintName?.Contains("source_key", StringComparison.Ordinal) == true;

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

        return ApiResults.Ok(CheckDetailDto.From(check, recentRuns, await CheckTagsAsync(id, ct), await BuildSloAsync(id, check.SloTarget, ct)));
    }

    // A check's key:value tags as DTOs (sorted), for the check detail/update responses.
    private async Task<IReadOnlyList<TagDto>> CheckTagsAsync(long checkId, CancellationToken ct) =>
        await _db.CheckTags.AsNoTracking()
            .Where(t => t.CheckId == checkId)
            .OrderBy(t => t.Key).ThenBy(t => t.Value)
            .Select(t => new TagDto(t.Key, t.Value))
            .ToListAsync(ct);

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

    /// <summary>
    /// GET /api/checks/{id}/runs — cursor-paginated run history over a bounded date-range window.
    /// Keyset cursor on (started_at DESC, id DESC): stable for an append-only table where OFFSET
    /// re-scans the prefix and double-counts as new runs insert at the head. Params:
    /// <c>?from=&amp;to=</c> (ISO-8601; DEFAULT the last 7d so the query NEVER loads all-time),
    /// <c>?cursor=</c> (the opaque next-cursor from the prior page), <c>?pageSize=</c> (default 50,
    /// max 200). Returns the page + a nextCursor (null when the window is exhausted). The
    /// (check_id, started_at DESC) index makes the windowed + keyset query an index range scan.
    /// </summary>
    [Function("ListCheckRuns")]
    public async Task<IActionResult> ListCheckRuns(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "checks/{id:long}/runs")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        if (!await _db.Checks.AnyAsync(c => c.Id == id, ct))
            return ApiResults.NotFound($"Check {id} not found.");

        var range = CursorPaging.Parse(req, DateTimeOffset.UtcNow);
        if (!range.IsValid)
            return ApiResults.BadRequest(range.Error!);

        // Bounded to the date-range window (default last 7d) — never an all-time scan, and no
        // COUNT over the whole partition (the unbounded cost the cursor design removes).
        var query = _db.Runs.AsNoTracking()
            .Where(r => r.CheckId == id && r.StartedAt >= range.From && r.StartedAt < range.To);

        // Keyset: continue strictly after the cursor's (started_at, id) under the DESC ordering.
        // The id tie-break keeps runs that share a started_at from being skipped or repeated.
        if (range.Cursor is { } cur)
            query = query.Where(r => r.StartedAt < cur.Ts || (r.StartedAt == cur.Ts && r.Id < cur.Id));

        // Over-fetch one row to know whether a further page exists, without a COUNT.
        var rows = await query
            .OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id)
            .Take(range.PageSize + 1)
            .ToListAsync(ct);

        var hasMore = rows.Count > range.PageSize;
        if (hasMore)
            rows.RemoveAt(rows.Count - 1);

        var runs = rows.Select(RunDto.From).ToList();
        var nextCursor = hasMore && rows.Count > 0
            ? new CursorPosition(rows[^1].StartedAt, rows[^1].Id).Encode()
            : null;

        return ApiResults.Ok(new CursorPage<RunDto>(runs, nextCursor, range.PageSize));
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

    /// <summary>
    /// GET /api/checks/{id}/availability-series?window=24h|7d|30d|90d — uptime % per time bucket for
    /// graphing. ONE inline bucketed query that mirrors sla_availability's up=pass|warn /
    /// down=fail|error taxonomy + maintenance-window exclusion, so the series integrated over the
    /// window reconciles with the SLA panel's headline %. Empty buckets carry availabilityPct=null
    /// (a gap in the line, not 0%) with up/down = 0. Bucket: 24h->hour, 7d->6h, 30d/90d->day.
    /// </summary>
    [Function("GetAvailabilitySeries")]
    public async Task<IActionResult> GetAvailabilitySeries(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "checks/{id:long}/availability-series")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        if (!await _db.Checks.AnyAsync(c => c.Id == id, ct))
            return ApiResults.NotFound($"Check {id} not found.");

        var window = req.Query["window"].ToString();
        (string Stride, string Bucket, TimeSpan Span)? cfg = window switch
        {
            // Strides are FIXED-WIDTH (hours), never '1 day': generate_series treats '1 day' as a
            // DST-aware CALENDAR day but date_bin treats it as fixed 24h, so in a non-UTC session the
            // bucket grid and the per-bucket assignment drift ±1h across a DST boundary and the
            // a.ts=b.ts join silently drops runs (breaking reconciliation). '24 hours' bins identically
            // in both, in any session timezone. (Bucket label stays "day".)
            "" or "24h" => ("1 hour", "hour", TimeSpan.FromHours(24)),
            "7d" => ("6 hours", "6h", TimeSpan.FromDays(7)),
            "30d" => ("24 hours", "day", TimeSpan.FromDays(30)),
            "90d" => ("24 hours", "day", TimeSpan.FromDays(90)),
            _ => null
        };
        if (cfg is null)
            return ApiResults.BadRequest("window must be one of: 24h, 7d, 30d, 90d.");
        var (stride, bucket, span) = cfg.Value;

        var to = DateTimeOffset.UtcNow;
        var from = to - span;

        // Bucket grid (generate_series) LEFT JOIN per-bucket up/down. The inner aggregate mirrors
        // sla_availability exactly (same status taxonomy + maintenance anti-join), so summing the
        // series' up/down equals the SLA function's. date_bin (PG14+) anchors buckets to `from`; the
        // grid and the bins use the SAME fixed-width stride (hours, never '1 day') so they align in
        // any session timezone — see the stride note above. availability_pct is null for an empty bucket.
        var rows = await _db.AvailabilitySeries.FromSql(
            $"""
            SELECT b.ts AS ts,
                   coalesce(a.up_runs, 0)   AS up_runs,
                   coalesce(a.down_runs, 0) AS down_runs,
                   CASE WHEN coalesce(a.completed, 0) > 0
                        THEN round(100.0 * a.up_runs / a.completed, 4) END AS availability_pct
            FROM generate_series({from}, {to}, {stride}::interval) AS b(ts)
            LEFT JOIN (
                SELECT date_bin({stride}::interval, r.started_at, {from}) AS ts,
                       count(*) FILTER (WHERE r.status IN ('pass','warn','fail','error')) AS completed,
                       count(*) FILTER (WHERE r.status IN ('pass','warn'))                AS up_runs,
                       count(*) FILTER (WHERE r.status IN ('fail','error'))               AS down_runs
                FROM runs r
                LEFT JOIN maintenance_windows mw
                       ON (mw.check_id = r.check_id OR mw.check_id IS NULL)
                      AND r.started_at >= mw.starts_at AND r.started_at < mw.ends_at
                WHERE r.check_id = {id} AND r.started_at >= {from} AND r.started_at < {to}
                  AND mw.id IS NULL
                GROUP BY 1
            ) a ON a.ts = b.ts
            """)
            .AsNoTracking()
            .OrderBy(p => p.Ts)
            .ToListAsync(ct);

        var points = rows
            .Select(r => new AvailabilityPointDto(r.Ts, r.AvailabilityPct, r.UpRuns, r.DownRuns))
            .ToList();

        req.HttpContext.Response.Headers.CacheControl = "public, max-age=30";
        req.HttpContext.Response.Headers["Vary"] = "Origin";
        return ApiResults.Ok(new AvailabilitySeriesDto(
            Window: string.IsNullOrEmpty(window) ? "24h" : window,
            Bucket: bucket,
            Points: points));
    }
}
