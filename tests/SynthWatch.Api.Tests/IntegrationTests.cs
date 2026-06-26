using System.Text.Json;
using System.Text.RegularExpressions;
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
    public async Task Notifications_readiness_reports_db_config_and_unknown_transport()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        var fn = new NotificationsFunctions(db);
        var ch = new ChannelsFunctions(db);
        try
        {
            // Routing is seeded (alert_routes); the test process has no ACS env, so transport is
            // genuinely UNKNOWN (null) — the API never asserts a transport state it can't see.
            var baseDto = Assert.IsType<NotificationsReadinessDto>(
                Assert.IsType<OkObjectResult>(await fn.Readiness(Request(), default)).Value!);
            Assert.True(baseDto.RoutingConfigured);
            Assert.Null(baseDto.TransportConfigured);

            // Add a DELIVERABLE channel (email with a recipient) → channelsConfigured flips true
            // (the seeded channels carry empty config, so this proves the deliverability check).
            await ch.CreateChannel(JsonRequest(new {
                name = "readiness-deliverable", type = "email", config = new { to = new[] { "x@y.com" } } }), default);
            var withCh = Assert.IsType<NotificationsReadinessDto>(
                Assert.IsType<OkObjectResult>(await fn.Readiness(Request(), default)).Value!);
            Assert.True(withCh.ChannelsConfigured);

            // With the ACS env present (e.g. set on the API too), transport reports configured.
            Environment.SetEnvironmentVariable("ACS_EMAIL_CONNECTION_STRING", "endpoint=https://x;accesskey=y");
            Environment.SetEnvironmentVariable("ALERT_EMAIL_FROM", "alerts@example.com");
            var withEnv = Assert.IsType<NotificationsReadinessDto>(
                Assert.IsType<OkObjectResult>(await fn.Readiness(Request(), default)).Value!);
            Assert.True(withEnv.TransportConfigured);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACS_EMAIL_CONNECTION_STRING", null);
            Environment.SetEnvironmentVariable("ALERT_EMAIL_FROM", null);
            await using var cleanup = _pg.NewDbContext();
            await cleanup.Database.ExecuteSqlRawAsync("DELETE FROM channels WHERE name = 'readiness-deliverable'");
        }
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

    [SkippableFact]
    public async Task Check_dto_includes_tags_for_display()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        await db.Database.ExecuteSqlRawAsync("""
            DO $$ DECLARE cid bigint; BEGIN
              INSERT INTO checks (name,kind,target_url) VALUES ('tag-display','http','https://d.example') RETURNING id INTO cid;
              INSERT INTO check_tags (check_id,key,value) VALUES (cid,'env','prod'),(cid,'team','web');
            END $$;
            """);
        var id = await db.Checks.Where(c => c.Name == "tag-display").Select(c => c.Id).FirstAsync();
        try
        {
            var fn = new ChecksFunctions(db);
            // GET /api/checks/{id} now carries the check's tags (was always [] before — the display gap).
            var detail = Assert.IsType<CheckDetailDto>(Assert.IsType<OkObjectResult>(await fn.GetCheck(Request(), id, default)).Value!);
            Assert.Equal(new[] { ("env", "prod"), ("team", "web") }, detail.Tags.Select(t => (t.Key, t.Value)).ToArray());
            // GET /api/checks (list) carries them too.
            var list = Assert.IsAssignableFrom<IEnumerable<CheckSummaryDto>>(
                Assert.IsType<OkObjectResult>(await fn.ListChecks(Request(), default)).Value!).ToList();
            var summary = Assert.Single(list, c => c.Id == id);
            Assert.Equal(new[] { ("env", "prod"), ("team", "web") }, summary.Tags.Select(t => (t.Key, t.Value)).ToArray());
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM checks WHERE name = 'tag-display';");
        }
    }

    [SkippableFact]
    public async Task Narrative_serves_stored_row_with_factpack_and_404s_when_missing()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        var fn = new ReportsFunctions(db);
        // jsonb via builders (no literal braces for ExecuteSqlRaw). "window" is quoted — reserved word.
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO report_narratives (scope_type, scope_key, "window", generated_at, headline, body, highlights, fact_pack, model)
            VALUES ('fleet','','7d', now() - interval '2 days', 'All green',
                    'Availability 99.1%, 1 real-outage incident; p95 +15% w/w.',
                    jsonb_build_array('99.1% availability','p95 +15% w/w'),
                    jsonb_build_object('current', jsonb_build_object('availabilityPct', 99.1, 'p95Ms', 320), 'incidents', 1),
                    'gpt-5-mini');
            """);
        try
        {
            var dto = Assert.IsType<NarrativeDto>(Assert.IsType<OkObjectResult>(
                await fn.GetNarrative(Request("?scope=fleet&window=7d"), default)).Value!);
            Assert.Equal("All green", dto.Headline);
            Assert.Contains("99.1%", dto.Body);
            Assert.Equal(new[] { "99.1% availability", "p95 +15% w/w" }, dto.Highlights);
            Assert.Equal("gpt-5-mini", dto.Model);
            Assert.False(dto.Stale); // 2 days old, 7d window → fresh
            // ★ factPack carries the cited numbers verbatim (auditability).
            Assert.Equal(320, dto.FactPack.GetProperty("current").GetProperty("p95Ms").GetInt32());
            // ★ fleet is keyed by an EMPTY scope_key (runner contract); the ?key param is ignored for fleet
            // — a stray key still resolves the one fleet narrative, and the echoed key is "".
            Assert.Equal("", dto.Key);
            var withStrayKey = Assert.IsType<NarrativeDto>(Assert.IsType<OkObjectResult>(
                await fn.GetNarrative(Request("?scope=fleet&key=fleet&window=7d"), default)).Value!);
            Assert.Equal("All green", withStrayKey.Headline);

            // stale: a narrative older than its window period.
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE report_narratives SET generated_at = now() - interval '10 days' WHERE scope_type='fleet' AND \"window\"='7d';");
            await using var db2 = _pg.NewDbContext();
            var staleDto = Assert.IsType<NarrativeDto>(Assert.IsType<OkObjectResult>(
                await new ReportsFunctions(db2).GetNarrative(Request("?scope=fleet&window=7d"), default)).Value!);
            Assert.True(staleDto.Stale);

            // Missing narrative → 404 (the dashboard hides the card cleanly).
            Assert.IsType<NotFoundObjectResult>(await fn.GetNarrative(Request("?scope=monitor&key=999999&window=7d"), default));
            // Validation: bad scope / window / monitor-without-key → 400.
            Assert.IsType<BadRequestObjectResult>(await fn.GetNarrative(Request("?scope=bogus&window=7d"), default));
            Assert.IsType<BadRequestObjectResult>(await fn.GetNarrative(Request("?scope=fleet&window=1y"), default));
            Assert.IsType<BadRequestObjectResult>(await fn.GetNarrative(Request("?scope=monitor&window=7d"), default));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM report_narratives WHERE scope_type='fleet' AND \"window\"='7d';");
        }
    }

    [SkippableFact]
    public async Task Reconcile_drift_serves_all_types_verbatim_and_empty_when_in_sync()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        var fn = new ReconcileFunctions(db);

        // Empty table → in sync: items empty, detectedAt null (the dashboard shows "in sync with Git").
        var empty = Assert.IsType<ReconcileDriftDto>(Assert.IsType<OkObjectResult>(
            await fn.GetReconcileDrift(Request(), default)).Value!);
        Assert.Empty(empty.Items);
        Assert.Null(empty.DetectedAt);

        // Seed one of each drift type. A 'changed' row carries the per-field before/after diff verbatim;
        // an 'orphan' is the known browser-exec gap (Git defines a monitor the runner can't run yet).
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO reconcile_drift (source_key, drift_type, detail, detected_at) VALUES
              ('checkout-flow','orphan',
               jsonb_build_object('flow_name','checkout','reason','no compiled runner flow module for this monitor'),
               now() - interval '5 minutes'),
              ('new-api','new',
               jsonb_build_object('name','New API','kind','http','target_url','https://new.example','flow_name','new-api'),
               now() - interval '5 minutes'),
              ('home','changed',
               jsonb_build_object('fields', jsonb_build_object('name', jsonb_build_object('git','Home','live','Homepage'))),
               now() - interval '5 minutes'),
              ('legacy','missing',
               jsonb_build_object('name','Legacy','action','would soft-disable (enabled=false); never hard-delete'),
               now() - interval '5 minutes');
            """);
        try
        {
            var dto = Assert.IsType<ReconcileDriftDto>(Assert.IsType<OkObjectResult>(
                await fn.GetReconcileDrift(Request(), default)).Value!);
            Assert.Equal(4, dto.Items.Count);
            Assert.NotNull(dto.DetectedAt);

            // Ordered drift_type then source_key → changed, missing, new, orphan.
            Assert.Equal(new[] { "changed", "missing", "new", "orphan" }, dto.Items.Select(i => i.DriftType).ToArray());

            // detail jsonb passes through verbatim: the 'changed' per-field before/after diff is intact.
            var changed = dto.Items.Single(i => i.DriftType == "changed");
            var nameDiff = changed.Detail.GetProperty("fields").GetProperty("name");
            Assert.Equal("Home", nameDiff.GetProperty("git").GetString());
            Assert.Equal("Homepage", nameDiff.GetProperty("live").GetString());

            // the orphan carries its bound flow_name (the runner can't run it yet).
            var orphan = dto.Items.Single(i => i.DriftType == "orphan");
            Assert.Equal("checkout-flow", orphan.SourceKey);
            Assert.Equal("checkout", orphan.Detail.GetProperty("flow_name").GetString());
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM reconcile_drift;");
        }
    }

    [SkippableFact]
    public async Task Spec_catalog_joins_checks_for_coverage_and_enriches_active_with_health()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        var fn = new SpecsFunctions(db);

        // Empty catalog → reconcile hasn't populated it yet: items empty, probedAt null.
        var empty = Assert.IsType<SpecCatalogDto>(Assert.IsType<OkObjectResult>(
            await fn.GetSpecCatalog(Request(), default)).Value!);
        Assert.Empty(empty.Items);
        Assert.Null(empty.ProbedAt);

        // Seed an ACTIVE spec: a check bound by source_key (+ a pass run for health) and its catalog row.
        // Plus an UNMONITORED+runnable spec and an UNMONITORED+orphan spec (no check).
        await db.Database.ExecuteSqlRawAsync("""
            DO $$
            DECLARE cid bigint;
            BEGIN
              INSERT INTO checks (name, kind, target_url, flow_name, source_key, spec_path, enabled)
              VALUES ('Active Mon','browser','https://a.example','active-flow','active-spec',
                      'monitors/a/active.spec.ts', true)
              RETURNING id INTO cid;
              INSERT INTO runs (check_id, status, started_at, duration_ms)
              VALUES (cid, 'pass', now() - interval '2 minutes', 1234);
            END $$;

            INSERT INTO spec_catalog
              (source_key, name, spec_path, kind, target, suggested_interval_seconds, tags,
               description, enabled_by_default, runnable, not_runnable_reason, probed_at) VALUES
              ('active-spec','Active Mon','monitors/a/active.spec.ts','browser','https://a.example',1800,
               '["a","journey"]'::jsonb,'an active monitor',false,true,NULL, now() - interval '5 minutes'),
              ('unmon-spec','Unmonitored Mon','monitors/u/unmon.spec.ts','browser','https://u.example',600,
               '[]'::jsonb,NULL,false,true,NULL, now() - interval '5 minutes'),
              ('orphan-spec','Orphan Mon','monitors/o/orphan.spec.ts','browser',NULL,NULL,
               '[]'::jsonb,NULL,false,false,'not fetchable: 404', now() - interval '5 minutes');
            """);
        try
        {
            var dto = Assert.IsType<SpecCatalogDto>(Assert.IsType<OkObjectResult>(
                await fn.GetSpecCatalog(Request(), default)).Value!);
            Assert.Equal(3, dto.Items.Count);
            Assert.NotNull(dto.ProbedAt);
            // Ordered by source_key → active, orphan, unmon.
            Assert.Equal(new[] { "active-spec", "orphan-spec", "unmon-spec" }, dto.Items.Select(i => i.SourceKey).ToArray());

            // ACTIVE: monitored=true, links to the check, health enriched from the pass run.
            var active = dto.Items.Single(i => i.SourceKey == "active-spec");
            Assert.True(active.Monitored);
            Assert.NotNull(active.CheckId);
            Assert.Equal("Active Mon", active.CheckName);
            Assert.True(active.Enabled);
            Assert.True(active.Runnable);
            Assert.NotNull(active.Health);
            Assert.Equal("pass", active.Health!.CurrentStatus);
            Assert.Equal(1234d, active.Health.P95Ms);   // single 24h run → p95 = that run
            Assert.Equal(0, active.Health.OpenIncidentCount);
            Assert.Equal(new[] { "a", "journey" }, active.Tags.ToArray()); // tags jsonb round-trips

            // UNMONITORED: no check, no health; still carries the manifest's suggested defaults.
            var unmon = dto.Items.Single(i => i.SourceKey == "unmon-spec");
            Assert.False(unmon.Monitored);
            Assert.Null(unmon.CheckId);
            Assert.Null(unmon.Health);
            Assert.True(unmon.Runnable);
            Assert.Equal(600, unmon.SuggestedIntervalSeconds);

            // ORPHAN: unmonitored + not runnable, with the probe reason passed through.
            var orphan = dto.Items.Single(i => i.SourceKey == "orphan-spec");
            Assert.False(orphan.Monitored);
            Assert.False(orphan.Runnable);
            Assert.Equal("not fetchable: 404", orphan.NotRunnableReason);
            Assert.Null(orphan.Health);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM spec_catalog; DELETE FROM checks WHERE source_key = 'active-spec';");
        }
    }

    [SkippableFact]
    public async Task Reports_availability_is_additive_and_performance_p95_is_recomputed_from_raw()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        // rep-api (http, team:platform): day-2 has 90 UP @100ms (daily p95=100); day-1 has 4 UP @1000ms
        // (daily p95=1000). Window p95 over the 94 combined raw runs is ~100 (the 90 lows dominate) —
        // NOT the average of daily p95s (550). rep-web (browser, team:web): 10 runs w/ web-vitals.
        await db.Database.ExecuteSqlRawAsync("""
            DO $$
            DECLARE api_id bigint; web_id bigint; rid bigint; i int;
            BEGIN
              INSERT INTO checks (name,kind,target_url) VALUES ('rep-api','http','https://a.example') RETURNING id INTO api_id;
              INSERT INTO checks (name,kind,target_url,flow_name) VALUES ('rep-web','browser','https://w.example','rep-flow') RETURNING id INTO web_id;
              INSERT INTO check_tags (check_id,key,value) VALUES (api_id,'team','platform'), (web_id,'team','web');
              INSERT INTO daily_check_rollup (check_id,day,up_count,down_count,total_count,availability_pct,latency_count,duration_avg_ms,duration_p95_ms)
                VALUES (api_id, CURRENT_DATE-2, 90,10,100, 90.0, 90, 100, 100),
                       (api_id, CURRENT_DATE-1, 4,0,4, 100.0, 4, 1000, 1000);
              FOR i IN 1..90 LOOP
                INSERT INTO runs (check_id,status,started_at,finished_at,duration_ms)
                  VALUES (api_id,'pass',(CURRENT_DATE-2)::timestamptz + interval '6 hours',(CURRENT_DATE-2)::timestamptz + interval '6 hours',100);
              END LOOP;
              FOR i IN 1..4 LOOP
                INSERT INTO runs (check_id,status,started_at,finished_at,duration_ms)
                  VALUES (api_id,'pass',(CURRENT_DATE-1)::timestamptz + interval '6 hours',(CURRENT_DATE-1)::timestamptz + interval '6 hours',1000);
              END LOOP;
              INSERT INTO daily_check_rollup (check_id,day,up_count,down_count,total_count,availability_pct,latency_count,duration_avg_ms,duration_p95_ms,vitals_count,lcp_p75_ms)
                VALUES (web_id, CURRENT_DATE-1, 10,0,10, 100.0, 10, 200, 250, 10, 1200);
              FOR i IN 1..10 LOOP
                INSERT INTO runs (check_id,status,started_at,finished_at,duration_ms)
                  VALUES (web_id,'pass',(CURRENT_DATE-1)::timestamptz + interval '6 hours',(CURRENT_DATE-1)::timestamptz + interval '6 hours',200) RETURNING id INTO rid;
                INSERT INTO run_metrics (run_id,lcp_ms,fcp_ms,ttfb_ms,cls) VALUES (rid,1200,800,150,0.05);
              END LOOP;
            END $$;
            """);
        try
        {
            var fn = new ReportsFunctions(db);

            // ── AVAILABILITY by team (additive from rollup counts, NOT averaged daily %) ──
            var avail = Assert.IsType<AvailabilityReportDto>(Assert.IsType<OkObjectResult>(
                await fn.GetAvailabilityReport(Request("?window=30d&groupBy=team"), default)).Value!);
            Assert.Equal("team", avail.GroupBy);
            var platform = avail.Groups.Single(g => g.Group == "platform");
            Assert.Equal(94, platform.UpCount);   // 90 + 4
            Assert.Equal(10, platform.DownCount);  // 10 + 0
            Assert.Equal(Math.Round(100m * 94 / 104, 4), platform.AvailabilityPct); // = sum(up)/sum(up+down)
            Assert.NotEqual(95m, platform.AvailabilityPct);   // NOT the avg of daily pcts (90, 100)
            Assert.Single(platform.Checks, c => c.CheckName == "rep-api");
            Assert.True(platform.Series.Count >= 2);          // daily trend present
            Assert.Contains(avail.Groups, g => g.Group == "web");

            // ── PERFORMANCE by team: p95 RECOMPUTED FROM RAW (~100), not avg-of-daily-p95s (550) ──
            var perf = Assert.IsType<PerformanceReportDto>(Assert.IsType<OkObjectResult>(
                await fn.GetPerformanceReport(Request("?window=30d&groupBy=team"), default)).Value!);
            var pPlatform = perf.Groups.Single(g => g.Group == "platform");
            Assert.NotNull(pPlatform.Latency.P95Ms);
            Assert.True(pPlatform.Latency.P95Ms < 550, $"p95 {pPlatform.Latency.P95Ms} must be raw-recomputed, not the 550 avg of daily p95s");
            Assert.True(pPlatform.Latency.P95Ms <= 200);

            // The report's p95 MATCHES a direct raw percentile query (proves recompute-from-raw).
            var apiId = await db.Checks.Where(c => c.Name == "rep-api").Select(c => c.Id).FirstAsync();
            var conn = (NpgsqlConnection)db.Database.GetDbConnection();
            await conn.OpenAsync();
            try
            {
                var directP95 = Convert.ToInt32(await Scalar(conn,
                    $"SELECT round(percentile_cont(0.95) WITHIN GROUP (ORDER BY duration_ms))::int FROM runs WHERE check_id={apiId} AND status IN ('pass','warn')"));
                Assert.Equal(directP95, pPlatform.Latency.P95Ms);
            }
            finally { await conn.CloseAsync(); }

            // Web-vitals scoped to browser: platform (http) → null; web (browser) → present.
            Assert.Null(pPlatform.WebVitals);
            var pWeb = perf.Groups.Single(g => g.Group == "web");
            Assert.NotNull(pWeb.WebVitals);
            Assert.Equal(1200, pWeb.WebVitals!.LcpP75Ms);
            Assert.NotEmpty(pPlatform.Series);   // daily latency trend present

            // Ungrouped → one fleet group (group=null).
            var ung = Assert.IsType<PerformanceReportDto>(Assert.IsType<OkObjectResult>(
                await fn.GetPerformanceReport(Request("?window=30d"), default)).Value!);
            Assert.Null(Assert.Single(ung.Groups).Group);

            // ★ REGRESSION (the "reports empty despite data" bug): the dashboard sends groupBy="none" for the
            // ungrouped report. "none" is NOT a tag key — it must behave like ungrouped (one fleet group with
            // the rollup data), NOT join check_tags ON key='none' (which matches nothing → empty groups).
            var availNone = Assert.IsType<AvailabilityReportDto>(Assert.IsType<OkObjectResult>(
                await fn.GetAvailabilityReport(Request("?window=30d&groupBy=none"), default)).Value!);
            var availFleet = Assert.Single(availNone.Groups);
            Assert.Null(availFleet.Group);                 // ungrouped → group=null, not the literal "none"
            Assert.Null(availNone.GroupBy);
            Assert.Equal(104, availFleet.UpCount);         // 94 (rep-api) + 10 (rep-web) — the rollup data is served
            Assert.Equal(2, availFleet.Checks.Count);      // both monitors present

            var perfNone = Assert.IsType<PerformanceReportDto>(Assert.IsType<OkObjectResult>(
                await fn.GetPerformanceReport(Request("?window=30d&groupBy=none"), default)).Value!);
            Assert.Null(Assert.Single(perfNone.Groups).Group);
            Assert.NotNull(perfNone.Groups[0].Latency.P95Ms);

            // Bad window → 400.
            Assert.IsType<BadRequestObjectResult>(await fn.GetAvailabilityReport(Request("?window=1y"), default));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM checks WHERE name IN ('rep-api','rep-web');"); // cascades rollup/runs/tags
        }
    }

    [SkippableFact]
    public async Task Check_runs_are_cursor_paginated_over_a_bounded_window()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();

        // A dedicated check whose runs straddle the default 7d window: 3 OLD (>7d) + 6 RECENT (<7d).
        // Two recent runs SHARE a started_at (the -2h pair) so the (started_at, id) tie-break is
        // exercised — the keyset must keep timestamp-collision rows distinct across a page boundary.
        await db.Database.ExecuteSqlRawAsync("""
            DO $$ DECLARE cid bigint; BEGIN
              INSERT INTO checks (name,kind,target_url) VALUES ('runs-pg','http','https://p.example') RETURNING id INTO cid;
              INSERT INTO runs (check_id,status,started_at,finished_at,duration_ms) VALUES
                (cid,'pass', now() - interval '10 days', now() - interval '10 days', 40),
                (cid,'fail', now() - interval '11 days', now() - interval '11 days', 40),
                (cid,'pass', now() - interval '12 days', now() - interval '12 days', 40),
                (cid,'pass', now() - interval '1 hour',  now() - interval '1 hour',  40),
                (cid,'warn', now() - interval '2 hours', now() - interval '2 hours', 40),
                (cid,'pass', now() - interval '2 hours', now() - interval '2 hours', 41),
                (cid,'fail', now() - interval '3 hours', now() - interval '3 hours', 40),
                (cid,'pass', now() - interval '4 hours', now() - interval '4 hours', 40),
                (cid,'pass', now() - interval '5 hours', now() - interval '5 hours', 40);
            END $$;
            """);
        var cid = await db.Checks.Where(c => c.Name == "runs-pg").Select(c => c.Id).FirstAsync();
        try
        {
            var fn = new ChecksFunctions(db);
            static CursorPage<RunDto> Page(IActionResult r) =>
                Assert.IsType<CursorPage<RunDto>>(Assert.IsType<OkObjectResult>(r).Value!);

            // Runner-truth ordering inside the default 7d window: DESC started_at, then DESC id.
            var expectedRecent = await db.Runs.AsNoTracking()
                .Where(r => r.CheckId == cid && r.StartedAt >= DateTimeOffset.UtcNow.AddDays(-7))
                .OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id)
                .Select(r => r.Id).ToListAsync();
            Assert.Equal(6, expectedRecent.Count); // 6 recent kept, 3 old excluded

            // ── BOUNDED DEFAULT: a param-less call returns ONLY the recent 6 — never an all-time
            //    scan of the 3 old runs. This is the whole point of the date-range default.
            var def = Page(await fn.ListCheckRuns(Request(), cid, default));
            Assert.Equal(expectedRecent, def.Items.Select(i => i.Id).ToList());

            // ── CURSOR WALK: page size 2 walks the window, each row exactly once, no skips/dupes,
            //    next-cursor null when exhausted.
            var walked = new List<long>();
            string? cursor = null;
            var guard = 0;
            do
            {
                var q = "?pageSize=2" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
                var pg = Page(await fn.ListCheckRuns(Request(q), cid, default));
                Assert.True(pg.Items.Count <= 2);
                walked.AddRange(pg.Items.Select(i => i.Id));
                cursor = pg.NextCursor;
            } while (cursor is not null && ++guard < 20);
            Assert.Null(cursor);                  // exhausted -> null next-cursor
            Assert.Equal(expectedRecent, walked); // identical set, identical DESC order, no dupes

            // ── DATE-RANGE FILTER: an explicit from/to reaches the OLD runs and filters to ONLY them.
            var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-13).ToString("o"));
            var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-8).ToString("o"));
            var old = Page(await fn.ListCheckRuns(Request($"?from={from}&to={to}&pageSize=200"), cid, default));
            Assert.Equal(3, old.Items.Count);
            Assert.All(old.Items, i => Assert.True(i.StartedAt < DateTimeOffset.UtcNow.AddDays(-8)));
            Assert.Null(old.NextCursor);

            // ── MALFORMED INPUT is 400 — not a 500, and not a silent fall-through to an all-time scan.
            Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(
                await fn.ListCheckRuns(Request("?cursor=not~a~cursor"), cid, default)).StatusCode);
            var fromAfterTo = $"?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("o"))}" +
                              $"&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("o"))}";
            Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(
                await fn.ListCheckRuns(Request(fromAfterTo), cid, default)).StatusCode);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM checks WHERE name = 'runs-pg';");
        }
    }

    [SkippableFact]
    public async Task Incidents_are_cursor_paginated_with_open_exempt_from_the_window()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();

        // One check whose incidents straddle the default 30d window: 3 OLD resolved (>30d), 6 RECENT
        // resolved (<30d, two sharing an opened_at to exercise the (opened_at, id) tie-break), and ONE
        // OPEN incident opened 45d ago. The partial unique index one_open_incident_per_check allows only
        // one open per check — so the single old-open incident is exactly the open-exemption case.
        await db.Database.ExecuteSqlRawAsync("""
            DO $$ DECLARE cid bigint; BEGIN
              INSERT INTO checks (name,kind,target_url) VALUES ('inc-pg','http','https://i.example') RETURNING id INTO cid;
              INSERT INTO incidents (check_id,status,severity,opened_at,resolved_at,consecutive_failures) VALUES
                (cid,'resolved','critical', now()-interval '40 days', now()-interval '40 days'+interval '1 hour', 3),
                (cid,'resolved','warning',  now()-interval '50 days', now()-interval '50 days'+interval '1 hour', 3),
                (cid,'resolved','critical', now()-interval '60 days', now()-interval '60 days'+interval '1 hour', 3),
                (cid,'resolved','critical', now()-interval '1 day',  now()-interval '1 day'+interval  '1 hour', 3),
                (cid,'resolved','warning',  now()-interval '2 days', now()-interval '2 days'+interval '1 hour', 3),
                (cid,'resolved','critical', now()-interval '2 days', now()-interval '2 days'+interval '1 hour', 4),
                (cid,'resolved','warning',  now()-interval '3 days', now()-interval '3 days'+interval '1 hour', 3),
                (cid,'resolved','critical', now()-interval '4 days', now()-interval '4 days'+interval '1 hour', 3),
                (cid,'resolved','warning',  now()-interval '5 days', now()-interval '5 days'+interval '1 hour', 3),
                (cid,'open','critical', now()-interval '45 days', NULL, 7);
            END $$;
            """);
        var cid = await db.Checks.Where(c => c.Name == "inc-pg").Select(c => c.Id).FirstAsync();
        try
        {
            var fn = new IncidentsFunctions(db);
            static CursorPage<IncidentDto> Page(IActionResult r) =>
                Assert.IsType<CursorPage<IncidentDto>>(Assert.IsType<OkObjectResult>(r).Value!);
            // Scope every request to this check (?checkId=) so counts are independent of the seed/other data.
            string Q(string extra) => $"?checkId={cid}{extra}";

            var expectedResolved = await db.Incidents.AsNoTracking()
                .Where(i => i.CheckId == cid && i.Status == "resolved" && i.OpenedAt >= DateTimeOffset.UtcNow.AddDays(-30))
                .OrderByDescending(i => i.OpenedAt).ThenByDescending(i => i.Id)
                .Select(i => i.Id).ToListAsync();
            Assert.Equal(6, expectedResolved.Count); // 6 recent resolved kept, 3 old excluded

            // ── BOUNDED DEFAULT (no status): only the last-30d incidents. The 3 old resolved AND the
            //    45d-old OPEN incident are all excluded — a param-less call never loads all-time.
            var def = Page(await fn.ListIncidents(Request(Q("")), default));
            Assert.Equal(6, def.Items.Count);
            Assert.DoesNotContain(def.Items, i => i.Status == "open"); // the only open one is 45d old → windowed out

            // ── status=open is EXEMPT from the window: the 45d-old open incident still surfaces (a
            //    long-running open incident must never be hidden by the recent default window).
            var open = Page(await fn.ListIncidents(Request(Q("&status=open")), default));
            var openItem = Assert.Single(open.Items);
            Assert.Equal("open", openItem.Status);
            Assert.True(openItem.OpenedAt < DateTimeOffset.UtcNow.AddDays(-30));

            // ── CURSOR WALK over resolved (pageSize 2): each row once, DESC (opened_at, id), null at end.
            var walked = new List<long>();
            string? cursor = null;
            var guard = 0;
            do
            {
                var q = Q("&status=resolved&pageSize=2") + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
                var pg = Page(await fn.ListIncidents(Request(q), default));
                Assert.True(pg.Items.Count <= 2);
                walked.AddRange(pg.Items.Select(i => i.Id));
                cursor = pg.NextCursor;
            } while (cursor is not null && ++guard < 20);
            Assert.Null(cursor);
            Assert.Equal(expectedResolved, walked);

            // ── DATE-RANGE reaches the OLD resolved incidents and filters to ONLY them.
            var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-65).ToString("o"));
            var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-31).ToString("o"));
            var old = Page(await fn.ListIncidents(Request(Q($"&status=resolved&from={from}&to={to}&pageSize=200")), default));
            Assert.Equal(3, old.Items.Count);
            Assert.All(old.Items, i => Assert.True(i.OpenedAt < DateTimeOffset.UtcNow.AddDays(-31)));
            Assert.Null(old.NextCursor);

            // ── MALFORMED input is 400 — bad cursor and bad status alike.
            Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(
                await fn.ListIncidents(Request(Q("&cursor=not~a~cursor")), default)).StatusCode);
            Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(
                await fn.ListIncidents(Request(Q("&status=banana")), default)).StatusCode);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM checks WHERE name = 'inc-pg';"); // cascades incidents
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
        var incidents = Assert.IsType<CursorPage<IncidentDto>>(ok.Value!).Items;
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
            var incidents = Assert.IsType<CursorPage<IncidentDto>>(ok.Value!).Items;

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
    public async Task Activation_creates_browser_check_with_spec_binding_and_409s_on_duplicate()
    {
        // Phase 13 activation (steps 4-6, API half): POST /api/checks carrying spec_path + source_key +
        // a SYNTHETIC flow_name (flowNameFor(spec_path)) creates the live monitor; a duplicate activation
        // (same source_key) returns a CLEAN 409, not a constraint 500.
        RequireDocker();
        await using var db = _pg.NewDbContext();
        var fn = new ChecksFunctions(db);
        var body = new CreateCheckRequest
        {
            Name = "Wegmans — search product",
            Kind = "browser",
            TargetUrl = "https://www.wegmans.com",
            FlowName = "search-product", // synthetic; satisfies browser_needs_flow
            SourceKey = "wegmans-search-product",
            SpecPath = "monitors/wegmans/search-product.spec.ts",
            IntervalSeconds = 600,
        };
        try
        {
            var res = Assert.IsType<ObjectResult>(await fn.CreateCheck(JsonRequest(body), default));
            Assert.Equal(201, res.StatusCode);
            var dto = Assert.IsType<CheckDetailDto>(res.Value!);
            // The create response echoes the binding the runner will execute.
            Assert.Equal("monitors/wegmans/search-product.spec.ts", dto.SpecPath);
            Assert.Equal("wegmans-search-product", dto.SourceKey);

            // The PERSISTED row carries spec_path (→ executeBrowser takes the Git-fetch path, Option C)
            // AND the synthetic flow_name (→ browser_needs_flow satisfied, the INSERT succeeded).
            await using var db2 = _pg.NewDbContext();
            var conn = (NpgsqlConnection)db2.Database.GetDbConnection();
            await conn.OpenAsync();
            try
            {
                Assert.Equal("monitors/wegmans/search-product.spec.ts",
                    (string?)await Scalar(conn, $"SELECT spec_path FROM checks WHERE id = {dto.Id}"));
                Assert.Equal("search-product",
                    (string?)await Scalar(conn, $"SELECT flow_name FROM checks WHERE id = {dto.Id}"));
            }
            finally { await conn.CloseAsync(); }

            // ── DUPLICATE activation (same source_key) → 409 (the partial unique index), NOT a 500.
            var dupRes = await fn.CreateCheck(JsonRequest(body), default);
            Assert.Equal(409, Assert.IsType<ConflictObjectResult>(dupRes).StatusCode);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM checks WHERE source_key = 'wegmans-search-product';");
        }

        // ── spec_path SHAPE validation rejects traversal with a 400 — the path never reaches the DB.
        await using var db3 = _pg.NewDbContext();
        var fn3 = new ChecksFunctions(db3);
        var bad = new CreateCheckRequest
        {
            Name = "bad", Kind = "browser", TargetUrl = "https://x.example", FlowName = "x",
            SourceKey = "bad-traversal-key", SpecPath = "monitors/../etc/passwd.spec.ts",
        };
        Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(await fn3.CreateCheck(JsonRequest(bad), default)).StatusCode);
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

    [SkippableFact]
    public async Task Routing_put_rejects_unrecognized_payload_without_wiping_routes()
    {
        // Defense-in-depth: a wrong-key payload (the dashboard contract-drift {defaults,overrides}) or an
        // empty {} must 400 and leave existing routes UNTOUCHED — never silently delete-all + return 200.
        RequireDocker();
        await using var db = _pg.NewDbContext();
        var rt = new RoutingFunctions(db);
        async Task<int> RouteCount()
        {
            await using var c = _pg.NewDbContext();
            return await c.AlertRoutes.CountAsync();
        }
        try
        {
            // Establish a known state: critical -> channel 1.
            Assert.IsType<OkObjectResult>(await rt.SetRouting(
                JsonRequest(new RoutingDto { Severity = new() { ["critical"] = new ChannelIdsDto(new long[] { 1 }) } }), default));
            Assert.Equal(1, await RouteCount());

            // Wrong keys ({defaults,overrides}, the stale dashboard) -> 400, routes UNTOUCHED (not wiped).
            Assert.IsType<BadRequestObjectResult>(await rt.SetRouting(
                JsonRequest(new { defaults = new { critical = new { channelIds = new[] { 1 } } }, overrides = new { } }), default));
            Assert.Equal(1, await RouteCount());

            // Empty object {} -> 400, still untouched.
            Assert.IsType<BadRequestObjectResult>(await rt.SetRouting(JsonRequest(new { }), default));
            Assert.Equal(1, await RouteCount());

            // Explicit well-formed clear {severity:{},perCheck:{}} -> 200, routes cleared.
            Assert.IsType<OkObjectResult>(await rt.SetRouting(
                JsonRequest(new RoutingDto { Severity = new(), PerCheck = new() }), default));
            Assert.Equal(0, await RouteCount());
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("""
                DELETE FROM alert_routes;
                INSERT INTO alert_routes (severity, channel_id)
                  SELECT s, c.id FROM (VALUES ('critical'), ('warning')) v(s) CROSS JOIN channels c WHERE c.name IN ('email','webhook');
                """);
        }
    }

    [SkippableFact]
    public async Task Tag_crud_normalization_distinct_and_suggested()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        var fn = new TagsFunctions(db);
        await db.Database.ExecuteSqlRawAsync("INSERT INTO checks (name,kind,target_url) VALUES ('tag-test','http','https://t.example');");
        var id = await db.Checks.Where(c => c.Name == "tag-test").Select(c => c.Id).FirstAsync();
        try
        {
            // PUT exercises normalization (lowercase/trim/ws→_), last-wins dedupe, empty-value drop, bare value:
            //   "Env":"Prod"         -> env:prod, then "env":"staging" -> last wins -> env:staging
            //   "service":"Prod Web" -> service:prod_web (internal whitespace -> _)
            //   "team":"  "          -> value empty after normalize -> DROPPED
            //   "":"adhoc"           -> bare value (empty key)
            var put = Assert.IsType<CheckTagsResponse>(Assert.IsType<OkObjectResult>(await fn.SetCheckTags(
                JsonRequest(new SetTagsRequest { Tags = new() {
                    new TagDto("Env", "Prod"), new TagDto("service", "Prod Web"),
                    new TagDto("env", "staging"), new TagDto("team", "  "), new TagDto("", "adhoc") } }), id, default)).Value!);
            Assert.Equal(new[] { ("", "adhoc"), ("env", "staging"), ("service", "prod_web") },
                put.Tags.Select(t => (t.Key, t.Value)).ToArray());

            // GET returns the same set.
            var got = Assert.IsType<CheckTagsResponse>(Assert.IsType<OkObjectResult>(await fn.GetCheckTags(Request(), id, default)).Value!);
            Assert.Equal(3, got.Tags.Count);

            // PUT a new EXACT set -> add (criticality) + remove (the bare value, old service value).
            var put2 = Assert.IsType<CheckTagsResponse>(Assert.IsType<OkObjectResult>(await fn.SetCheckTags(
                JsonRequest(new SetTagsRequest { Tags = new() {
                    new TagDto("env", "staging"), new TagDto("service", "synthwatch"), new TagDto("criticality", "tier-1") } }), id, default)).Value!);
            Assert.Equal(new[] { ("criticality", "tier-1"), ("env", "staging"), ("service", "synthwatch") },
                put2.Tags.Select(t => (t.Key, t.Value)).ToArray());
            Assert.DoesNotContain(put2.Tags, t => t.Value == "prod_web" || t.Value == "adhoc");

            // GET /api/tags — distinct in-use tags with check counts (includes this check's).
            var inUse = Assert.IsType<TagsInUseResponse>(Assert.IsType<OkObjectResult>(await fn.GetTagsInUse(Request(), default)).Value!);
            Assert.True(inUse.Tags.Single(t => t.Key == "criticality" && t.Value == "tier-1").Count >= 1);

            // GET /api/tags/suggested.
            var sug = Assert.IsType<string[]>(Assert.IsType<OkObjectResult>(TagsFunctions.GetSuggestedTagKeys(Request())).Value!);
            Assert.Equal(new[] { "env", "service", "team", "criticality" }, sug);

            // Guard: a missing `tags` key -> 400, tags UNTOUCHED (no silent wipe).
            Assert.IsType<BadRequestObjectResult>(await fn.SetCheckTags(JsonRequest(new { notTags = 1 }), id, default));
            await using (var db2 = _pg.NewDbContext())
                Assert.Equal(3, await db2.CheckTags.CountAsync(t => t.CheckId == id));

            // Explicit clear { tags: [] } -> 200, empty.
            var cleared = Assert.IsType<CheckTagsResponse>(Assert.IsType<OkObjectResult>(await fn.SetCheckTags(
                JsonRequest(new SetTagsRequest { Tags = new() }), id, default)).Value!);
            Assert.Empty(cleared.Tags);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM checks WHERE name = 'tag-test';"); // cascades tags
        }
    }

    [SkippableFact]
    public async Task TagRouting_get_set_normalize_partial_update_and_guard()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        var rt = new RoutingFunctions(db);
        async Task<int> TagRouteCount() { await using var c = _pg.NewDbContext(); return await c.TagRoutes.CountAsync(); }
        async Task<int> AlertRouteCount() { await using var c = _pg.NewDbContext(); return await c.AlertRoutes.CountAsync(); }
        try
        {
            // GET returns all three dimensions (seeded severity; tagRules empty initially).
            var got = Assert.IsType<RoutingDto>(Assert.IsType<OkObjectResult>(await rt.GetRouting(Request(), default)).Value!);
            Assert.NotNull(got.Severity);
            var severityRowsBefore = await AlertRouteCount();

            // PUT tagRules ONLY (severity/perCheck omitted) -> sets tag-rules + LEAVES severity untouched.
            // Normalization: "Env"/"Prod"->env/prod, "service"/"Prod Web"->service/prod_web; dup deduped.
            var put = Assert.IsType<RoutingDto>(Assert.IsType<OkObjectResult>(await rt.SetRouting(JsonRequest(new RoutingDto {
                TagRules = new() {
                    new TagRuleDto("Env", "Prod", 1), new TagRuleDto("service", "Prod Web", 2),
                    new TagRuleDto("env", "prod", 1) } }), default)).Value!);
            Assert.Equal(new[] { ("env", "prod", 1L), ("service", "prod_web", 2L) },
                put.TagRules!.Select(t => (t.TagKey, t.TagValue, t.ChannelId)).ToArray());
            Assert.Equal(2, await TagRouteCount());
            Assert.Equal(severityRowsBefore, await AlertRouteCount()); // severity UNTOUCHED by a tagRules-only PUT
            Assert.NotNull(put.Severity);

            // Replace the tag-rule set (add criticality, drop service).
            var put2 = Assert.IsType<RoutingDto>(Assert.IsType<OkObjectResult>(await rt.SetRouting(JsonRequest(new RoutingDto {
                TagRules = new() { new TagRuleDto("env", "prod", 1), new TagRuleDto("criticality", "tier-1", 2) } }), default)).Value!);
            Assert.Equal(new[] { ("criticality", "tier-1", 2L), ("env", "prod", 1L) },
                put2.TagRules!.Select(t => (t.TagKey, t.TagValue, t.ChannelId)).ToArray());

            // channelId validation: unknown channel -> 400, tag_routes UNTOUCHED.
            Assert.IsType<BadRequestObjectResult>(await rt.SetRouting(JsonRequest(new RoutingDto {
                TagRules = new() { new TagRuleDto("env", "prod", 999999) } }), default));
            Assert.Equal(2, await TagRouteCount());
            // empty tagValue -> 400.
            Assert.IsType<BadRequestObjectResult>(await rt.SetRouting(JsonRequest(new RoutingDto {
                TagRules = new() { new TagRuleDto("env", "  ", 1) } }), default));

            // ★ #66 guard, extended: a wrong-key payload ({defaults,overrides}) -> 400, tag_routes UNTOUCHED (not wiped).
            Assert.IsType<BadRequestObjectResult>(await rt.SetRouting(JsonRequest(new { defaults = new { }, overrides = new { } }), default));
            Assert.Equal(2, await TagRouteCount());

            // ★ MISSING tagRules key (only severity sent) -> tag-rules LEFT untouched (partial update).
            Assert.IsType<OkObjectResult>(await rt.SetRouting(JsonRequest(new RoutingDto {
                Severity = new() { ["critical"] = new ChannelIdsDto(new long[] { 1 }) } }), default));
            Assert.Equal(2, await TagRouteCount());

            // ★ EXPLICIT { tagRules: [] } -> clears tag-rules.
            var cleared = Assert.IsType<RoutingDto>(Assert.IsType<OkObjectResult>(await rt.SetRouting(JsonRequest(new RoutingDto {
                TagRules = new() }), default)).Value!);
            Assert.Null(cleared.TagRules);
            Assert.Equal(0, await TagRouteCount());

            // Delete-guard now covers tag-routed channels: route channel 2 via a tag-rule, then delete -> 409.
            await rt.SetRouting(JsonRequest(new RoutingDto { TagRules = new() { new TagRuleDto("env", "prod", 2) } }), default);
            Assert.IsType<ConflictObjectResult>(await new ChannelsFunctions(db).DeleteChannel(Request(), 2, default));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("""
                DELETE FROM tag_routes;
                DELETE FROM alert_routes;
                INSERT INTO alert_routes (severity, channel_id)
                  SELECT s, c.id FROM (VALUES ('critical'), ('warning')) v(s) CROSS JOIN channels c WHERE c.name IN ('email','webhook');
                """);
        }
    }

    [SkippableFact]
    public async Task Channel_test_enqueues_pending_request_starts_runner_and_serves_status()
    {
        // Option A: POST enqueues a 'pending' test_send_requests row + kicks the (mocked) runner job and
        // returns 202 { requestId }; the RUNNER (not the API) does the real send. GET status reads the
        // runner-owned row. The API never sends and creates no incident.
        RequireDocker();
        await using var db = _pg.NewDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO channels (name,type,config) VALUES ('test-ch','email', jsonb_build_object('to', jsonb_build_array('a@x.com')));");
        var id = await db.Channels.Where(c => c.Name == "test-ch").Select(c => c.Id).FirstAsync();
        var incidentsBefore = await db.Incidents.CountAsync();
        var runner = new FakeRunnerJobTrigger();
        var fn = new ChannelTestFunctions(db, runner);
        try
        {
            // POST -> 202 { requestId }, a 'pending' row inserted, the runner job started exactly once.
            var accepted = Assert.IsType<ObjectResult>(await fn.TestChannel(Request(), id, default));
            Assert.Equal(202, accepted.StatusCode);
            var requestId = Assert.IsType<ChannelTestAcceptedDto>(accepted.Value!).RequestId;
            Assert.True(requestId > 0);
            Assert.Equal(1, runner.StartCount);

            await using (var db2 = _pg.NewDbContext()) // fresh read of committed state
            {
                var row = await db2.TestSendRequests.AsNoTracking().SingleAsync(r => r.Id == requestId);
                Assert.Equal(id, row.ChannelId);
                Assert.Equal("pending", row.Status);
                Assert.Null(row.CompletedAt);
            }
            // No incident created (pure channel test).
            Assert.Equal(incidentsBefore, await db.Incidents.CountAsync());

            // GET status -> 200 with the runner-owned lifecycle fields (still 'pending' here).
            var statusOk = Assert.IsType<OkObjectResult>(
                await fn.TestChannelStatus(Request($"?requestId={requestId}"), id, default));
            var status = Assert.IsType<ChannelTestStatusDto>(statusOk.Value!);
            Assert.Equal("pending", status.Status);
            Assert.Null(status.Detail);
            Assert.Null(status.CompletedAt);

            // Simulate the runner completing the lifecycle -> GET reflects delivered + detail + completedAt.
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE test_send_requests SET status='delivered', detail='sent', completed_at=now() WHERE id = {0}", requestId);
            var doneOk = Assert.IsType<OkObjectResult>(
                await fn.TestChannelStatus(Request($"?requestId={requestId}"), id, default));
            var done = Assert.IsType<ChannelTestStatusDto>(doneOk.Value!);
            Assert.Equal("delivered", done.Status);
            Assert.Equal("sent", done.Detail);
            Assert.NotNull(done.CompletedAt);

            // POST to an unknown channel -> 404 (and no row enqueued, runner not kicked again).
            Assert.IsType<NotFoundObjectResult>(await fn.TestChannel(Request(), 999999, default));
            Assert.Equal(1, runner.StartCount);

            // GET status for an unknown requestId -> 404; for a requestId belonging to ANOTHER channel -> 404.
            Assert.IsType<NotFoundObjectResult>(await fn.TestChannelStatus(Request("?requestId=999999"), id, default));
            Assert.IsType<NotFoundObjectResult>(await fn.TestChannelStatus(Request($"?requestId={requestId}"), id + 1, default));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM channels WHERE name = 'test-ch';"); // cascades test_send_requests
        }
    }

    private sealed class FakeRunnerJobTrigger : IRunnerJobTrigger
    {
        public int StartCount;
        public Task<bool> StartAsync(CancellationToken ct)
        {
            StartCount++;
            return Task.FromResult(true);
        }
    }

    // ─── Phase 12 slice 2 — the gate (principal resolution, audit write, fail-closed) ───────────────

    [SkippableFact]
    public async Task AuthPrincipal_resolves_roles_and_rejects_invalid_sessions()
    {
        RequireDocker();
        Environment.SetEnvironmentVariable("ADMIN_EMAILS", "boss@gate.test");
        await using var db = _pg.NewDbContext();
        var svc = new AuthPrincipalService(db);
        const string edTok = "swt_ed_tok", adTok = "swt_admin_tok", expTok = "swt_expired_tok", revTok = "swt_revoked_tok";
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO editors (email, added_by) VALUES ('ed@gate.test', 'boss@gate.test')");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO sessions (token_hash, email, expires_at) VALUES ({AuthTokens.Sha256Hex(edTok)}, 'ed@gate.test', now() + interval '1 hour')");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO sessions (token_hash, email, expires_at) VALUES ({AuthTokens.Sha256Hex(adTok)}, 'boss@gate.test', now() + interval '1 hour')");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO sessions (token_hash, email, expires_at) VALUES ({AuthTokens.Sha256Hex(expTok)}, 'ed@gate.test', now() - interval '1 minute')");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO sessions (token_hash, email, expires_at, revoked_at) VALUES ({AuthTokens.Sha256Hex(revTok)}, 'ed@gate.test', now() + interval '1 hour', now())");

            // editor token → editor (can write, not admin); admin token → admin.
            var ed = await svc.FromBearerAsync($"Bearer {edTok}", default);
            Assert.NotNull(ed);
            Assert.Equal("ed@gate.test", ed!.Email);
            Assert.Equal(Roles.Editor, ed.Role);
            Assert.True(ed.CanWrite);
            Assert.False(ed.IsAdmin);
            Assert.Equal(Roles.Admin, (await svc.FromBearerAsync($"Bearer {adTok}", default))!.Role);

            // no/invalid/expired/revoked token → null (→ the gate denies 401).
            Assert.Null(await svc.FromBearerAsync(null, default));
            Assert.Null(await svc.FromBearerAsync("Bearer swt_nonexistent", default));
            Assert.Null(await svc.FromBearerAsync($"Bearer {expTok}", default));
            Assert.Null(await svc.FromBearerAsync($"Bearer {revTok}", default));

            // ★ Live role: a valid session whose email is no longer an editor resolves to anonymous (→ 403),
            // so removing an editor revokes their write access on the very next request.
            await db.Database.ExecuteSqlRawAsync("DELETE FROM editors WHERE email = 'ed@gate.test'");
            var demoted = await svc.FromBearerAsync($"Bearer {edTok}", default);
            Assert.NotNull(demoted);
            Assert.Equal(Roles.Anonymous, demoted!.Role);
            Assert.False(demoted.CanWrite);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ADMIN_EMAILS", null);
            await using var c = _pg.NewDbContext();
            await c.Database.ExecuteSqlRawAsync(
                "DELETE FROM sessions WHERE email LIKE '%@gate.test'; DELETE FROM editors WHERE email LIKE '%@gate.test';");
        }
    }

    [SkippableFact]
    public async Task Audit_row_is_written_with_a_redacted_diff()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        const string secret = "SUPERSECRETwebhooktoken";
        var diff = new AuditDiff("channel", "9", Before: null,
            After: new ChannelDto(9, "ops-wh", "webhook",
                new ChannelConfig { Url = $"https://h.test/B/{secret}", AuthHeader = $"Bearer {secret}", To = new() { "a@b.test" } },
                true),
            Note: null);
        var row = AuditWriter.BuildRow(new Principal("admin@audit.test", Roles.Admin), "9.9.9.9",
            "POST", "/api/channels", statusCode: 201, success: true, diff);
        db.AuditLogs.Add(row);
        await db.SaveChangesAsync();
        try
        {
            await using var read = _pg.NewDbContext();
            var saved = await read.AuditLogs.AsNoTracking()
                .Where(a => a.ActorEmail == "admin@audit.test").OrderByDescending(a => a.Id).FirstAsync();
            Assert.Equal("create", saved.Action);
            Assert.Equal("channel", saved.TargetType);
            Assert.Equal("9", saved.TargetId);
            Assert.True(saved.Success);
            Assert.NotNull(saved.AfterJson);
            // ★ The persisted jsonb carries NO plaintext secret — only fingerprints.
            Assert.DoesNotContain(secret, saved.AfterJson!, StringComparison.Ordinal);
            Assert.Contains("redacted:sha256:", saved.AfterJson!, StringComparison.Ordinal);
        }
        finally
        {
            await using var c = _pg.NewDbContext();
            await c.Database.ExecuteSqlRawAsync("DELETE FROM audit_log WHERE actor_email = 'admin@audit.test'");
        }
    }

    [SkippableFact]
    public async Task AuthPrincipal_lookup_error_throws_so_the_gate_fails_closed()
    {
        RequireDocker();
        // A session lookup that ERRORS must throw — the middleware does NOT catch it, so it bubbles to the
        // outer exception-shield → 500 = DENIED (an auth error is never an open door).
        await using var broken = _pg.NewBrokenDbContext();
        var svc = new AuthPrincipalService(broken);
        await Assert.ThrowsAnyAsync<Exception>(() => svc.FromBearerAsync("Bearer swt_anything", default));
    }

    // ─── Phase 12 slice 1 — auth identity (OTP + sessions + access request) ────────────────────────

    private sealed class FakeEmailSender : IEmailSender
    {
        public List<(string To, string Subject, string Text, string Html)> Sent { get; } = new();
        public Task SendAsync(string recipient, string subject, string plainText, string html, CancellationToken ct)
        {
            Sent.Add((recipient, subject, plainText, html));
            return Task.CompletedTask;
        }
    }

    private static HttpRequest AuthedRequest(string token)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = $"Bearer {token}";
        return ctx.Request;
    }

    private static string ExtractCode(string body) => Regex.Match(body, @"\b(\d{6})\b").Groups[1].Value;

    [SkippableFact]
    public async Task Auth_otp_flow_issues_verifies_mints_session_me_and_logout()
    {
        RequireDocker();
        Environment.SetEnvironmentVariable("ADMIN_EMAILS", "boss@synth.test");
        await using var db = _pg.NewDbContext();
        var email = new FakeEmailSender();
        var fn = new AuthFunctions(db, email, NullLogger<AuthFunctions>.Instance);
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO editors (email, added_by) VALUES ('editor@synth.test', 'boss@synth.test')");

            // request-code for a KNOWN editor → 202 + the code emailed (case-insensitively normalized).
            var rc = await fn.RequestCode(JsonRequest(new { email = "Editor@Synth.test" }), default);
            Assert.Equal(202, Assert.IsType<ObjectResult>(rc).StatusCode);
            var sent = Assert.Single(email.Sent);
            Assert.Equal("editor@synth.test", sent.To);
            var code = ExtractCode(sent.Text);
            Assert.Matches("^[0-9]{6}$", code);
            // The wired send carries the branded multipart HTML with the code (not just plaintext).
            Assert.Contains("SYNTHWATCH", sent.Html, StringComparison.Ordinal);
            Assert.Contains(code, sent.Html, StringComparison.Ordinal);

            // The stored code is HASHED, never plaintext.
            await using (var dbr = _pg.NewDbContext())
            {
                var row = await dbr.OtpCodes.AsNoTracking().Where(o => o.Email == "editor@synth.test")
                    .OrderByDescending(o => o.CreatedAt).FirstAsync();
                Assert.NotEqual(code, row.CodeHash);
                Assert.Equal(AuthTokens.Sha256Hex(code), row.CodeHash);
            }

            // A WRONG code → 400 and bumps attempt_count (brute-force cap).
            var wrong = code == "000000" ? "111111" : "000000";
            Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(
                await fn.Verify(JsonRequest(new { email = "editor@synth.test", code = wrong }), default)).StatusCode);
            await using (var dbr = _pg.NewDbContext())
                Assert.Equal(1, await dbr.OtpCodes.AsNoTracking().Where(o => o.Email == "editor@synth.test")
                    .OrderByDescending(o => o.CreatedAt).Select(o => o.AttemptCount).FirstAsync());

            // The CORRECT code → 200 + a session token; role resolved live = editor.
            var ok = Assert.IsType<OkObjectResult>(
                await fn.Verify(JsonRequest(new { email = "editor@synth.test", code }), default));
            var verified = Assert.IsType<VerifyResponseDto>(ok.Value!);
            Assert.Equal("editor", verified.Role);
            Assert.StartsWith("swt_", verified.Token);
            // The session is stored HASHED.
            await using (var dbr = _pg.NewDbContext())
                Assert.True(await dbr.Sessions.AsNoTracking()
                    .AnyAsync(s => s.TokenHash == AuthTokens.Sha256Hex(verified.Token) && s.Email == "editor@synth.test"));

            // The same code can't be reused (one-time/consumed).
            Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(
                await fn.Verify(JsonRequest(new { email = "editor@synth.test", code }), default)).StatusCode);

            // me resolves the token → { email, role }; no token → 401.
            var me = Assert.IsType<MeDto>(Assert.IsType<OkObjectResult>(await fn.Me(AuthedRequest(verified.Token), default)).Value!);
            Assert.Equal("editor@synth.test", me.Email);
            Assert.Equal("editor", me.Role);
            Assert.IsType<UnauthorizedObjectResult>(await fn.Me(Request(), default));

            // logout revokes the session → me now 401.
            await fn.Logout(AuthedRequest(verified.Token), default);
            Assert.IsType<UnauthorizedObjectResult>(await fn.Me(AuthedRequest(verified.Token), default));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ADMIN_EMAILS", null);
            await using var cleanup = _pg.NewDbContext();
            await cleanup.Database.ExecuteSqlRawAsync(
                "DELETE FROM sessions WHERE email='editor@synth.test'; DELETE FROM otp_codes WHERE email='editor@synth.test'; DELETE FROM editors WHERE email='editor@synth.test';");
        }
    }

    [SkippableFact]
    public async Task Auth_request_code_is_enumeration_safe_and_verify_guards_expiry_and_role()
    {
        RequireDocker();
        Environment.SetEnvironmentVariable("ADMIN_EMAILS", "boss@synth.test");
        await using var db = _pg.NewDbContext();
        var email = new FakeEmailSender();
        var fn = new AuthFunctions(db, email, NullLogger<AuthFunctions>.Instance);
        try
        {
            // UNKNOWN email → still 202 (enumeration-safe), but NO email is sent.
            var rc = await fn.RequestCode(JsonRequest(new { email = "stranger@nope.test" }), default);
            Assert.Equal(202, Assert.IsType<ObjectResult>(rc).StatusCode);
            Assert.Empty(email.Sent);

            // ADMIN email (in ADMIN_EMAILS) → emailed + verify resolves role=admin.
            Assert.Equal(202, Assert.IsType<ObjectResult>(await fn.RequestCode(JsonRequest(new { email = "boss@synth.test" }), default)).StatusCode);
            var adminCode = ExtractCode(Assert.Single(email.Sent).Text);
            var adminOk = Assert.IsType<OkObjectResult>(await fn.Verify(JsonRequest(new { email = "boss@synth.test", code = adminCode }), default));
            Assert.Equal("admin", Assert.IsType<VerifyResponseDto>(adminOk.Value!).Role);

            // EXPIRED code → 400 (seed a row whose expires_at is in the past).
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO otp_codes (email, code_hash, expires_at) VALUES ('exp@synth.test', {AuthTokens.Sha256Hex("424242")}, now() - interval '1 minute')");
            Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(
                await fn.Verify(JsonRequest(new { email = "exp@synth.test", code = "424242" }), default)).StatusCode);

            // OVER attempt cap → 400 even with the right code (seed attempt_count=5).
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO otp_codes (email, code_hash, expires_at, attempt_count) VALUES ('lock@synth.test', {AuthTokens.Sha256Hex("424242")}, now() + interval '5 minutes', 5)");
            Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(
                await fn.Verify(JsonRequest(new { email = "lock@synth.test", code = "424242" }), default)).StatusCode);

            // A VALID code for an email that is neither admin nor editor → rejected (no session minted).
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO otp_codes (email, code_hash, expires_at) VALUES ('nobody@synth.test', {AuthTokens.Sha256Hex("424242")}, now() + interval '5 minutes')");
            Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(
                await fn.Verify(JsonRequest(new { email = "nobody@synth.test", code = "424242" }), default)).StatusCode);
            await using (var dbr = _pg.NewDbContext())
                Assert.False(await dbr.Sessions.AsNoTracking().AnyAsync(s => s.Email == "nobody@synth.test"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ADMIN_EMAILS", null);
            await using var cleanup = _pg.NewDbContext();
            await cleanup.Database.ExecuteSqlRawAsync(
                "DELETE FROM sessions WHERE email LIKE '%@synth.test'; DELETE FROM otp_codes WHERE email LIKE '%@synth.test';");
        }
    }

    [SkippableFact]
    public async Task Auth_request_access_is_uniform_records_and_rate_limited()
    {
        RequireDocker();
        Environment.SetEnvironmentVariable("ADMIN_EMAILS", "boss@synth.test");
        await using var db = _pg.NewDbContext();
        var email = new FakeEmailSender();
        var fn = new AuthFunctions(db, email, NullLogger<AuthFunctions>.Instance);
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO editors (email, added_by) VALUES ('known@req.test', 'boss@synth.test')");

            // The SAME uniform 200 message whether the requester is unknown OR already an editor — no status leak.
            var unknown = Assert.IsType<OkObjectResult>(await fn.RequestAccess(JsonRequest(new { email = "unknown@req.test" }), default));
            var knownEditor = Assert.IsType<OkObjectResult>(await fn.RequestAccess(JsonRequest(new { email = "known@req.test" }), default));
            Assert.Equal(
                Assert.IsType<MessageDto>(unknown.Value!).Message,
                Assert.IsType<MessageDto>(knownEditor.Value!).Message);

            // Both are RECORDED (admin visibility) and both PAGE the admin (boss@synth.test).
            await using (var dbr = _pg.NewDbContext())
            {
                Assert.True(await dbr.AccessRequests.AsNoTracking().AnyAsync(a => a.Email == "unknown@req.test"));
                Assert.True(await dbr.AccessRequests.AsNoTracking().AnyAsync(a => a.Email == "known@req.test"));
            }
            Assert.Equal(2, email.Sent.Count);
            Assert.All(email.Sent, s => Assert.Equal("boss@synth.test", s.To));

            // Rate-limit per email: after the cap (3/24h), further requests are uniform 200 but DON'T record/page.
            for (var i = 0; i < 5; i++)
                await fn.RequestAccess(JsonRequest(new { email = "spammer@req.test" }), default);
            await using (var dbr = _pg.NewDbContext())
                Assert.Equal(3, await dbr.AccessRequests.AsNoTracking().CountAsync(a => a.Email == "spammer@req.test"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ADMIN_EMAILS", null);
            await using var cleanup = _pg.NewDbContext();
            await cleanup.Database.ExecuteSqlRawAsync(
                "DELETE FROM access_requests WHERE email LIKE '%@req.test'; DELETE FROM editors WHERE email LIKE '%@req.test';");
        }
    }

    // ─── Phase 12 slice 3 — editor (user) management, admin-only ────────────────────────────────────

    private static HttpRequest AuthReq(string? token = null)
    {
        var ctx = new DefaultHttpContext();
        if (token is not null) ctx.Request.Headers.Authorization = $"Bearer {token}";
        return ctx.Request;
    }

    private static HttpRequest AuthJsonReq(string? token, object body)
    {
        var ctx = new DefaultHttpContext();
        if (token is not null) ctx.Request.Headers.Authorization = $"Bearer {token}";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentType = "application/json";
        ctx.Request.ContentLength = bytes.Length;
        return ctx.Request;
    }

    private static int StatusOf(IActionResult r) => r switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        _ => 200,
    };

    [SkippableFact]
    public async Task Editors_management_is_admin_only_and_audited()
    {
        RequireDocker();
        Environment.SetEnvironmentVariable("ADMIN_EMAILS", "boss@ed.test");
        await using var db = _pg.NewDbContext();
        var auth = new AuthPrincipalService(db);
        var audit = new AuditScope();
        var fn = new EditorsFunctions(db, auth, audit);
        const string adminTok = "swt_admin_ed", editorTok = "swt_editor_ed";
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO editors (email, added_by) VALUES ('ed@ed.test', 'boss@ed.test')");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO sessions (token_hash, email, expires_at) VALUES ({AuthTokens.Sha256Hex(adminTok)}, 'boss@ed.test', now() + interval '1 hour')");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO sessions (token_hash, email, expires_at) VALUES ({AuthTokens.Sha256Hex(editorTok)}, 'ed@ed.test', now() + interval '1 hour')");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO access_requests (email) VALUES ('want@ed.test'), ('ed@ed.test')");

            // No token → 401; editor token → 403 (incl. the GET list, which the verb-gate doesn't cover).
            Assert.IsType<UnauthorizedObjectResult>(await fn.ListEditors(AuthReq(), default));
            Assert.Equal(403, StatusOf(await fn.ListEditors(AuthReq(editorTok), default)));
            Assert.Equal(403, StatusOf(await fn.AddEditor(AuthJsonReq(editorTok, new { email = "x@ed.test" }), default)));
            Assert.Equal(403, StatusOf(await fn.RemoveEditor(AuthReq(editorTok), "ed@ed.test", default)));

            // Admin lists → sees the seeded editor.
            var listed = (List<EditorDto>)Assert.IsType<OkObjectResult>(await fn.ListEditors(AuthReq(adminTok), default)).Value!;
            Assert.Contains(listed, e => e.Email == "ed@ed.test");

            // Admin adds (email normalized) → 201 + audit diff recorded; duplicate → 409.
            Assert.Equal(201, StatusOf(await fn.AddEditor(AuthJsonReq(adminTok, new { email = "New@ED.test" }), default)));
            Assert.True(await db.Editors.AnyAsync(e => e.Email == "new@ed.test"));
            Assert.Equal("editor", audit.Diff?.TargetType);
            Assert.Equal("new@ed.test", audit.Diff?.TargetId);
            Assert.IsType<ConflictObjectResult>(await fn.AddEditor(AuthJsonReq(adminTok, new { email = "new@ed.test" }), default));

            // Access-requests: the stranger shows; an existing editor is filtered out.
            var reqs = (List<AccessRequestDto>)Assert.IsType<OkObjectResult>(await fn.ListAccessRequests(AuthReq(adminTok), default)).Value!;
            Assert.Contains(reqs, r => r.Email == "want@ed.test");
            Assert.DoesNotContain(reqs, r => r.Email == "ed@ed.test");

            // Admin removes → 204, gone; removing a non-editor → 404.
            Assert.IsType<NoContentResult>(await fn.RemoveEditor(AuthReq(adminTok), "new@ed.test", default));
            Assert.False(await db.Editors.AnyAsync(e => e.Email == "new@ed.test"));
            Assert.IsType<NotFoundObjectResult>(await fn.RemoveEditor(AuthReq(adminTok), "ghost@ed.test", default));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ADMIN_EMAILS", null);
            await using var cleanup = _pg.NewDbContext();
            await cleanup.Database.ExecuteSqlRawAsync(
                "DELETE FROM sessions WHERE email LIKE '%@ed.test'; DELETE FROM editors WHERE email LIKE '%@ed.test'; DELETE FROM access_requests WHERE email LIKE '%@ed.test';");
        }
    }
}
