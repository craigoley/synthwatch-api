using System.Text.Json;
using Azure.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Dtos;
using SynthWatch.Api.Functions;
using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>
/// DB-backed tests that exercise the real SQL (sla_availability function/views, the parity
/// lateral joins) and JSONB round-trips against a Testcontainers Postgres seeded with the runner's
/// schema. They call the function handlers directly (the isolated worker has no in-process
/// TestServer, so WebApplicationFactory isn't usable). Skip when Docker is unavailable.
/// </summary>
[Collection("postgres")]
public class IntegrationTests
{
    private readonly PostgresFixture _pg;
    public IntegrationTests(PostgresFixture pg) => _pg = pg;

    private void RequireDocker()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
    }

    private static HttpRequest Request(string? queryString = null)
    {
        var ctx = new DefaultHttpContext();
        if (queryString is not null) ctx.Request.QueryString = new QueryString(queryString);
        return ctx.Request;
    }

    private static HttpRequest JsonRequest(object body)
    {
        var ctx = new DefaultHttpContext();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentType = "application/json";
        ctx.Request.ContentLength = bytes.Length;
        return ctx.Request;
    }

    [SkippableFact]
    public async Task Health_returns_healthy_against_a_real_db()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        var fn = new HealthFunctions(db, NullLogger<HealthFunctions>.Instance);
        var result = await fn.Health(Request(), default);
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(200, ok.StatusCode ?? 200);
    }

    [SkippableFact]
    public async Task Health_returns_503_when_db_unreachable()
    {
        RequireDocker();
        await using var db = _pg.NewBrokenDbContext();
        var fn = new HealthFunctions(db, NullLogger<HealthFunctions>.Instance);
        var result = await fn.Health(Request(), default);
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, obj.StatusCode); // graceful, not a thrown 500
    }

    [SkippableFact]
    public async Task Checks_list_carries_parity_fields_from_the_lateral_sql()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        var fn = new ChecksFunctions(db);
        var ok = Assert.IsType<OkObjectResult>(await fn.ListChecks(Request(), default));
        var checks = Assert.IsAssignableFrom<IEnumerable<CheckSummaryDto>>(ok.Value!).ToList();

        var seed = Assert.Single(checks, c => c.Name == "seed-http");
        Assert.True(seed.Runs24h >= 25);          // 25 completed runs seeded in the last 24h
        Assert.NotNull(seed.P50Ms);               // percentile_cont over duration_ms
        Assert.NotNull(seed.P95Ms);
        Assert.NotEmpty(seed.Spark);              // json_agg sparkline
        Assert.Equal(0, seed.OpenIncidentCount);  // the seeded incident is resolved
        Assert.Null(seed.MaxOpenSeverity);
        // Per-location rollup: the seed runs are single-location -> one "default" entry.
        Assert.Contains(seed.Locations, l => l.Location == "default");
    }

    [SkippableFact]
    public async Task Check_detail_serves_slo_when_target_set_and_null_otherwise()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        // Self-contained: a check WITH slo_target=0.99 + 100 runs over ~4 days, 5 of them fail
        // (budget = 100 * 0.01 = 1; consumed = 5 => over budget, burn > 0). started_at is
        // ValueGeneratedOnAdd, so seed via raw SQL.
        await db.Database.ExecuteSqlRawAsync("""
            DO $$
            DECLARE cid bigint;
            BEGIN
              INSERT INTO checks (name, kind, target_url, slo_target)
                VALUES ('slo-test', 'http', 'https://s.example', 0.99) RETURNING id INTO cid;
              FOR i IN 1..100 LOOP
                INSERT INTO runs (check_id, status, started_at, finished_at, duration_ms)
                  VALUES (cid, CASE WHEN i <= 5 THEN 'fail' ELSE 'pass' END,
                          now() - (i || ' hours')::interval, now(), 40);
              END LOOP;
            END $$;
            """);
        try
        {
            var fn = new ChecksFunctions(db);
            var sloId = await db.Checks.Where(c => c.Name == "slo-test").Select(c => c.Id).FirstAsync();

            var ok = Assert.IsType<OkObjectResult>(await fn.GetCheck(Request(), sloId, default));
            var d = Assert.IsType<CheckDetailDto>(ok.Value!);
            Assert.NotNull(d.Slo);
            Assert.Equal(0.99, (double)d.Slo!.Target, 2);
            Assert.True(d.Slo.Budget > 0);     // 100 * (1 - 0.99) = 1
            Assert.Equal(5, d.Slo.Consumed);   // 5 fail runs in the 30d window
            Assert.True(d.Slo.BurnRate > 0);   // consumed > budget -> burning

            // A check with NO slo_target -> slo is null (opt-in; not fabricated).
            var seedId = await db.Checks.Where(c => c.Name == "seed-http").Select(c => c.Id).FirstAsync();
            var ok2 = Assert.IsType<OkObjectResult>(await fn.GetCheck(Request(), seedId, default));
            Assert.Null(Assert.IsType<CheckDetailDto>(ok2.Value!).Slo);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM checks WHERE name = 'slo-test';");
        }
    }

    [SkippableFact]
    public async Task Check_detail_does_not_500_when_slo_target_is_1_0()
    {
        // Regression (orphaned #47 finding): slo_status divides by (1 - slo_target), so a check with
        // slo_target = 1.0 AND runs in the window made the function div-by-zero -> GetCheck 500. The
        // API now guards (computes SLO only for a target in (0,1)) and serves Slo = null instead.
        RequireDocker();
        await using var db = _pg.NewDbContext();
        await db.Database.ExecuteSqlRawAsync("""
            DO $$
            DECLARE cid bigint;
            BEGIN
              INSERT INTO checks (name, kind, target_url, slo_target)
                VALUES ('slo-100', 'http', 'https://s.example', 1.0) RETURNING id INTO cid;
              INSERT INTO runs (check_id, status, started_at, finished_at, duration_ms)
                VALUES (cid, 'fail', now() - interval '1 hour', now(), 40),
                       (cid, 'pass', now() - interval '2 hours', now(), 40);
            END $$;
            """);
        try
        {
            var fn = new ChecksFunctions(db);
            var id = await db.Checks.Where(c => c.Name == "slo-100").Select(c => c.Id).FirstAsync();
            var ok = Assert.IsType<OkObjectResult>(await fn.GetCheck(Request(), id, default)); // 200, not 500
            Assert.Null(Assert.IsType<CheckDetailDto>(ok.Value!).Slo);                          // SLO skipped
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM checks WHERE name = 'slo-100';");
        }
    }

    [SkippableFact]
    public async Task Sla_24h_window_is_sufficient_30d_is_insufficient()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        var fn = new SlaFunctions(db);

        // 24h: ~10-day-old check with 25 completed runs, full coverage -> real % + fleet.
        var ok24 = Assert.IsType<OkObjectResult>(await fn.GetSla(Request("?window=24h"), default));
        var r24 = Assert.IsType<SlaResponseDto>(ok24.Value!);
        var item24 = Assert.Single(r24.Items, i => i.CheckName == "seed-http");
        Assert.False(item24.InsufficientData);
        Assert.NotNull(item24.AvailabilityPct);
        Assert.False(r24.Fleet.InsufficientData);
        Assert.NotNull(r24.Fleet.AvailabilityPct);

        // 30d: same data, but the check is only ~10 days old -> <80% coverage -> insufficient.
        var ok30 = Assert.IsType<OkObjectResult>(await fn.GetSla(Request("?window=30d"), default));
        var r30 = Assert.IsType<SlaResponseDto>(ok30.Value!);
        var item30 = Assert.Single(r30.Items, i => i.CheckName == "seed-http");
        Assert.True(item30.InsufficientData);
        Assert.Null(item30.AvailabilityPct);      // misleading precise % suppressed
        Assert.True(item30.CompletedRuns > 0);    // raw counts still present

        // 90d: the window is served (sla_availability_90d); the ~10-day-old check is even thinner
        // coverage -> insufficient ("building baseline"), raw counts still present.
        var ok90 = Assert.IsType<OkObjectResult>(await fn.GetSla(Request("?window=90d"), default));
        var r90 = Assert.IsType<SlaResponseDto>(ok90.Value!);
        Assert.Equal("90d", r90.Window);
        var item90 = Assert.Single(r90.Items, i => i.CheckName == "seed-http");
        Assert.True(item90.InsufficientData);
        Assert.Null(item90.AvailabilityPct);
        Assert.True(item90.CompletedRuns > 0);
    }

    [SkippableFact]
    public async Task Sla_rejects_bad_window()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        var fn = new SlaFunctions(db);
        var result = await fn.GetSla(Request("?window=banana"), default);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, bad.StatusCode);
    }

    [SkippableFact]
    public async Task Availability_series_buckets_nulls_and_reconciles_with_sla()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        // 4 runs (3 pass, 1 fail) across a few hours in the last 24h; the rest of the 24h is empty.
        await db.Database.ExecuteSqlRawAsync("""
            DO $$
            DECLARE cid bigint;
            BEGIN
              INSERT INTO checks (name, kind, target_url)
                VALUES ('avail-test', 'http', 'https://a.example') RETURNING id INTO cid;
              INSERT INTO runs (check_id, status, started_at, finished_at, duration_ms) VALUES
                (cid, 'pass', now() - interval '1 hour',  now() - interval '1 hour',  40),
                (cid, 'pass', now() - interval '2 hours', now() - interval '2 hours', 40),
                (cid, 'fail', now() - interval '2 hours', now() - interval '2 hours', 40),
                (cid, 'pass', now() - interval '5 hours', now() - interval '5 hours', 40);
            END $$;
            """);
        try
        {
            var cid = await db.Checks.Where(c => c.Name == "avail-test").Select(c => c.Id).FirstAsync();
            var checks = new ChecksFunctions(db);

            var okS = Assert.IsType<OkObjectResult>(await checks.GetAvailabilitySeries(Request("?window=24h"), cid, default));
            var series = Assert.IsType<AvailabilitySeriesDto>(okS.Value!);
            Assert.Equal("24h", series.Window);
            Assert.Equal("hour", series.Bucket);

            // No-data buckets are null (a gap), NOT 0% — most of the 24h has no runs.
            Assert.Contains(series.Points, p => p.AvailabilityPct is null && p.UpRuns == 0 && p.DownRuns == 0);
            // A bucket with runs carries a non-null pct + counts.
            Assert.Contains(series.Points, p => p.AvailabilityPct is not null && p.UpRuns + p.DownRuns > 0);
            // Points are ascending by ts.
            Assert.True(series.Points.SequenceEqual(series.Points.OrderBy(p => p.Ts)));

            // Reconcile with the SLA panel for the SAME 24h window: identical up/down totals (same
            // taxonomy + maintenance exclusion), so the graph can't disagree with the headline %.
            var seriesUp = series.Points.Sum(p => p.UpRuns);
            var seriesDown = series.Points.Sum(p => p.DownRuns);
            var okSla = Assert.IsType<OkObjectResult>(await new SlaFunctions(db).GetSla(Request("?window=24h"), default));
            var slaItem = Assert.Single(Assert.IsType<SlaResponseDto>(okSla.Value!).Items, i => i.CheckId == cid);
            Assert.Equal(slaItem.UpRuns, seriesUp);
            Assert.Equal(slaItem.DownRuns, seriesDown);
            Assert.Equal(3, seriesUp);    // 3 pass
            Assert.Equal(1, seriesDown);  // 1 fail
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM checks WHERE name = 'avail-test';");
        }
    }

    [SkippableFact]
    public async Task Availability_series_404_and_bad_window()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        var fn = new ChecksFunctions(db);
        Assert.IsType<NotFoundObjectResult>(await fn.GetAvailabilitySeries(Request("?window=24h"), 999999, default));

        var seedId = await db.Checks.Where(c => c.Name == "seed-http").Select(c => c.Id).FirstAsync();
        var bad = await fn.GetAvailabilitySeries(Request("?window=banana"), seedId, default);
        Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(bad).StatusCode);
    }

    [SkippableFact]
    public async Task Availability_day_buckets_reconcile_in_non_utc_session_across_dst()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        // A run "survives" the series only if its date_bin(stride) bucket is a generate_series(stride)
        // grid point (the handler's a.ts = b.ts join). Count survivors for a given stride.
        async Task<long> Survivors(string stride) => Convert.ToInt64(await Scalar(conn,
            $"""
            SELECT count(*) FROM runs r
            WHERE r.check_id = (SELECT id FROM checks WHERE name='dst-test')
              AND r.started_at >= '2026-03-07 00:00 America/New_York'
              AND r.started_at <  '2026-03-12 00:00 America/New_York'
              AND date_bin('{stride}'::interval, r.started_at, '2026-03-07 00:00 America/New_York'::timestamptz)
                  IN (SELECT g FROM generate_series('2026-03-07 00:00 America/New_York'::timestamptz,
                                                     '2026-03-12 00:00 America/New_York'::timestamptz,
                                                     '{stride}'::interval) g)
            """));
        try
        {
            // Non-UTC session spanning the 2026 US spring-forward (Mar 8). The day-bucket grid
            // (generate_series) and per-bucket assignment (date_bin) must share a fixed-24h basis,
            // else runs across the DST boundary are dropped by the join (the #49 latent bug).
            await Exec(conn, "SET TIME ZONE 'America/New_York'");
            await Exec(conn, "INSERT INTO checks (name, kind, target_url) VALUES ('dst-test','http','https://d.example')");
            var cid = Convert.ToInt64(await Scalar(conn, "SELECT id FROM checks WHERE name='dst-test'"));
            // 3 runs on POST-DST days (noon, unambiguous), absolute NY timestamps.
            await Exec(conn, $"""
                INSERT INTO runs (check_id, status, started_at, finished_at, duration_ms) VALUES
                  ({cid},'pass','2026-03-09 12:00 America/New_York','2026-03-09 12:00 America/New_York',40),
                  ({cid},'fail','2026-03-10 12:00 America/New_York','2026-03-10 12:00 America/New_York',40),
                  ({cid},'pass','2026-03-11 12:00 America/New_York','2026-03-11 12:00 America/New_York',40)
                """);

            // FIX: fixed-24h stride (the handler's day stride) — every run buckets onto a grid point,
            // so none is dropped and the series reconciles with the SLA count.
            Assert.Equal(3, await Survivors("24 hours"));
            // BUG (pre-fix): calendar '1 day' diverges from date_bin across the DST boundary -> the
            // post-DST runs' bins are not grid points -> dropped -> series would NOT reconcile.
            Assert.True(await Survivors("1 day") < 3,
                "'1 day' must drop post-DST runs in a non-UTC session (the divergence the '24 hours' fix removes)");
        }
        finally
        {
            await Exec(conn, "SET TIME ZONE 'UTC'");
            await Exec(conn, "DELETE FROM checks WHERE name='dst-test'");
            await conn.CloseAsync();
        }
    }

    private static async Task Exec(NpgsqlConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> Scalar(NpgsqlConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    [SkippableFact]
    public async Task Incidents_carry_check_name_and_kind()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        var fn = new IncidentsFunctions(db);
        var ok = Assert.IsType<OkObjectResult>(await fn.ListIncidents(Request(), default));
        var incidents = Assert.IsAssignableFrom<IEnumerable<IncidentDto>>(ok.Value!).ToList();
        var inc = Assert.Single(incidents);
        Assert.Equal("seed-http", inc.CheckName);
        Assert.Equal("http", inc.CheckKind);
    }

    [SkippableFact]
    public async Task Incident_with_missing_check_still_appears_via_left_join()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        // incidents.check_id has ON DELETE CASCADE, so an orphan can't arise normally — simulate the
        // (defensive) orphan state via an FK-bypassed insert (session_replication_role=replica),
        // then assert the LEFT JOIN still surfaces it with a null check name (INNER JOIN would drop it).
        await db.Database.ExecuteSqlRawAsync(
            "SET session_replication_role = replica; " +
            "INSERT INTO incidents (check_id, status, severity, opened_at) " +
            "VALUES (999999, 'open', 'critical', now()); " +
            "SET session_replication_role = origin;");
        try
        {
            var fn = new IncidentsFunctions(db);
            var ok = Assert.IsType<OkObjectResult>(await fn.ListIncidents(Request(), default));
            var incidents = Assert.IsAssignableFrom<IEnumerable<IncidentDto>>(ok.Value!).ToList();

            var orphan = Assert.Single(incidents, i => i.CheckId == 999999);
            Assert.Null(orphan.CheckName);  // LEFT JOIN -> null name, but the incident SURVIVES
            Assert.Null(orphan.CheckKind);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM incidents WHERE check_id = 999999;");
        }
    }

    [SkippableFact]
    public async Task Incident_detail_unknown_id_returns_404()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        var fn = new IncidentsFunctions(db);
        var result = await fn.GetIncident(Request(), 999999, default);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [SkippableFact]
    public async Task Incident_detail_returns_timeline_recurrence_rca_and_open_case()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        // Self-contained fixture: opened_at/started_at are ValueGeneratedOnAdd, so set them via raw
        // SQL. One check; an older resolved incident (recurrence), a current resolved incident WITH
        // rca, and an open incident; runs in/around the current window + the open window.
        await db.Database.ExecuteSqlRawAsync("""
            DO $$
            DECLARE cid bigint;
            BEGIN
              INSERT INTO checks (name, kind, target_url)
                VALUES ('detail-test', 'http', 'https://d.example') RETURNING id INTO cid;
              INSERT INTO incidents (check_id, status, severity, opened_at, resolved_at, consecutive_failures)
                VALUES (cid, 'resolved', 'critical', now() - interval '2 days',
                        now() - interval '2 days' + interval '10 min', 2);
              INSERT INTO incidents (check_id, status, severity, opened_at, resolved_at, consecutive_failures, rca)
                VALUES (cid, 'resolved', 'critical', now() - interval '30 min', now() - interval '10 min', 3,
                        jsonb_build_object(
                          'classification', 'real-outage', 'confidence', 'high',
                          'observed', jsonb_build_array('HTTP 503'),
                          'inferred', jsonb_build_array('origin down'),
                          'summary', 's'));
              INSERT INTO incidents (check_id, status, severity, opened_at, consecutive_failures)
                VALUES (cid, 'open', 'critical', now() - interval '5 min', 1);
              INSERT INTO runs (check_id, status, started_at, finished_at, duration_ms) VALUES
                (cid, 'fail', now() - interval '25 min', now() - interval '25 min', 50),   -- current window
                (cid, 'fail', now() - interval '15 min', now() - interval '15 min', 60),   -- current window
                (cid, 'fail', now() - interval '31 min', now() - interval '31 min', 40),   -- lead (just before opened)
                (cid, 'fail', now() - interval '2 min',  now() - interval '2 min',  70);   -- open window
            END $$;
            """);
        try
        {
            var checkId = await db.Checks.Where(c => c.Name == "detail-test").Select(c => c.Id).FirstAsync();
            var incs = await db.Incidents.Where(i => i.CheckId == checkId)
                .OrderByDescending(i => i.OpenedAt).Select(i => i.Id).ToListAsync();
            var openId = incs[0];     // opened 5 min ago (newest)
            var currentId = incs[1];  // opened 30 min ago (resolved, has rca)
            var olderId = incs[2];    // opened 2 days ago

            var fn = new IncidentsFunctions(db);

            // --- current resolved incident: timeline + recurrence + rca + duration ---
            var ok = Assert.IsType<OkObjectResult>(await fn.GetIncident(Request(), currentId, default));
            var d = Assert.IsType<IncidentDetailDto>(ok.Value!);
            Assert.Equal("detail-test", d.CheckName);
            Assert.Equal("http", d.CheckKind);
            Assert.NotNull(d.Rca);
            Assert.Equal("real-outage", d.Rca!.Classification);
            Assert.Equal("high", d.Rca.Confidence);
            Assert.NotNull(d.DurationSeconds);                 // resolved -> non-null (~1200s)
            Assert.True(d.DurationSeconds > 0);
            // 2 in-window runs + 1 lead run, ASC by startedAt
            Assert.Equal(3, d.Timeline.Count);
            Assert.True(d.Timeline.SequenceEqual(d.Timeline.OrderBy(t => t.StartedAt)));
            // recurrence: the older incident, excluding self
            Assert.Contains(d.Recurrence, r => r.Id == olderId);
            Assert.DoesNotContain(d.Recurrence, r => r.Id == currentId);

            // --- open incident: durationSeconds null, resolvedAt null, window runs through now() ---
            var okOpen = Assert.IsType<OkObjectResult>(await fn.GetIncident(Request(), openId, default));
            var open = Assert.IsType<IncidentDetailDto>(okOpen.Value!);
            Assert.Null(open.ResolvedAt);
            Assert.Null(open.DurationSeconds);
            Assert.Contains(open.Timeline, t => t.DurationMs == 70); // the in-window run (now-2min)
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM checks WHERE name = 'detail-test';"); // cascades incidents + runs
        }
    }

    [SkippableFact]
    public async Task Incident_detail_perlocation_is_window_scoped_and_null_safe()
    {
        // #46: a RESOLVED incident's perLocation must reflect the status DURING its window
        // [opened_at, resolved_at], not the present. #39: a null-location run coalesces to "default",
        // not its own bogus group. Seed: a resolved incident whose window has eastus=fail / westus=fail
        // / (null-location)=fail, then an AFTER-window eastus=pass (recovery) that must NOT leak in.
        RequireDocker();
        await using var db = _pg.NewDbContext();
        await db.Database.ExecuteSqlRawAsync("""
            DO $$
            DECLARE cid bigint;
            BEGIN
              INSERT INTO checks (name, kind, target_url)
                VALUES ('perloc-test', 'http', 'https://p.example') RETURNING id INTO cid;
              INSERT INTO incidents (check_id, status, severity, opened_at, resolved_at, consecutive_failures)
                VALUES (cid, 'resolved', 'critical', now() - interval '1 day',
                        now() - interval '1 day' + interval '30 min', 0);
              -- IN-WINDOW runs (one per location; one with an explicit NULL location)
              INSERT INTO runs (check_id, status, started_at, finished_at, duration_ms, location) VALUES
                (cid, 'fail', now() - interval '1 day' + interval '10 min', now() - interval '1 day' + interval '10 min', 50, 'eastus'),
                (cid, 'fail', now() - interval '1 day' + interval '12 min', now() - interval '1 day' + interval '12 min', 50, 'westus'),
                (cid, 'fail', now() - interval '1 day' + interval '14 min', now() - interval '1 day' + interval '14 min', 50, NULL);
              -- AFTER the window: eastus recovered. This is the CURRENT status; it must NOT appear.
              INSERT INTO runs (check_id, status, started_at, finished_at, duration_ms, location)
                VALUES (cid, 'pass', now() - interval '1 hour', now() - interval '1 hour', 50, 'eastus');
            END $$;
            """);
        try
        {
            var checkId = await db.Checks.Where(c => c.Name == "perloc-test").Select(c => c.Id).FirstAsync();
            var incId = await db.Incidents.Where(i => i.CheckId == checkId).Select(i => i.Id).FirstAsync();

            var fn = new IncidentsFunctions(db);
            var ok = Assert.IsType<OkObjectResult>(await fn.GetIncident(Request(), incId, default));
            var d = Assert.IsType<IncidentDetailDto>(ok.Value!);

            // Three locations, ordinal-sorted, all null-safe ("default" not null/empty).
            Assert.Equal(new[] { "default", "eastus", "westus" }, d.PerLocation.Select(p => p.Location));
            Assert.All(d.PerLocation, p => Assert.False(string.IsNullOrEmpty(p.Location)));
            // eastus reflects the IN-WINDOW status (fail), NOT the post-window recovery (pass).
            Assert.Equal("fail", d.PerLocation.Single(p => p.Location == "eastus").Status);
            // The null-location run surfaced as "default".
            Assert.Equal("fail", d.PerLocation.Single(p => p.Location == "default").Status);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM checks WHERE name = 'perloc-test';");
        }
    }

    [SkippableFact]
    public async Task Run_without_a_trace_returns_404()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        var fn = new RunsFunctions(db, new DefaultAzureCredential(), NullLogger<RunsFunctions>.Instance);
        var runId = await db.Runs.Where(r => r.TraceUrl == null).Select(r => r.Id).FirstAsync();
        var result = await fn.GetRunTrace(Request(), runId, default);
        Assert.IsType<NotFoundObjectResult>(result); // no trace_url -> 404 before any blob call
    }

    [SkippableFact]
    public async Task Check_with_jsonb_config_round_trips_through_real_postgres()
    {
        RequireDocker();
        // Build the entity through the real create path (TryBuildNew applies all the scalar
        // defaults the DB constraints require), then persist — so this exercises the production
        // write path AND the JSONB value-converter round-trip.
        var req = new CreateCheckRequest
        {
            Name = "jsonb-roundtrip", Kind = "multistep", TargetUrl = "https://example.com",
            Auth = new() { ["type"] = "bearer", ["token_env"] = "T_ENV" },
            Steps = new()
            {
                new ChainStep
                {
                    Name = "login", Method = "POST", Url = "https://example.com/login",
                    Auth = new() { ["type"] = "basic", ["password_env"] = "PW_ENV" },
                    Extract = new() { new ExtractRule { Var = "token", JsonPath = "$.t" } }
                }
            }
        };
        Assert.True(CheckValidation.TryBuildNew(req, out var newCheck, out _));

        long id;
        await using (var db = _pg.NewDbContext())
        {
            db.Checks.Add(newCheck);
            await db.SaveChangesAsync();
            id = db.Checks.Single(c => c.Name == "jsonb-roundtrip").Id;
        }

        await using (var db = _pg.NewDbContext()) // fresh context => actually reads from Postgres
        {
            var c = await db.Checks.SingleAsync(c => c.Id == id);
            Assert.Single(c.Steps!);
            Assert.Equal("login", c.Steps![0].Name);
            Assert.Equal("PW_ENV", c.Steps[0].Auth!["password_env"]);
            Assert.Equal("token", c.Steps[0].Extract![0].Var);
            Assert.Equal("T_ENV", c.Auth!["token_env"]);

            db.Checks.Remove(c);
            await db.SaveChangesAsync(); // clean up so reruns stay deterministic
        }
    }

    [SkippableFact]
    public async Task Create_check_seeds_default_location_cursor_at_create_time()
    {
        // 4-MLACT step 2: the POST handler seeds per-location cadence cursors AT create (replicating the
        // runner's assignDefaultLocations()), not implicitly on first run. With only 'default' active,
        // this is exactly one 'default' cursor — identical to today's lazy-insert, just explicit.
        RequireDocker();
        await using var db = _pg.NewDbContext();
        var fn = new ChecksFunctions(db);
        var body = new CreateCheckRequest { Name = "loc-seed-test", Kind = "http", TargetUrl = "https://l.example" };

        var res = Assert.IsType<ObjectResult>(await fn.CreateCheck(JsonRequest(body), default));
        Assert.Equal(201, res.StatusCode);
        var id = Assert.IsType<CheckDetailDto>(res.Value!).Id;

        await using var db2 = _pg.NewDbContext(); // fresh connection => reads committed state
        var conn = (NpgsqlConnection)db2.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            // (1) the cursor exists immediately AT CREATE (not after first run);
            // (2) backward-compat: exactly one 'default' cursor with only 'default' active.
            Assert.Equal(1L, Convert.ToInt64(await Scalar(conn, $"SELECT count(*) FROM check_locations WHERE check_id = {id}")));
            Assert.Equal("default", (string?)await Scalar(conn, $"SELECT location FROM check_locations WHERE check_id = {id}"));
            // last_run_at is NULL — matches assignDefaultLocations; the #68 claim loop's IS-NULL arm
            // treats that as due-now, so the first run behaves exactly like today's no-cursor check.
            Assert.True((bool)(await Scalar(conn, $"SELECT last_run_at IS NULL FROM check_locations WHERE check_id = {id}"))!);

            // (3) idempotency: replay the runner's lazy-insert hitting the same cursor -> ON CONFLICT
            // DO NOTHING no-ops (0 rows, no error), so API seeding + runner lazy-insert coexist safely.
            await using var dup = conn.CreateCommand();
            dup.CommandText = $"INSERT INTO check_locations (check_id, location) SELECT {id}, name FROM locations WHERE enabled ON CONFLICT (check_id, location) DO NOTHING";
            Assert.Equal(0, await dup.ExecuteNonQueryAsync());
        }
        finally
        {
            await using var del = conn.CreateCommand();
            del.CommandText = $"DELETE FROM checks WHERE id = {id}"; // cascades check_locations
            await del.ExecuteNonQueryAsync();
            await conn.CloseAsync();
        }
    }

    [SkippableFact]
    public async Task Location_endpoints_list_get_and_set_with_validation()
    {
        // 4-MLACT: the selector's API half. Seed extra registry locations (2 enabled, 1 disabled) + a
        // check assigned to {default, eastus2} with a known eastus2 cursor, then exercise all three
        // endpoints + validation. eastus2 keeps its cursor across a PUT (set preserved, cadence not reset).
        RequireDocker();
        await using var db = _pg.NewDbContext();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO locations (name, enabled) VALUES ('eastus2', true), ('centralus', true), ('westus', false)
              ON CONFLICT (name) DO NOTHING;
            DO $$
            DECLARE cid bigint;
            BEGIN
              INSERT INTO checks (name, kind, target_url) VALUES ('loc-ep-test','http','https://le.example') RETURNING id INTO cid;
              INSERT INTO check_locations (check_id, location, last_run_at) VALUES
                (cid, 'default', NULL),
                (cid, 'eastus2', timestamptz '2026-01-01 00:00:00+00');
            END $$;
            """);
        var id = await db.Checks.Where(c => c.Name == "loc-ep-test").Select(c => c.Id).FirstAsync();
        var fn = new LocationsFunctions(db);
        try
        {
            // GET /api/locations -> registry with enabled flags.
            var reg = Assert.IsType<LocationsResponse>(
                Assert.IsType<OkObjectResult>(await fn.GetLocations(Request(), default)).Value!);
            Assert.True(reg.Locations.Single(l => l.Name == "eastus2").Enabled);
            Assert.False(reg.Locations.Single(l => l.Name == "westus").Enabled);

            // GET /api/checks/{id}/locations -> the current set (sorted).
            var cur = Assert.IsType<CheckLocationsResponse>(
                Assert.IsType<OkObjectResult>(await fn.GetCheckLocations(Request(), id, default)).Value!);
            Assert.Equal(new[] { "default", "eastus2" }, cur.Locations);

            // PUT to {eastus2, centralus}: drops 'default', keeps eastus2, adds centralus.
            var put = Assert.IsType<CheckLocationsResponse>(Assert.IsType<OkObjectResult>(
                await fn.SetCheckLocations(JsonRequest(new SetLocationsRequest { Locations = new() { "centralus", "eastus2" } }), id, default)).Value!);
            Assert.Equal(new[] { "centralus", "eastus2" }, put.Locations);

            await using var db2 = _pg.NewDbContext(); // fresh read of committed cursors
            var rows = await db2.CheckLocations.AsNoTracking().Where(cl => cl.CheckId == id)
                .OrderBy(cl => cl.Location).ToListAsync();
            Assert.Equal(new[] { "centralus", "eastus2" }, rows.Select(r => r.Location));
            Assert.Null(rows.Single(r => r.Location == "centralus").LastRunAt);                  // newly added -> due-now
            Assert.NotNull(rows.Single(r => r.Location == "eastus2").LastRunAt);                 // kept -> cursor preserved (cadence NOT reset)

            // Validation: disabled location -> 400.
            Assert.IsType<BadRequestObjectResult>(
                await fn.SetCheckLocations(JsonRequest(new SetLocationsRequest { Locations = new() { "westus" } }), id, default));
            // Validation: unknown location -> 400.
            Assert.IsType<BadRequestObjectResult>(
                await fn.SetCheckLocations(JsonRequest(new SetLocationsRequest { Locations = new() { "atlantis" } }), id, default));
            // Validation: empty set -> 400 (a check must run from >= 1 location).
            Assert.IsType<BadRequestObjectResult>(
                await fn.SetCheckLocations(JsonRequest(new SetLocationsRequest { Locations = new() }), id, default));

            // A rejected PUT changed nothing.
            var after = await db2.CheckLocations.AsNoTracking().Where(cl => cl.CheckId == id).CountAsync();
            Assert.Equal(2, after);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM checks WHERE name = 'loc-ep-test'; DELETE FROM locations WHERE name IN ('eastus2','centralus','westus');");
        }
    }

    [SkippableFact]
    public async Task Channel_crud_and_config_validation()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        var ch = new ChannelsFunctions(db);
        try
        {
            // Create email (to[]+from) + webhook (url).
            var em = Assert.IsType<ObjectResult>(await ch.CreateChannel(JsonRequest(new ChannelWriteRequest {
                Name = "ops-email", Type = "email",
                Config = new ChannelConfig { To = new() { "ops@x.com" } } }), default));
            Assert.Equal(201, em.StatusCode);
            var emailId = Assert.IsType<ChannelDto>(em.Value!).Id;
            var wh = Assert.IsType<ObjectResult>(await ch.CreateChannel(JsonRequest(new ChannelWriteRequest {
                Name = "ops-hook", Type = "webhook", Config = new ChannelConfig { Url = "https://hooks.x.com/y" } }), default));
            var hookId = Assert.IsType<ChannelDto>(wh.Value!).Id;

            // created_at is DB-generated (ValueGeneratedOnAdd) — NOT the 0001-01-01 CLR default that an
            // un-marked mapping would write over the DEFAULT now(). (Verified via the entity; not in the DTO.)
            await using (var db2 = _pg.NewDbContext())
            {
                var createdAt = await db2.Channels.Where(c => c.Id == emailId).Select(c => c.CreatedAt).FirstAsync();
                Assert.True(createdAt.Year >= 2024, $"created_at should be DB now(), was {createdAt:o}");
            }

            // List includes both (config round-trips).
            var list = Assert.IsAssignableFrom<IEnumerable<ChannelDto>>(
                Assert.IsType<OkObjectResult>(await ch.GetChannels(Request(), default)).Value!).ToList();
            Assert.Contains(list, c => c.Id == emailId && c.Config.To!.Contains("ops@x.com"));
            Assert.Contains(list, c => c.Id == hookId && c.Config.Url == "https://hooks.x.com/y");

            // Update: new recipients + disable.
            var upd = Assert.IsType<ChannelDto>(Assert.IsType<OkObjectResult>(await ch.UpdateChannel(JsonRequest(new ChannelWriteRequest {
                Name = "ops-email", Type = "email", Enabled = false,
                Config = new ChannelConfig { To = new() { "a@x.com", "b@x.com" } } }), emailId, default)).Value!);
            Assert.False(upd.Enabled);
            Assert.Equal(2, upd.Config.To!.Count);

            // Validation: email without to[] -> 400; webhook without url -> 400.
            Assert.IsType<BadRequestObjectResult>(await ch.CreateChannel(JsonRequest(new ChannelWriteRequest {
                Name = "bad-email", Type = "email", Config = new ChannelConfig() }), default)); // no to[] -> 400
            Assert.IsType<BadRequestObjectResult>(await ch.CreateChannel(JsonRequest(new ChannelWriteRequest {
                Name = "bad-hook", Type = "webhook", Config = new ChannelConfig() }), default));
            // No-secret: an ACS connection string anywhere in config -> 400.
            Assert.IsType<BadRequestObjectResult>(await ch.CreateChannel(JsonRequest(new ChannelWriteRequest {
                Name = "bad-secret", Type = "webhook",
                Config = new ChannelConfig { Url = "https://h", AuthHeader = "endpoint=https://x.communication.azure.com/;accesskey=Zm9vYmFy==" } }), default));

            // Delete (not referenced) -> 204.
            Assert.IsType<NoContentResult>(await ch.DeleteChannel(Request(), hookId, default));
            Assert.IsType<NoContentResult>(await ch.DeleteChannel(Request(), emailId, default));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM channels WHERE name IN ('ops-email','ops-hook','bad-email','bad-hook','bad-secret');");
        }
    }

    [SkippableFact]
    public async Task Routing_get_set_validation_and_delete_guard()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        var rt = new RoutingFunctions(db);
        var ch = new ChannelsFunctions(db);
        try
        {
            // GET assembles the seeded routing: critical+warning -> [1,2] (channels 1=email,2=webhook).
            var got = Assert.IsType<RoutingDto>(Assert.IsType<OkObjectResult>(await rt.GetRouting(Request(), default)).Value!);
            Assert.Equal(new long[] { 1, 2 }, got.Severity!["critical"].ChannelIds);
            Assert.Equal(new long[] { 1, 2 }, got.Severity!["warning"].ChannelIds);

            // PUT with an unknown channelId -> 400 (referential validation).
            Assert.IsType<BadRequestObjectResult>(await rt.SetRouting(JsonRequest(new RoutingDto {
                Severity = new() { ["critical"] = new ChannelIdsDto(new long[] { 999999 }) } }), default));
            // PUT with an unknown severity -> 400.
            Assert.IsType<BadRequestObjectResult>(await rt.SetRouting(JsonRequest(new RoutingDto {
                Severity = new() { ["info"] = new ChannelIdsDto(new long[] { 1 }) } }), default));

            // PUT a valid config (severity defaults + a per-check override) -> replaces, returns it.
            var checkId = await db.Checks.Where(c => c.Name == "seed-http").Select(c => c.Id).FirstAsync();
            var put = Assert.IsType<RoutingDto>(Assert.IsType<OkObjectResult>(await rt.SetRouting(JsonRequest(new RoutingDto {
                Severity = new() { ["critical"] = new ChannelIdsDto(new long[] { 1 }), ["warning"] = new ChannelIdsDto(new long[] { 2 }) },
                PerCheck = new() { [checkId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new ChannelIdsDto(new long[] { 1 }) } }), default)).Value!);
            Assert.Equal(new long[] { 1 }, put.Severity!["critical"].ChannelIds);
            Assert.Equal(new long[] { 2 }, put.Severity!["warning"].ChannelIds);
            Assert.Equal(new long[] { 1 }, put.PerCheck![checkId.ToString(System.Globalization.CultureInfo.InvariantCulture)].ChannelIds);

            // Delete-guard: channel 1 is now referenced (critical + the per-check override) -> 409 (DB FK
            // is CASCADE, so the API enforces the block).
            Assert.IsType<ConflictObjectResult>(await ch.DeleteChannel(Request(), 1, default));
        }
        finally
        {
            // Restore the seeded routing (PUT replaces ALL rows; keep the shared snapshot baseline intact).
            await db.Database.ExecuteSqlRawAsync("""
                DELETE FROM alert_routes;
                INSERT INTO alert_routes (severity, channel_id)
                  SELECT s, c.id FROM (VALUES ('critical'), ('warning')) v(s) CROSS JOIN channels c WHERE c.name IN ('email','webhook');
                """);
        }
    }
}
