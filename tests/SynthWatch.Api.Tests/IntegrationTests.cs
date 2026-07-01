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

    // ★ GET /reports/slo — fleet + per-check error budget (P5 v1). Proves: only SLO-having checks appear;
    // per-check budget math; the fleet rollup is ADDITIVE (not an average of per-check %); tag scoping;
    // insufficientData + empty-scope honesty (never a fabricated %); and pins the response wire shape.
    [SkippableFact]
    public async Task Slo_report_is_additive_tag_scoped_and_honest_about_thin_data()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        // A: 200 runs / 2 down (budget 2, pct 0). B: 100 runs / 0 down (budget 1, pct 1). Both tagged team:slorep.
        // C: tagged team:slorep but NO slo_target → must be EXCLUDED. D: tagged team:thin, target 0.95, 5 runs → thin.
        await db.Database.ExecuteSqlRawAsync("""
            DO $$
            DECLARE a bigint; b bigint; c bigint; d bigint;
            BEGIN
              INSERT INTO checks (name, kind, target_url, slo_target) VALUES ('slo-rep-a','http','https://a.ex',0.99) RETURNING id INTO a;
              INSERT INTO checks (name, kind, target_url, slo_target) VALUES ('slo-rep-b','http','https://b.ex',0.99) RETURNING id INTO b;
              INSERT INTO checks (name, kind, target_url)            VALUES ('slo-rep-c','http','https://c.ex')      RETURNING id INTO c;
              INSERT INTO checks (name, kind, target_url, slo_target) VALUES ('slo-rep-d','http','https://d.ex',0.95) RETURNING id INTO d;
              INSERT INTO check_tags (check_id, key, value) VALUES (a,'team','slorep'), (b,'team','slorep'), (c,'team','slorep'), (d,'team','thin');
              FOR i IN 1..200 LOOP INSERT INTO runs (check_id, status, started_at) VALUES (a, CASE WHEN i <= 2 THEN 'fail' ELSE 'pass' END, now() - (i || ' minutes')::interval); END LOOP;
              FOR i IN 1..100 LOOP INSERT INTO runs (check_id, status, started_at) VALUES (b, 'pass', now() - (i || ' minutes')::interval); END LOOP;
              FOR i IN 1..50  LOOP INSERT INTO runs (check_id, status, started_at) VALUES (c, 'pass', now() - (i || ' minutes')::interval); END LOOP;
              FOR i IN 1..5   LOOP INSERT INTO runs (check_id, status, started_at) VALUES (d, 'pass', now() - (i || ' minutes')::interval); END LOOP;
            END $$;
            """);
        try
        {
            var reports = new ReportsFunctions(db);

            // ── scope to team:slorep → exactly A + B (C excluded: it carries the tag but has NO slo_target) ──
            var scoped = Assert.IsType<SloReportResponseDto>(Assert.IsType<OkObjectResult>(
                await reports.GetSloReport(Request("?window=30d&tag=team:slorep"), default)).Value!);
            Assert.Equal(new[] { "slo-rep-a", "slo-rep-b" }, scoped.Items.Select(i => i.CheckName).OrderBy(n => n).ToArray());
            Assert.DoesNotContain(scoped.Items, i => i.CheckName == "slo-rep-c"); // no slo_target ⇒ opt-out

            var a = scoped.Items.First(i => i.CheckName == "slo-rep-a");
            var b = scoped.Items.First(i => i.CheckName == "slo-rep-b");
            Assert.True(Math.Abs(a.Budget - 2m) < 0.1m);   // (1-0.99)*200
            Assert.Equal(2, a.Consumed);                   // 2 down
            Assert.True(Math.Abs(a.Remaining - 0m) < 0.1m);
            Assert.True(a.RemainingPct is decimal ap && Math.Abs(ap - 0m) < 0.05m);  // budget fully consumed
            Assert.False(a.InsufficientData);
            Assert.True(Math.Abs(b.Budget - 1m) < 0.1m);
            Assert.Equal(0, b.Consumed);
            Assert.True(b.RemainingPct is decimal bp && Math.Abs(bp - 1m) < 0.05m);

            // ── ★ fleet rollup is ADDITIVE: Σbudget=3, Σconsumed=2 → 1 - 2/3 = 0.333…, NOT the mean of the
            //     per-check %s (0 and 1 → 0.5). This is the teeth: an averaging bug would read 0.5. ──
            Assert.True(Math.Abs(scoped.Fleet.Budget - 3m) < 0.2m);
            Assert.Equal(2, scoped.Fleet.Consumed);
            Assert.True(Math.Abs(scoped.Fleet.Remaining - 1m) < 0.2m);
            Assert.False(scoped.Fleet.InsufficientData);
            var fleetPct = scoped.Fleet.RemainingPct!.Value;
            Assert.True(Math.Abs(fleetPct - 0.3333m) < 0.02m, $"expected additive ~0.333, got {fleetPct}");
            Assert.True(Math.Abs(fleetPct - 0.5m) > 0.1m, "fleet % must NOT be the average of per-check %s");

            // ── insufficientData honesty: D has 5 runs (< 20) → flagged + a NULL pct, never a fabricated number ──
            var thin = Assert.IsType<SloReportResponseDto>(Assert.IsType<OkObjectResult>(
                await reports.GetSloReport(Request("?tag=team:thin"), default)).Value!);
            var d = Assert.Single(thin.Items);
            Assert.True(d.InsufficientData);
            Assert.Null(d.RemainingPct);
            Assert.True(thin.Fleet.InsufficientData);
            Assert.Null(thin.Fleet.RemainingPct);

            // ── scoped-empty honesty: a tag matching nothing → empty items + honest insufficient fleet, no fake % ──
            var empty = Assert.IsType<SloReportResponseDto>(Assert.IsType<OkObjectResult>(
                await reports.GetSloReport(Request("?tag=team:doesnotexist"), default)).Value!);
            Assert.Empty(empty.Items);
            Assert.True(empty.Fleet.InsufficientData);
            Assert.Null(empty.Fleet.RemainingPct);

            // ── window validation ──
            Assert.IsType<BadRequestObjectResult>(await reports.GetSloReport(Request("?window=1y"), default));

            // ── ★ wire-shape pin (#123): top-level + per-item + fleet key sets ──
            var web = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
            var root = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(scoped, web)).RootElement;
            Assert.Equal(new[] { "fleet", "items", "window" }, root.EnumerateObject().Select(p => p.Name).OrderBy(k => k).ToArray());
            Assert.Equal(new[] { "budget", "consumed", "downRuns", "insufficientData", "remaining", "remainingPct", "totalRuns" },
                root.GetProperty("fleet").EnumerateObject().Select(p => p.Name).OrderBy(k => k).ToArray());
            Assert.Equal(
                new[] { "budget", "burnRate", "burnState", "checkId", "checkName", "consumed", "downRuns", "insufficientData", "kind", "remaining", "remainingPct", "reportedBurn", "target", "totalRuns" },
                root.GetProperty("items")[0].EnumerateObject().Select(p => p.Name).OrderBy(k => k).ToArray());
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM check_tags WHERE check_id IN (SELECT id FROM checks WHERE name LIKE 'slo-rep-%'); DELETE FROM checks WHERE name LIKE 'slo-rep-%';");
        }
    }

    // ★ GET /reports/mttr — fleet incident analytics. Proves: MTTR excludes OPEN incidents (counts them);
    // mean≠median both present (skewed durations); the fleet mean/median are over the FULL resolved set,
    // NOT mean-of-means / median-of-medians; insufficientData → null (never 0); classification unclassified
    // shown last; tag scoping; scoped-empty honesty; and pins the response wire shape.
    [SkippableFact]
    public async Task Mttr_report_excludes_open_reports_median_and_mean_and_is_honest()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        // A: 3 resolved (60/120/1200s — skewed → mean 460 ≠ median 120) + 1 OPEN; 2 real-outage + 2 unclassified;
        //    consecutive_failures 2 everywhere (MTTD proxy = 2×300 = 600). B: 1 resolved (300s) → thin.
        //    C: a resolved incident but NOT tagged team:mttr → excluded by tag scope.
        await db.Database.ExecuteSqlRawAsync("""
            DO $$
            DECLARE a bigint; b bigint; c bigint;
            BEGIN
              INSERT INTO checks (name, kind, target_url, interval_seconds) VALUES ('mttr-a','http','https://a.ex',300) RETURNING id INTO a;
              INSERT INTO checks (name, kind, target_url, interval_seconds) VALUES ('mttr-b','http','https://b.ex',300) RETURNING id INTO b;
              INSERT INTO checks (name, kind, target_url, interval_seconds) VALUES ('mttr-c','http','https://c.ex',300) RETURNING id INTO c;
              INSERT INTO check_tags (check_id, key, value) VALUES (a,'team','mttr'), (b,'team','mttr');
              INSERT INTO incidents (check_id, status, severity, opened_at, resolved_at, consecutive_failures, rca) VALUES
                (a,'resolved','critical', now()-interval '5 days', now()-interval '5 days'+interval '60 seconds',   2, jsonb_build_object('classification','real-outage')),
                (a,'resolved','critical', now()-interval '4 days', now()-interval '4 days'+interval '120 seconds',  2, jsonb_build_object('classification','real-outage')),
                (a,'resolved','warning',  now()-interval '3 days', now()-interval '3 days'+interval '1200 seconds', 2, NULL),
                (a,'open','critical',     now()-interval '1 day',  NULL,                                            2, NULL),
                (b,'resolved','critical', now()-interval '2 days', now()-interval '2 days'+interval '300 seconds',  1, jsonb_build_object('classification','real-outage')),
                (c,'resolved','critical', now()-interval '2 days', now()-interval '2 days'+interval '999 seconds',  1, jsonb_build_object('classification','selector-drift'));
            END $$;
            """);
        try
        {
            var reports = new ReportsFunctions(db);
            var scoped = Assert.IsType<MttrReportResponseDto>(Assert.IsType<OkObjectResult>(
                await reports.GetMttrReport(Request("?window=30d&tag=team:mttr"), default)).Value!);

            // scope → A + B (C excluded: not tagged team:mttr)
            Assert.Equal(new[] { "mttr-a", "mttr-b" }, scoped.Items.Select(i => i.CheckName).OrderBy(n => n).ToArray());

            // ── A: open EXCLUDED from durations but COUNTED; mean 460 ≠ median 120 (both present) ──
            var a = scoped.Items.First(i => i.CheckName == "mttr-a");
            Assert.Equal(3, a.ResolvedCount);
            Assert.Equal(1, a.OpenCount);                       // ★ open counted, not in the mean
            Assert.Equal(460d, a.MeanSeconds);
            Assert.Equal(120d, a.MedianSeconds);
            Assert.NotEqual(a.MeanSeconds, a.MedianSeconds);    // ★ skew visible — both reported
            Assert.False(a.InsufficientData);
            Assert.Equal(600d, a.MttdProxySeconds);             // 2 failures × 300s interval

            // ── B: 1 resolved (< MinResolved) → insufficientData + NULL mttr (never a fake number) ──
            var b = scoped.Items.First(i => i.CheckName == "mttr-b");
            Assert.Equal(1, b.ResolvedCount);
            Assert.True(b.InsufficientData);
            Assert.Null(b.MeanSeconds);
            Assert.Null(b.MedianSeconds);

            // ── ★ fleet over the FULL resolved set [60,120,300,1200]: mean 420, median 210 — NOT the
            //     mean-of-means (460,300 → 380) or median-of-medians (120). This is the teeth. ──
            Assert.Equal(4, scoped.Fleet.ResolvedCount);
            Assert.Equal(1, scoped.Fleet.OpenCount);
            Assert.Equal(5, scoped.Fleet.TotalIncidents);
            Assert.Equal(420d, scoped.Fleet.MeanSeconds!.Value);
            Assert.NotEqual(380d, scoped.Fleet.MeanSeconds!.Value);     // ≠ average of per-check means
            Assert.Equal(210d, scoped.Fleet.MedianSeconds!.Value);
            Assert.NotEqual(120d, scoped.Fleet.MedianSeconds!.Value);   // ≠ median of per-check medians
            Assert.False(scoped.Fleet.InsufficientData);

            // ── classification: real-outage(3) first, unclassified(2) LAST (never dropped) ──
            Assert.Equal("real-outage", scoped.Classification[0].Classification);
            Assert.Equal(3, scoped.Classification[0].Count);
            Assert.Equal("unclassified", scoped.Classification[^1].Classification);
            Assert.Equal(2, scoped.Classification[^1].Count);

            // ── trend: resolved incidents bucketed; totals reconcile to the fleet resolved count ──
            Assert.NotEmpty(scoped.Trend);
            Assert.Equal(4, scoped.Trend.Sum(t => t.ResolvedCount));

            // ── scoped-empty honesty: a tag matching nothing → empty everywhere + insufficient fleet, no fake % ──
            var empty = Assert.IsType<MttrReportResponseDto>(Assert.IsType<OkObjectResult>(
                await reports.GetMttrReport(Request("?tag=team:none"), default)).Value!);
            Assert.Empty(empty.Items);
            Assert.Empty(empty.Classification);
            Assert.Empty(empty.Trend);
            Assert.Equal(0, empty.Fleet.ResolvedCount);
            Assert.True(empty.Fleet.InsufficientData);
            Assert.Null(empty.Fleet.MeanSeconds);
            Assert.Null(empty.Fleet.MedianSeconds);

            // ── window validation ──
            Assert.IsType<BadRequestObjectResult>(await reports.GetMttrReport(Request("?window=1y"), default));

            // ── ★ wire-shape pin (#123): top-level + fleet + per-item key sets ──
            var web = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
            var root = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(scoped, web)).RootElement;
            Assert.Equal(new[] { "classification", "fleet", "items", "trend", "window" },
                root.EnumerateObject().Select(p => p.Name).OrderBy(k => k).ToArray());
            Assert.Equal(new[] { "insufficientData", "meanSeconds", "medianSeconds", "mttdProxySeconds", "openCount", "resolvedCount", "totalIncidents" },
                root.GetProperty("fleet").EnumerateObject().Select(p => p.Name).OrderBy(k => k).ToArray());
            Assert.Equal(new[] { "checkId", "checkName", "insufficientData", "kind", "meanSeconds", "medianSeconds", "mttdProxySeconds", "openCount", "resolvedCount" },
                root.GetProperty("items")[0].EnumerateObject().Select(p => p.Name).OrderBy(k => k).ToArray());
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM incidents WHERE check_id IN (SELECT id FROM checks WHERE name LIKE 'mttr-%'); " +
                "DELETE FROM check_tags WHERE check_id IN (SELECT id FROM checks WHERE name LIKE 'mttr-%'); " +
                "DELETE FROM checks WHERE name LIKE 'mttr-%';");
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
        var fn = new ReconcileFunctions(db, new FakeRunnerJobTrigger(),
            Microsoft.Extensions.Options.Options.Create(new SynthWatch.Api.Infrastructure.RunnerJobOptions()));

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

    // ★ The repeatable ?tag=key:value filter scopes EVERY report aggregate to the tagged subset (multi-select
    // AND), so a tag-filtered reports surface shows tag-scoped CWV / trend / verdict-breakdown — not the fleet
    // numbers. Empty filter = whole fleet (no-op). A tag with no matching checks = honest empty, never a lie.
    [SkippableFact]
    public async Task Reports_tag_filter_scopes_every_aggregate_to_the_tagged_subset()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        // tag-api (http, team:platform + env:prod): rollup + 50 runs + a real-outage incident.
        // tag-web (browser, team:web): rollup + 20 runs w/ web-vitals + a flaky-transient incident.
        await db.Database.ExecuteSqlRawAsync("""
            DO $$
            DECLARE api_id bigint; web_id bigint; rid bigint; i int;
            BEGIN
              INSERT INTO checks (name,kind,target_url) VALUES ('tag-api','http','https://a.example') RETURNING id INTO api_id;
              INSERT INTO checks (name,kind,target_url,flow_name) VALUES ('tag-web','browser','https://w.example','tag-flow') RETURNING id INTO web_id;
              INSERT INTO check_tags (check_id,key,value) VALUES (api_id,'team','platform'),(api_id,'env','prod'),(web_id,'team','web');
              INSERT INTO daily_check_rollup (check_id,day,up_count,down_count,total_count,availability_pct,latency_count,duration_avg_ms,duration_p95_ms)
                VALUES (api_id, CURRENT_DATE-1, 50,5,55, 90.9, 50, 100, 120);
              INSERT INTO daily_check_rollup (check_id,day,up_count,down_count,total_count,availability_pct,latency_count,duration_avg_ms,duration_p95_ms,vitals_count,lcp_p75_ms)
                VALUES (web_id, CURRENT_DATE-1, 20,0,20, 100.0, 20, 200, 250, 20, 1300);
              FOR i IN 1..50 LOOP
                INSERT INTO runs (check_id,status,started_at,finished_at,duration_ms)
                  VALUES (api_id,'pass',(CURRENT_DATE-1)::timestamptz + interval '6 hours',(CURRENT_DATE-1)::timestamptz + interval '6 hours',100);
              END LOOP;
              FOR i IN 1..20 LOOP
                INSERT INTO runs (check_id,status,started_at,finished_at,duration_ms)
                  VALUES (web_id,'pass',(CURRENT_DATE-1)::timestamptz + interval '6 hours',(CURRENT_DATE-1)::timestamptz + interval '6 hours',200) RETURNING id INTO rid;
                INSERT INTO run_metrics (run_id,lcp_ms,fcp_ms,ttfb_ms,cls) VALUES (rid,1300,800,150,0.05);
              END LOOP;
              INSERT INTO incidents (check_id,status,severity,opened_at,rca)
                VALUES (api_id,'resolved','critical',now() - interval '1 day','{{"classification":"real-outage"}}'),
                       (web_id,'resolved','warning',now() - interval '1 day','{{"classification":"flaky-transient"}}');
            END $$;
            """);
        try
        {
            var fn = new ReportsFunctions(db);

            // ── AVAILABILITY scoped to team:platform → only tag-api (tag-web excluded). ──
            var avail = Assert.IsType<AvailabilityReportDto>(Assert.IsType<OkObjectResult>(
                await fn.GetAvailabilityReport(Request("?window=30d&tag=team:platform"), default)).Value!);
            var ag = Assert.Single(avail.Groups);
            Assert.Equal(50, ag.UpCount);                                  // tag-api only — NOT 70 (fleet)
            Assert.Single(ag.Checks, c => c.CheckName == "tag-api");
            Assert.DoesNotContain(ag.Checks, c => c.CheckName == "tag-web");
            Assert.True(ag.Series.Count >= 1);                             // tag-scoped trend present

            // ── PERFORMANCE scoped to team:web → web-vitals present (browser), latency from tag-web only. ──
            var perf = Assert.IsType<PerformanceReportDto>(Assert.IsType<OkObjectResult>(
                await fn.GetPerformanceReport(Request("?window=30d&tag=team:web"), default)).Value!);
            var pgrp = Assert.Single(perf.Groups);
            Assert.NotNull(pgrp.WebVitals);
            Assert.Equal(1300, pgrp.WebVitals!.LcpP75Ms);                  // tag-scoped CWV
            Assert.Single(pgrp.Checks, c => c.CheckName == "tag-web");
            Assert.NotEmpty(pgrp.Series);                                  // tag-scoped latency trend

            // ── INCIDENT BREAKDOWN scoped to team:platform → only the real-outage; precision 1.0 (honest). ──
            var brk = Assert.IsType<IncidentBreakdownDto>(Assert.IsType<OkObjectResult>(
                await fn.GetIncidentBreakdown(Request("?window=30d&tag=team:platform"), default)).Value!);
            Assert.Equal(1, brk.Total);
            Assert.Equal(1m, brk.Precision);                              // 1 real / 1 classified
            Assert.Contains(brk.Buckets, b => b.Classification == "real-outage" && b.Count == 1);
            Assert.DoesNotContain(brk.Buckets, b => b.Classification == "flaky-transient");

            // ── MULTI-TAG AND: both tags on tag-api → matches; an impossible AND → empty (not a fake number). ──
            var both = Assert.IsType<AvailabilityReportDto>(Assert.IsType<OkObjectResult>(
                await fn.GetAvailabilityReport(Request("?window=30d&tag=team:platform&tag=env:prod"), default)).Value!);
            Assert.Equal(50, Assert.Single(both.Groups).UpCount);
            var none = Assert.IsType<AvailabilityReportDto>(Assert.IsType<OkObjectResult>(
                await fn.GetAvailabilityReport(Request("?window=30d&tag=team:platform&tag=env:nope"), default)).Value!);
            Assert.Empty(none.Groups);                                    // no check has BOTH → honest empty
            var brkNone = Assert.IsType<IncidentBreakdownDto>(Assert.IsType<OkObjectResult>(
                await fn.GetIncidentBreakdown(Request("?window=30d&tag=team:nope"), default)).Value!);
            Assert.Equal(0, brkNone.Total);
            Assert.Null(brkNone.Precision);                              // ★ honest-empty: null, NOT 0% "real"

            // ── NO tag param → whole fleet (the filter is a no-op when absent; regression guard). ──
            var all = Assert.IsType<AvailabilityReportDto>(Assert.IsType<OkObjectResult>(
                await fn.GetAvailabilityReport(Request("?window=30d"), default)).Value!);
            Assert.Equal(70, Assert.Single(all.Groups).UpCount);          // 50 + 20 — both checks
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM checks WHERE name IN ('tag-api','tag-web');");
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
            static RunsPage Page(IActionResult r) =>
                Assert.IsType<RunsPage>(Assert.IsType<OkObjectResult>(r).Value!);

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
            // ── FRESHNESS SIGNAL: latestRunId = the most-recent run id for the check (unwindowed). On the
            //    default page that IS the newest item, so the client sees it's looking at current data.
            Assert.Equal(expectedRecent[0], def.LatestRunId);
            Assert.Equal(def.Items[0].Id, def.LatestRunId);

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
            // ★ THE FROZEN-`to` CASE, made provable in-band: this WINDOWED page shows only the 3 old runs, but
            //    latestRunId is the TRUE most-recent run (outside the window) — so a client can prove newer data
            //    exists (latestRunId > the windowed page's newest id) rather than guessing "nothing newer".
            Assert.Equal(expectedRecent[0], old.LatestRunId);
            Assert.True(old.LatestRunId > old.Items[0].Id);

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

    // ── B10 enable gate (PUT /locations): a sensitive check can't be enabled without redaction ──
    [SkippableFact]
    public async Task B10_sensitive_check_without_redaction_cannot_be_enabled_via_locations()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO locations (name, enabled) VALUES ('b10loc', true) ON CONFLICT (name) DO UPDATE SET enabled = true;
            INSERT INTO checks (name, kind, target_url, sensitive) VALUES ('b10-unwired', 'http', 'https://x.example', true);
            """);
        try
        {
            var id = await db.Checks.Where(c => c.Name == "b10-unwired").Select(c => c.Id).FirstAsync();
            var res = await new LocationsFunctions(db).SetCheckLocations(JsonRequest(new { locations = new[] { "b10loc" } }), id, default);
            var bad = Assert.IsType<BadRequestObjectResult>(res); // refused — B10 (before any cursor is inserted)
            Assert.Equal(400, bad.StatusCode);
            Assert.Contains("B10", System.Text.Json.JsonSerializer.Serialize(bad.Value), StringComparison.Ordinal);
            // The check stays inert (no cursor) — the gate returns before the INSERT.
            Assert.Equal(0, await db.CheckLocations.CountAsync(cl => cl.CheckId == id));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM checks WHERE name = 'b10-unwired'; DELETE FROM locations WHERE name = 'b10loc';");
        }
    }

    [SkippableFact]
    public async Task B10_sensitive_check_WITH_redaction_can_be_enabled()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO locations (name, enabled) VALUES ('b10loc', true) ON CONFLICT (name) DO UPDATE SET enabled = true;
            INSERT INTO checks (name, kind, target_url, sensitive, redact_patterns)
                VALUES ('b10-wired', 'http', 'https://x.example', true, '["token=\\S+"]'::jsonb);
            """);
        try
        {
            var id = await db.Checks.Where(c => c.Name == "b10-wired").Select(c => c.Id).FirstAsync();
            var res = await new LocationsFunctions(db).SetCheckLocations(JsonRequest(new { locations = new[] { "b10loc" } }), id, default);
            Assert.IsType<OkObjectResult>(res); // redaction declared → enable allowed
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM checks WHERE name = 'b10-wired'; DELETE FROM locations WHERE name = 'b10loc';");
        }
    }

    [SkippableFact]
    public async Task B10_non_sensitive_check_enable_is_unchanged()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO locations (name, enabled) VALUES ('b10loc', true) ON CONFLICT (name) DO UPDATE SET enabled = true;
            INSERT INTO checks (name, kind, target_url) VALUES ('b10-normal', 'http', 'https://x.example');
            """);
        try
        {
            var id = await db.Checks.Where(c => c.Name == "b10-normal").Select(c => c.Id).FirstAsync();
            var res = await new LocationsFunctions(db).SetCheckLocations(JsonRequest(new { locations = new[] { "b10loc" } }), id, default);
            Assert.IsType<OkObjectResult>(res); // no sensitive flag → no new restriction
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM checks WHERE name = 'b10-normal'; DELETE FROM locations WHERE name = 'b10loc';");
        }
    }

    [SkippableFact]
    public async Task B10_created_check_is_never_sensitive_via_the_api()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        try
        {
            var res = await new ChecksFunctions(db).CreateCheck(
                JsonRequest(new { name = "b10-create", kind = "http", targetUrl = "https://x.example" }), default);
            Assert.IsType<ObjectResult>(res); // created (201)
            var sensitive = await db.Checks.Where(c => c.Name == "b10-create").Select(c => c.Sensitive).FirstAsync();
            Assert.False(sensitive); // the create DTO can't set sensitive → always false (safe; no redaction needed)
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM checks WHERE name = 'b10-create';");
        }
    }

    // ★ The non-browser ssl/cert create path (the 3 cert monitors' shape) against the REAL schema + constraints:
    // an ssl check creates WITHOUT spec_path/flow_name and does NOT trip browser_needs_flow or the spec_path gate
    // (those are browser-only). Matches the known-good id-10 (Wegmans cert) shape.
    [SkippableFact]
    public async Task Ssl_cert_monitor_creates_matching_id10_shape_without_browser_or_spec_gates()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        try
        {
            // The Meals2Go cert monitor: kind=ssl, https URL, warn=30 — no spec_path/flow_name, sensitive default false.
            var res = await new ChecksFunctions(db).CreateCheck(
                JsonRequest(new { name = "Meals2Go cert", kind = "ssl", targetUrl = "https://www.meals2go.com", certExpiryWarnDays = 30 }),
                default);
            Assert.Equal(201, Assert.IsType<ObjectResult>(res).StatusCode); // created — the constraints did NOT reject it

            var c = await db.Checks.AsNoTracking().Where(x => x.Name == "Meals2Go cert").FirstAsync();
            Assert.Equal("ssl", c.Kind);
            Assert.Equal("https://www.meals2go.com", c.TargetUrl);
            Assert.Null(c.SpecPath);                 // ★ NOT a Git-spec check — no spec_path (id-10 shape)
            Assert.Null(c.FlowName);                 // ★ browser_needs_flow not tripped (ssl needs no flow_name)
            Assert.False(c.Sensitive);               // cert handshake reads only the public cert — no auth/PII
            Assert.Equal(30, c.CertExpiryWarnDays);  // the warn window (meals2go ~19d → WARN on first run)
            Assert.True(c.Enabled);                  // create defaults enabled=true, like id 10

            // ★ A bare ssl host (not an https URL) is REJECTED by the same validator (validate-don't-trust holds).
            var bad = await new ChecksFunctions(db).CreateCheck(
                JsonRequest(new { name = "bad-ssl", kind = "ssl", targetUrl = "www.meals2go.com" }), default);
            Assert.Equal(400, StatusOf(bad));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM checks WHERE name IN ('Meals2Go cert','bad-ssl');");
        }
    }

    [SkippableFact]
    public async Task Run_without_a_trace_returns_404()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        var fn = new ArtifactsFunctions(db, new ArtifactReader(new DefaultAzureCredential(), NullLogger<ArtifactReader>.Instance));
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

    // ★ F-05: a PRESENT dimension whose entry is MISSING channelIds (inner write-shape drift) must 400 and
    // leave routes UNTOUCHED — never coalesce-to-empty → DELETE-then-insert-nothing → wipe + 200. This is the
    // silent-integrity class on the alerting path: invisible until an alert doesn't fire. (Distinct from the
    // #66 wrong-TOP-LEVEL-key guard above; this anchors the INNER { channelIds } shape.)
    [SkippableFact]
    public async Task Routing_put_rejects_a_present_dimension_missing_channelIds_without_wiping()
    {
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
            // Known state: critical -> channel 1.
            Assert.IsType<OkObjectResult>(await rt.SetRouting(
                JsonRequest(new RoutingDto { Severity = new() { ["critical"] = new ChannelIdsDto(new long[] { 1 }) } }), default));
            Assert.Equal(1, await RouteCount());

            // severity PRESENT but the entry omits channelIds (e.g. a client that renamed the inner key) ->
            // 400, routes UNTOUCHED (the wipe-on-mismatch this fixes).
            Assert.IsType<BadRequestObjectResult>(await rt.SetRouting(
                JsonRequest(new { severity = new { critical = new { } } }), default));
            Assert.Equal(1, await RouteCount());

            // perCheck PRESENT but the entry omits channelIds -> 400, still untouched.
            Assert.IsType<BadRequestObjectResult>(await rt.SetRouting(
                JsonRequest(new { perCheck = new Dictionary<string, object> { ["1"] = new { } } }), default));
            Assert.Equal(1, await RouteCount());

            // ✓ the explicit per-entry clear (channelIds:[]) is STILL allowed — present-but-empty is intentional.
            var ok = Assert.IsType<RoutingDto>(Assert.IsType<OkObjectResult>(await rt.SetRouting(
                JsonRequest(new RoutingDto { Severity = new() { ["critical"] = new ChannelIdsDto(System.Array.Empty<long>()) } }), default)).Value!);
            Assert.True(ok.Severity is null || !ok.Severity.ContainsKey("critical") || ok.Severity["critical"].ChannelIds.Count == 0);
            Assert.Equal(0, await RouteCount()); // critical cleared (intended), not a malformed wipe
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
        public string? LastJobName;
        public bool Result = true;
        public Task<bool> StartAsync(CancellationToken ct) => StartAsync("synthwatch-runner-job", ct);
        public Task<bool> StartAsync(string jobName, CancellationToken ct)
        {
            StartCount++;
            LastJobName = jobName;
            return Task.FromResult(Result);
        }
    }

    // ── POST /reconcile/trigger — starts the RECONCILE job (no DB touched on this path) ──
    private static ReconcileFunctions ReconcileFn(FakeRunnerJobTrigger trigger)
    {
        var opts = new DbContextOptionsBuilder<SynthWatch.Api.Data.SynthWatchDbContext>().UseNpgsql("Host=localhost;Database=none").Options;
        return new ReconcileFunctions(new SynthWatch.Api.Data.SynthWatchDbContext(opts), trigger,
            Microsoft.Extensions.Options.Options.Create(new SynthWatch.Api.Infrastructure.RunnerJobOptions()));
    }

    [Fact]
    public async Task Reconcile_trigger_starts_the_reconcile_job_and_202s()
    {
        var trigger = new FakeRunnerJobTrigger();
        var result = await ReconcileFn(trigger).TriggerReconcile(Request(), default);

        var dto = Assert.IsType<ReconcileTriggeredDto>(Assert.IsType<ObjectResult>(result).Value);
        Assert.True(dto.Triggered);
        Assert.Equal("synthwatch-reconcile-job", trigger.LastJobName); // ★ the reconcile job, NOT the runner job
        Assert.Equal(1, trigger.StartCount);
    }

    [Fact]
    public async Task Reconcile_trigger_returns_503_on_a_failed_start_not_a_500()
    {
        var result = await ReconcileFn(new FakeRunnerJobTrigger { Result = false }).TriggerReconcile(Request(), default);
        Assert.Equal(503, Assert.IsType<ObjectResult>(result).StatusCode); // clean non-2xx (trigger logged the reason), not an unhandled 500
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

    // A denied request now leaves a durable audit_log row — exercised through the SAME isolated-context write
    // (AuditWriter.TryPersistAsync) the middleware runs on a 401/403.
    [SkippableFact]
    public async Task Denied_request_writes_an_audit_log_row()
    {
        RequireDocker();
        await using var ds = Npgsql.NpgsqlDataSource.Create(_pg.ConnectionString);
        var row = AuditWriter.BuildDenialRow("probe@deny.test", "8.8.8.8", "DELETE", "/api/checks/7", 403);
        try
        {
            Assert.True(await AuditWriter.TryPersistAsync(ds, row)); // the real middleware write path
            await using var read = _pg.NewDbContext();
            var saved = await read.AuditLogs.AsNoTracking()
                .Where(a => a.ActorEmail == "probe@deny.test").OrderByDescending(a => a.Id).FirstAsync();
            Assert.Equal("auth.denied", saved.Action);
            Assert.Equal(403, saved.StatusCode);
            Assert.False(saved.Success!.Value);
            Assert.Equal("DELETE", saved.HttpMethod);
            Assert.Equal("checks", saved.TargetType);
            Assert.Equal("7", saved.TargetId);
        }
        finally
        {
            await using var c = _pg.NewDbContext();
            await c.Database.ExecuteSqlRawAsync("DELETE FROM audit_log WHERE actor_email = 'probe@deny.test'");
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

    // AOAI "configured" so the endpoint gets past the inert check; never actually called in this test (the
    // no-trace 404 returns before any AOAI call).
    private sealed class ConfiguredFakeAoai : IAoaiClient
    {
        public bool IsConfigured => true;
        public Task<AoaiResult> ChatJsonAsync(string system, string user, CancellationToken ct) =>
            Task.FromResult(new AoaiResult(AoaiOutcome.Ok, "{}", "stop", 200, null));
    }

    // Captures the user message so a test can assert WHAT context the RCA actually fed the model.
    private sealed class CapturingFakeAoai : IAoaiClient
    {
        public string? LastUser { get; private set; }
        public bool IsConfigured => true;
        public Task<AoaiResult> ChatJsonAsync(string system, string user, CancellationToken ct)
        {
            LastUser = user;
            return Task.FromResult(new AoaiResult(AoaiOutcome.Ok, "{}", "stop", 200, null));
        }
    }

    // ── ai-insights: a SUCCESS run (trace_url null) whose monitor has NO success baseline yet → clean 404 ──
    [SkippableFact]
    public async Task AiInsights_success_run_with_no_baseline_returns_a_clean_no_trace_not_500()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO checks (name, kind, target_url) VALUES ('ins-nobase', 'http', 'https://x.example');
            INSERT INTO runs (check_id, status, started_at)
                SELECT id, 'pass', now() FROM checks WHERE name = 'ins-nobase';
            """);
        try
        {
            var runId = await db.Runs.Where(r => r.Check!.Name == "ins-nobase").Select(r => r.Id).FirstAsync();
            var fn = new AiInsightsFunctions(db,
                new ArtifactReader(new Azure.Identity.DefaultAzureCredential(), NullLogger<ArtifactReader>.Instance),
                new ConfiguredFakeAoai());

            var result = await fn.GetAiInsights(Request(), runId, default);

            // No per-run trace (success) and no success baseline yet → clean 404, never a 500.
            var nf = Assert.IsType<NotFoundObjectResult>(result);
            Assert.Equal(404, nf.StatusCode);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM checks WHERE name = 'ins-nobase';"); // cascades runs
        }
    }

    // Stubs the shared artifact reader so we can drive the transient-blob-error path without a live blob.
    private sealed class FakeArtifactReader(ArtifactBlob result) : IArtifactReader
    {
        public Task<ArtifactBlob> DownloadToMemoryAsync(string? url, string artifact, long id, CancellationToken ct) => Task.FromResult(result);
        public Task<ArtifactBlob> OpenStreamAsync(string? url, string artifact, long id, CancellationToken ct) => Task.FromResult(result);
    }

    // URL-aware reader returning a FRESH readable stream per call, keyed by a URL substring — needed once the
    // failing run is also extracted from its own zip (zip-first), so one reader serves run zip AND baseline zip.
    private sealed class FakeZipReader(params (string urlContains, byte[] zip)[] zips) : IArtifactReader
    {
        private Task<ArtifactBlob> Resolve(string? url)
        {
            foreach (var (key, bytes) in zips)
                if (url is not null && url.Contains(key, StringComparison.Ordinal))
                    return Task.FromResult(ArtifactBlob.Of(new MemoryStream(bytes, writable: false)));
            return Task.FromResult(ArtifactBlob.Missing);
        }
        public Task<ArtifactBlob> DownloadToMemoryAsync(string? url, string artifact, long id, CancellationToken ct) => Resolve(url);
        public Task<ArtifactBlob> OpenStreamAsync(string? url, string artifact, long id, CancellationToken ct) => Resolve(url);
    }

    private static byte[] TraceZipBytes(string networkNdjson, string consoleNdjson)
    {
        using var s = BuildTraceZip(networkNdjson, consoleNdjson);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static Stream BuildTraceZip(string networkNdjson, string consoleNdjson)
    {
        var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(z.CreateEntry("trace.network").Open())) w.Write(networkNdjson);
            using (var w = new StreamWriter(z.CreateEntry("trace.trace").Open())) w.Write(consoleNdjson);
        }
        ms.Position = 0;
        return ms;
    }

    // ── baseline-diff: zip-first failing signals (mutations) + the FAILED ASSERTION + on-demand baseline ──
    // The 849441 regression at the integration level: the RCA context MUST carry the failing assertion
    // (error_message/failed_step) AND the action-under-test's network result (the cart-items POST 200), both of
    // which the old path dropped. We assert what reaches the MODEL via a capturing fake.
    [SkippableFact]
    public async Task BaselineDiff_feeds_the_failing_assertion_and_the_action_network_result_to_the_model()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO checks (name, kind, target_url, success_trace_url, success_trace_at) " +
            "VALUES ('bdiff', 'http', 'https://x.example', 'https://x.blob.core.windows.net/c/success.zip', now());");
        // The failing run: a spec-code assertion error + a trace whose network has the cart-items POST 200.
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO runs (check_id, status, started_at, location, trace_url, failed_step, error_message) " +
            "SELECT id, 'error', now(), 'eastus2', 'https://x.blob.core.windows.net/c/run.zip', " +
            "'add cheese pizza to cart', 'Cannot read properties of undefined (reading ''toBeNull'')' " +
            "FROM checks WHERE name = 'bdiff';");
        try
        {
            var runId = await db.Runs.Where(r => r.Check!.Name == "bdiff").Select(r => r.Id).FirstAsync();
            // Failing zip: a 200 POST to cart-items (the action SUCCEEDED) + a FAILING-ONLY console error.
            var failingZip = TraceZipBytes(
                """{"type":"resource-snapshot","snapshot":{"request":{"url":"https://x.example/api/cart-items","method":"POST"},"response":{"status":200},"_resourceType":"fetch","time":40}}""",
                """{"type":"console","messageType":"error","text":"FAILING-ONLY region error","location":{"url":"https://x.example/"}}""");
            var baselineZip = TraceZipBytes("",
                """{"type":"console","messageType":"error","text":"BASELINE-ONLY benign warning","location":{"url":"https://x.example/"}}""");
            var aoai = new CapturingFakeAoai();
            var fn = new LocationDiffFunctions(db,
                new FakeZipReader(("run.zip", failingZip), ("success.zip", baselineZip)), aoai);

            var result = await fn.GetBaselineDiff(Request(), runId, default);
            var dto = Assert.IsType<LocationDiffDto>(Assert.IsType<OkObjectResult>(result).Value);

            Assert.True(dto.Configured);
            Assert.Equal("eastus2", dto.Failing.Location);
            Assert.Contains(dto.Diff.Console.OnlyInA, m => m.Text.Contains("FAILING-ONLY", StringComparison.Ordinal));
            Assert.Contains(dto.Diff.Console.OnlyInB, m => m.Text.Contains("BASELINE-ONLY", StringComparison.Ordinal));

            // ★ The new context REACHED THE MODEL: the failed assertion + the 2xx action under test.
            Assert.Contains("add cheese pizza to cart", aoai.LastUser!, StringComparison.Ordinal);
            Assert.Contains("toBeNull", aoai.LastUser!, StringComparison.Ordinal);
            Assert.Contains("cart-items", aoai.LastUser!, StringComparison.Ordinal);
            Assert.Contains("→ 200", aoai.LastUser!, StringComparison.Ordinal);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM checks WHERE name = 'bdiff';");
        }
    }

    [SkippableFact]
    public async Task BaselineDiff_returns_404_when_the_monitor_has_no_baseline()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        const string sig =
            """{"targetHost":"x.example","network":{"totalRequests":1,"wireKb":1,"thirdPartyCount":0,"failed":[],"slowest":[],"largest":[],"uncompressed":[],"topThirdParties":[]},"console":{"messages":[],"droppedInfoLog":0,"droppedExtensionNoise":0}}""";
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO checks (name, kind, target_url) VALUES ('bdiff-nobase', 'http', 'https://x.example');");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO runs (check_id, status, started_at, trace_url, trace_signals) " +
            "SELECT id, 'fail', now(), 'https://x.blob.core.windows.net/c/run.zip', {0}::jsonb " +
            "FROM checks WHERE name = 'bdiff-nobase';", sig);
        try
        {
            var runId = await db.Runs.Where(r => r.Check!.Name == "bdiff-nobase").Select(r => r.Id).FirstAsync();
            var fn = new LocationDiffFunctions(db,
                new FakeArtifactReader(ArtifactBlob.Of(BuildTraceZip("", ""))), new ConfiguredFakeAoai());
            var result = await fn.GetBaselineDiff(Request(), runId, default);
            Assert.IsType<NotFoundObjectResult>(result); // no success baseline → clean 404
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM checks WHERE name = 'bdiff-nobase';");
        }
    }

    // ── a non-404 blob error (throttle/transient) → a CLEAN response, never an unhandled 500 ──
    [SkippableFact]
    public async Task TraceSignals_returns_503_not_500_on_a_transient_blob_error()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO checks (name, kind, target_url) VALUES ('sig-unavail', 'http', 'https://x.example');
            INSERT INTO runs (check_id, status, started_at, trace_url)
                SELECT id, 'fail', now(), 'https://x.blob.core.windows.net/c/traces/t.zip' FROM checks WHERE name = 'sig-unavail';
            """);
        try
        {
            var runId = await db.Runs.Where(r => r.Check!.Name == "sig-unavail").Select(r => r.Id).FirstAsync();
            var fn = new ArtifactsFunctions(db, new FakeArtifactReader(ArtifactBlob.Unavailable));
            var result = await fn.GetTraceSignals(Request(), runId, default);
            Assert.Equal(503, Assert.IsType<ObjectResult>(result).StatusCode); // clean 503, NOT an unhandled 500
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM checks WHERE name = 'sig-unavail';");
        }
    }

    [SkippableFact]
    public async Task AiInsights_returns_retryable_unavailable_on_a_transient_blob_error()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO checks (name, kind, target_url) VALUES ('ins-unavail', 'http', 'https://x.example');
            INSERT INTO runs (check_id, status, started_at, trace_url)
                SELECT id, 'fail', now(), 'https://x.blob.core.windows.net/c/traces/t.zip' FROM checks WHERE name = 'ins-unavail';
            """);
        try
        {
            var runId = await db.Runs.Where(r => r.Check!.Name == "ins-unavail").Select(r => r.Id).FirstAsync();
            var fn = new AiInsightsFunctions(db, new FakeArtifactReader(ArtifactBlob.Unavailable), new ConfiguredFakeAoai());
            var result = await fn.GetAiInsights(Request(), runId, default);
            var dto = Assert.IsType<AiInsightsDto>(Assert.IsType<OkObjectResult>(result).Value);
            Assert.True(dto.Retryable);  // transient blob error → honest retryable, never a 500
            Assert.NotNull(dto.Note);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM checks WHERE name = 'ins-unavail';");
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
        var fn = new EditorsFunctions(db, auth, audit, NullLogger<EditorsFunctions>.Instance);
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

            // Dismiss access request → 204, row gone; idempotent (no-match → 204 too).
            Assert.IsType<NoContentResult>(await fn.DismissAccessRequest(AuthReq(adminTok), "want@ed.test", default));
            Assert.False(await db.AccessRequests.AnyAsync(a => a.Email == "want@ed.test"));
            // ★ The dismiss now RECORDS its audit diff again (restored — the prod 500 it was removed to isolate
            // was the grant gap, not this write). The middleware persists it via TryPersistAsync.
            Assert.Equal("access-request", audit.Diff?.TargetType);
            Assert.Equal("want@ed.test", audit.Diff?.TargetId);
            Assert.IsType<NoContentResult>(await fn.DismissAccessRequest(AuthReq(adminTok), "ghost@ed.test", default));

            // Editor can't dismiss access requests.
            Assert.Equal(403, StatusOf(await fn.DismissAccessRequest(AuthReq(editorTok), "want@ed.test", default)));

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

    // ★ The audit_log WRITE PATH for a dismiss — the #127-class gap (CI-compiled, never exercised). Proves the
    // handler RECORDS the diff AND it PERSISTS to audit_log under the now-present grants, with the right
    // Action/principal/target — end-to-end through the middleware's exact BuildRow + never-throw TryPersistAsync.
    [SkippableFact]
    public async Task Dismiss_access_request_persists_an_audit_row()
    {
        RequireDocker();
        Environment.SetEnvironmentVariable("ADMIN_EMAILS", "boss@dismiss.test");
        await using var db = _pg.NewDbContext();
        var audit = new AuditScope();
        var fn = new EditorsFunctions(db, new AuthPrincipalService(db), audit, NullLogger<EditorsFunctions>.Instance);
        const string adminTok = "swt_admin_dismiss";
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO sessions (token_hash, email, expires_at) VALUES ({AuthTokens.Sha256Hex(adminTok)}, 'boss@dismiss.test', now() + interval '1 hour')");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO access_requests (email) VALUES ('want@dismiss.test')");

            // Handler → 204 + records the audit diff on the scope (the restored _audit.Record call).
            Assert.IsType<NoContentResult>(await fn.DismissAccessRequest(AuthReq(adminTok), "want@dismiss.test", default));
            Assert.Equal("access-request", audit.Diff?.TargetType);

            // Persist exactly as AuthorizationMiddleware does → a REAL audit_log INSERT (the path the grant gap broke).
            await using var ds = Npgsql.NpgsqlDataSource.Create(_pg.ConnectionString);
            var row = AuditWriter.BuildRow(new Principal("boss@dismiss.test", Roles.Admin), "1.2.3.4",
                "DELETE", "/api/access-requests/want@dismiss.test", 204, success: true, audit.Diff);
            Assert.True(await AuditWriter.TryPersistAsync(ds, row)); // never-throws; true = row written under current grants

            await using var read = _pg.NewDbContext();
            var saved = await read.AuditLogs.AsNoTracking()
                .Where(a => a.ActorEmail == "boss@dismiss.test").OrderByDescending(a => a.Id).FirstAsync();
            Assert.Equal("delete", saved.Action);                 // ActionFor("DELETE")
            Assert.Equal("access-request", saved.TargetType);     // from the handler's diff
            Assert.Equal("want@dismiss.test", saved.TargetId);
            Assert.True(saved.Success!.Value);
            Assert.Equal("dismiss access request", saved.Note);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ADMIN_EMAILS", null);
            await using var c = _pg.NewDbContext();
            await c.Database.ExecuteSqlRawAsync(
                "DELETE FROM sessions WHERE email = 'boss@dismiss.test'; DELETE FROM access_requests WHERE email = 'want@dismiss.test'; DELETE FROM audit_log WHERE actor_email = 'boss@dismiss.test';");
        }
    }

    // ── List-endpoint SHAPE CONTRACT ────────────────────────────────────────────────────────────────────
    // The API-side anchor for the envelope-drift class: pins each list endpoint's TOP-LEVEL WIRE shape (bare
    // array vs which envelope + its exact keys) so a flip fails CI instead of silently emptying the dashboard.
    // Two prior prod incidents (routing {defaults,overrides} silent-wipe; performance-report nesting) shipped
    // because NO test pinned the shape. We assert the SERIALIZED JSON (camelCase = the actual wire, verified
    // against the live host which writes null keys) — catching BOTH a record-type swap AND a key rename/casing
    // change. NOT a full-DTO test — only the top-level shape that, if flipped, breaks a consumer.
    // ★ This pins the CURRENT shapes (per the task). Converging the 7 bare arrays onto {items} is a SEPARATE
    //   breaking change coordinated with the dashboard — deliberately NOT done here.
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    private static JsonElement WireRoot(IActionResult r)
    {
        var ok = Assert.IsType<OkObjectResult>(r);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value, WireJson));
        return doc.RootElement.Clone();
    }

    private static void AssertBareArray(IActionResult r) =>
        Assert.Equal(JsonValueKind.Array, WireRoot(r).ValueKind);

    private static void AssertEnvelope(IActionResult r, params string[] expectedKeys)
    {
        var root = WireRoot(r);
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        var actual = root.EnumerateObject().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedKeys.OrderBy(k => k, StringComparer.Ordinal).ToArray(), actual);
    }

    [SkippableFact]
    public async Task List_endpoint_top_level_shapes_are_pinned()
    {
        RequireDocker();
        Environment.SetEnvironmentVariable("ADMIN_EMAILS", "shapeadmin@shape.test");
        await using var db = _pg.NewDbContext();
        // Seed one check + one run + an admin session so per-check + admin-gated lists return their 200 shape.
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO checks (name, kind, target_url) VALUES ('shape-check', 'http', 'https://x.example');");
        var checkId = await db.Checks.Where(c => c.Name == "shape-check").Select(c => c.Id).FirstAsync();
        await db.Database.ExecuteSqlRawAsync(
            $"INSERT INTO runs (check_id, status, started_at) VALUES ({checkId}, 'pass', now());");
        var runId = await db.Runs.Where(r => r.CheckId == checkId).Select(r => r.Id).FirstAsync();
        const string adminTok = "swt_shape_admin";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO sessions (token_hash, email, expires_at) VALUES ({AuthTokens.Sha256Hex(adminTok)}, 'shapeadmin@shape.test', now() + interval '1 hour')");
        try
        {
            var checks = new ChecksFunctions(db);
            var runs = new RunsFunctions(db);
            var channels = new ChannelsFunctions(db);
            var flows = new FlowsFunctions(db);
            var editors = new EditorsFunctions(db, new AuthPrincipalService(db), new AuditScope(), NullLogger<EditorsFunctions>.Instance);
            var tags = new TagsFunctions(db);
            var incidents = new IncidentsFunctions(db);
            var locations = new LocationsFunctions(db);
            var specs = new SpecsFunctions(db);
            var reconcile = new ReconcileFunctions(db, new FakeRunnerJobTrigger(),
                Microsoft.Extensions.Options.Options.Create(new SynthWatch.Api.Infrastructure.RunnerJobOptions()));
            var routing = new RoutingFunctions(db);
            var sla = new SlaFunctions(db);
            var reports = new ReportsFunctions(db);

            // ── BARE ARRAYS (7) — must stay top-level JSON arrays ──
            AssertBareArray(await checks.ListChecks(Request(), default));                  // GET /checks
            AssertBareArray(await runs.ListRunSteps(Request(), runId, default));           // GET /runs/{id}/steps
            AssertBareArray(await channels.GetChannels(Request(), default));               // GET /channels
            AssertBareArray(await flows.ListFlows(Request(), default));                    // GET /flows
            AssertBareArray(await editors.ListEditors(AuthReq(adminTok), default));        // GET /editors (admin)
            AssertBareArray(await editors.ListAccessRequests(AuthReq(adminTok), default)); // GET /access-requests (admin)
            AssertBareArray(TagsFunctions.GetSuggestedTagKeys(Request()));                 // GET /tags/suggested

            // ── ENVELOPES — pin the exact top-level key set (the wire writes null keys, so all are present) ──
            AssertEnvelope(await checks.ListCheckRuns(Request(), checkId, default), "items", "nextCursor", "pageSize", "latestRunId");
            AssertEnvelope(await incidents.ListIncidents(Request(), default), "items", "nextCursor", "pageSize");
            AssertEnvelope(await checks.ListCheckMetrics(Request(), checkId, default), "items", "page", "pageSize", "total");
            AssertEnvelope(await specs.GetSpecCatalog(Request(), default), "items", "probedAt");
            AssertEnvelope(await reconcile.GetReconcileDrift(Request(), default), "items", "detectedAt");
            AssertEnvelope(await tags.GetTagsInUse(Request(), default), "tags");
            AssertEnvelope(await tags.GetCheckTags(Request(), checkId, default), "tags");
            AssertEnvelope(await locations.GetLocations(Request(), default), "locations");
            AssertEnvelope(await locations.GetCheckLocations(Request(), checkId, default), "locations");
            AssertEnvelope(await checks.GetAvailabilitySeries(Request(), checkId, default), "window", "bucket", "points");
            AssertEnvelope(await sla.GetSla(Request(), default), "window", "fleet", "items");
            AssertEnvelope(await new StatusFunctions(db).GetStatus(Request(), default), "window", "properties", "recentIncidents");
            AssertEnvelope(await reports.GetSloReport(Request(), default), "window", "fleet", "items");
            AssertEnvelope(await reports.GetDeploysReport(Request("?host=shape.example"), default), "host", "window", "deploys");
            AssertEnvelope(await reports.GetMttrReport(Request(), default), "window", "fleet", "items", "classification", "trend");
            AssertEnvelope(await routing.GetRouting(Request(), default), "severity", "perCheck", "tagRules");
            AssertEnvelope(await reports.GetAvailabilityReport(Request(), default), "window", "groupBy", "groups");
            AssertEnvelope(await reports.GetPerformanceReport(Request(), default), "window", "groupBy", "groups");

            // ── ★ SIBLING TRAPS — pin each side so they can't silently converge/diverge (the sharpest drift risk) ──
            // /tags is enveloped {tags}; its sibling /tags/suggested is a BARE string[].
            AssertEnvelope(await tags.GetTagsInUse(Request(), default), "tags");
            AssertBareArray(TagsFunctions.GetSuggestedTagKeys(Request()));
            // /checks/{id}/runs is the {items,…} cursor envelope; its sibling /runs/{id}/steps is a BARE array.
            AssertEnvelope(await checks.ListCheckRuns(Request(), checkId, default), "items", "nextCursor", "pageSize", "latestRunId");
            AssertBareArray(await runs.ListRunSteps(Request(), runId, default));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM checks WHERE name = 'shape-check';"); // cascades runs
            await db.Database.ExecuteSqlRawAsync("DELETE FROM sessions WHERE email = 'shapeadmin@shape.test';");
            Environment.SetEnvironmentVariable("ADMIN_EMAILS", null);
        }
    }

    // ── Reports P6: the verdict-taxonomy breakdown + ALERT PRECISION, with the response SHAPE pinned ──
    [SkippableFact]
    public async Task Incident_breakdown_reports_precision_taxonomy_and_unclassified_with_a_pinned_shape()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        // A dedicated check + 4 incidents opened now: 2 real-outage, 1 selector-drift, 1 UNCLASSIFIED (no rca).
        await db.Database.ExecuteSqlRawAsync("""
            DO $$ DECLARE cid bigint; BEGIN
              INSERT INTO checks (name,kind,target_url) VALUES ('p6-brk','http','https://p6.example') RETURNING id INTO cid;
              INSERT INTO incidents (check_id,status,severity,opened_at,rca) VALUES
                (cid,'resolved','critical',now(), jsonb_build_object('classification','real-outage')),
                (cid,'resolved','critical',now(), jsonb_build_object('classification','real-outage')),
                (cid,'resolved','warning', now(), jsonb_build_object('classification','selector-drift')),
                (cid,'open',    'critical',now(), NULL);
            END $$;
            """);
        try
        {
            var fn = new ReportsFunctions(db);
            var dto = Assert.IsType<IncidentBreakdownDto>(
                Assert.IsType<OkObjectResult>(await fn.GetIncidentBreakdown(Request("?window=7d"), default)).Value!);

            // The breakdown (this check's 4 incidents are a subset of the window — assert via the buckets we seeded).
            long BucketOf(string c) => dto.Buckets.Where(b => b.Classification == c).Sum(b => b.Count);
            Assert.True(BucketOf("real-outage") >= 2);
            Assert.True(BucketOf("selector-drift") >= 1);
            Assert.True(dto.Unclassified >= 1);                         // the null-rca incident is its own bucket, never dropped
            Assert.Contains(dto.Buckets, b => b.Classification == "unclassified");
            Assert.True(dto.RealOutages >= 2 && dto.Classified >= 3);
            Assert.NotNull(dto.Precision);                              // classified > 0 → a real number, not null
            Assert.InRange(dto.Precision!.Value, 0m, 1m);
            Assert.Equal(dto.Total, dto.Classified + dto.Unclassified); // accounting closes

            // ★ PIN THE WIRE SHAPE (the #123 discipline): exact top-level + bucket key sets.
            var root = JsonDocument.Parse(JsonSerializer.Serialize((object)dto,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement;
            Assert.Equal(
                new[] { "buckets", "classified", "precision", "realOutages", "total", "unclassified", "window" },
                root.EnumerateObject().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToArray());
            Assert.Equal(
                new[] { "classification", "count", "pctOfTotal" },
                root.GetProperty("buckets")[0].EnumerateObject().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToArray());
            Assert.Equal("7d", root.GetProperty("window").GetString());
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM checks WHERE name = 'p6-brk';"); // cascades incidents
        }
    }

    [SkippableFact]
    public async Task Incident_breakdown_honest_accounting_invariants_and_bad_window()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        var fn = new ReportsFunctions(db);
        var dto = Assert.IsType<IncidentBreakdownDto>(
            Assert.IsType<OkObjectResult>(await fn.GetIncidentBreakdown(Request("?window=90d"), default)).Value!);

        // Invariants that hold for ANY data (so they're deterministic in the shared DB):
        Assert.Equal(dto.Total, dto.Classified + dto.Unclassified);     // nothing dropped
        Assert.Equal(dto.Total, dto.Buckets.Sum(b => b.Count));         // buckets account for every incident
        Assert.Equal(dto.Classified == 0, dto.Precision is null);       // ★ honest empty: null IFF nothing classified — never a fake 0%
        Assert.All(dto.Buckets, b => Assert.InRange(b.PctOfTotal, 0m, 1m));

        // A bad window is a 400, not a silent default scan.
        Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(
            await fn.GetIncidentBreakdown(Request("?window=bogus"), default)).StatusCode);
    }

    // ★ GET /status — the internal/stakeholder status page. Proves: property rollup (down/degraded/up from the
    // area tag); current-state DISTINCT from uptime% (a building-baseline property is "up" NOW yet has a null
    // uptime); a critical fail rolls its property to DOWN; untagged/internal checks are EXCLUDED (no leak);
    // recent incidents are property-scoped; and pins the (leak-free) wire shape.
    [SkippableFact]
    public async Task Status_page_rolls_properties_up_hides_internals_and_separates_state_from_uptime()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        await db.Database.ExecuteSqlRawAsync("""
            DO $$
            DECLARE cc bigint; cp bigint; cw bigint; cu bigint; cthin bigint; cint bigint; i int;
            BEGIN
              INSERT INTO checks (name, kind, target_url, severity) VALUES ('st-crit-fail','http','https://crit.ex','critical') RETURNING id INTO cc;
              INSERT INTO checks (name, kind, target_url, severity) VALUES ('st-crit-ok','http','https://critok.ex','critical') RETURNING id INTO cp;
              INSERT INTO checks (name, kind, target_url, severity) VALUES ('st-warn','http','https://warn.ex','warning') RETURNING id INTO cw;
              INSERT INTO checks (name, kind, target_url, severity) VALUES ('st-up','http','https://up.ex','critical') RETURNING id INTO cu;
              INSERT INTO checks (name, kind, target_url, severity) VALUES ('st-thin','http','https://thin.ex','critical') RETURNING id INTO cthin;
              INSERT INTO checks (name, kind, target_url, severity) VALUES ('st-internal','http','https://internal.ex','critical') RETURNING id INTO cint;
              INSERT INTO check_tags (check_id, key, value) VALUES
                (cc,'area','st-crit'), (cp,'area','st-crit'), (cw,'area','st-warn'), (cu,'area','st-up'), (cthin,'area','st-thin');
              -- cint (internal) is intentionally UNTAGGED → must never surface.
              FOR i IN 1..25 LOOP INSERT INTO runs (check_id,status,started_at) VALUES (cc,'pass', now()-((i+10)||' minutes')::interval); END LOOP;
              INSERT INTO runs (check_id,status,started_at) VALUES (cc,'fail', now());   -- latest → down
              FOR i IN 1..25 LOOP INSERT INTO runs (check_id,status,started_at) VALUES (cp,'pass', now()-(i||' minutes')::interval); END LOOP;
              FOR i IN 1..24 LOOP INSERT INTO runs (check_id,status,started_at) VALUES (cw,'pass', now()-((i+10)||' minutes')::interval); END LOOP;
              INSERT INTO runs (check_id,status,started_at) VALUES (cw,'warn', now());   -- latest → degraded
              FOR i IN 1..25 LOOP INSERT INTO runs (check_id,status,started_at) VALUES (cu,'pass', now()-(i||' minutes')::interval); END LOOP;
              FOR i IN 1..5  LOOP INSERT INTO runs (check_id,status,started_at) VALUES (cthin,'pass', now()-(i||' minutes')::interval); END LOOP; -- <20 → building
              INSERT INTO runs (check_id,status,started_at) VALUES (cint,'fail', now()); -- internal fail (excluded)
              INSERT INTO incidents (check_id,status,severity,opened_at,summary) VALUES (cc,'open','critical', now()-interval '10 minutes', 'st-crit homepage down');
            END $$;
            """);
        try
        {
            var dto = Assert.IsType<StatusPageDto>(Assert.IsType<OkObjectResult>(
                await new StatusFunctions(db).GetStatus(Request(), default)).Value!);

            var props = dto.Properties.ToDictionary(p => p.Name);
            // internal/untagged check is NOT a property (no leak)
            Assert.DoesNotContain(dto.Properties, p => p.Name.Contains("internal"));
            Assert.Equal(new[] { "st-crit", "st-thin", "st-up", "st-warn" }, props.Keys.OrderBy(k => k).ToArray());

            // ★ a critical fail rolls the property to DOWN
            Assert.Equal("down", props["st-crit"].State);
            Assert.Equal(2, props["st-crit"].CheckCount);
            Assert.True(props["st-crit"].DownCount >= 1);
            Assert.Equal("degraded", props["st-warn"].State);   // a warn latest run
            Assert.Equal("up", props["st-up"].State);

            // ★ current-state DISTINCT from uptime: st-thin is "up" NOW but its uptime is null (building baseline)
            Assert.Equal("up", props["st-thin"].State);
            Assert.True(props["st-thin"].BuildingBaseline);
            Assert.Null(props["st-thin"].UptimePct);
            // a well-populated property carries a real % (not null, not building)
            Assert.False(props["st-up"].BuildingBaseline);
            Assert.NotNull(props["st-up"].UptimePct);

            // ordering: down → degraded → up (attention first)
            Assert.Equal("st-crit", dto.Properties[0].Name);
            Assert.Equal("down", dto.Properties[0].State);

            // recent incidents are property-scoped (title = the summary; no id/url)
            var inc = Assert.Single(dto.RecentIncidents, r => r.Property == "st-crit");
            Assert.Equal("st-crit homepage down", inc.Title);
            Assert.Equal("open", inc.Status);
            Assert.Equal("critical", inc.Severity);

            // ★ leak-free wire shape (#123): only property-level keys — NO checkId/targetUrl/url anywhere
            var web = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
            var root = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(dto, web)).RootElement;
            Assert.Equal(new[] { "properties", "recentIncidents", "window" },
                root.EnumerateObject().Select(p => p.Name).OrderBy(k => k).ToArray());
            Assert.Equal(new[] { "buildingBaseline", "checkCount", "degradedCount", "downCount", "name", "state", "upCount", "uptimePct" },
                root.GetProperty("properties")[0].EnumerateObject().Select(p => p.Name).OrderBy(k => k).ToArray());
            Assert.Equal(new[] { "openedAt", "property", "resolvedAt", "severity", "status", "title" },
                root.GetProperty("recentIncidents")[0].EnumerateObject().Select(p => p.Name).OrderBy(k => k).ToArray());
            var blob = System.Text.Json.JsonSerializer.Serialize(dto, web);
            Assert.DoesNotContain("targetUrl", blob);
            Assert.DoesNotContain("checkId", blob);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM incidents WHERE check_id IN (SELECT id FROM checks WHERE name LIKE 'st-%'); " +
                "DELETE FROM check_tags WHERE check_id IN (SELECT id FROM checks WHERE name LIKE 'st-%'); " +
                "DELETE FROM checks WHERE name LIKE 'st-%';");
        }
    }

    // ★ GET /reports/deploys — the deploy-marker overlay source (deploy-markers v1). Proves: host-scoped;
    // a git-sha marker exposes its sha, a non-sha (etag) marker exposes NULL sha + is_sha=false (honest);
    // window filtering; host required (400); and the wire shape.
    [SkippableFact]
    public async Task Deploys_report_is_host_scoped_and_labels_sha_vs_non_sha_honestly()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO deploys (target_host, sha, fingerprint, is_sha, source, deployed_at) VALUES
              ('www.meals2go.com', 'a1b2c3d4e5f6a7b8c9d0a1b2c3d4e5f6a7b8c9d0', 'a1b2c3d4e5f6a7b8c9d0a1b2c3d4e5f6a7b8c9d0', true,  'sentry-release', now() - interval '5 days'),
              ('www.meals2go.com', NULL,                                       '93718211',                                 false, 'etag',           now() - interval '1 day'),
              ('other.example',    NULL,                                       'zzzz',                                     false, 'etag',           now() - interval '1 day');
            """);
        try
        {
            var reports = new ReportsFunctions(db);
            var dto = Assert.IsType<DeploysReportDto>(Assert.IsType<OkObjectResult>(
                await reports.GetDeploysReport(Request("?host=www.meals2go.com&window=30d"), default)).Value!);

            Assert.Equal("www.meals2go.com", dto.Host);
            Assert.Equal(2, dto.Deploys.Count);                              // host-scoped: other.example excluded
            // ordered by deployed_at → the sentry-release (5d ago) first, the etag (1d ago) second
            Assert.True(dto.Deploys[0].IsSha);
            Assert.Equal("a1b2c3d4e5f6a7b8c9d0a1b2c3d4e5f6a7b8c9d0", dto.Deploys[0].Sha);
            Assert.Equal("sentry-release", dto.Deploys[0].Source);
            // ★ a non-sha (etag) marker → NULL sha + is_sha=false (the UI labels it "no commit id", never a fake)
            Assert.False(dto.Deploys[1].IsSha);
            Assert.Null(dto.Deploys[1].Sha);
            Assert.Equal("etag", dto.Deploys[1].Source);

            // host is required
            Assert.Equal(400, StatusOf(await reports.GetDeploysReport(Request("?window=30d"), default)));
            // bad window → 400
            Assert.Equal(400, StatusOf(await reports.GetDeploysReport(Request("?host=x&window=1y"), default)));
            // an unknown host → honest empty (not a 500)
            var empty = Assert.IsType<DeploysReportDto>(Assert.IsType<OkObjectResult>(
                await reports.GetDeploysReport(Request("?host=nobody.example"), default)).Value!);
            Assert.Empty(empty.Deploys);

            // ★ wire-shape pin (#123)
            var web = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
            var root = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(dto, web)).RootElement;
            Assert.Equal(new[] { "deploys", "host", "window" },
                root.EnumerateObject().Select(p => p.Name).OrderBy(k => k).ToArray());
            Assert.Equal(new[] { "deployedAt", "isSha", "sha", "source" },
                root.GetProperty("deploys")[0].EnumerateObject().Select(p => p.Name).OrderBy(k => k).ToArray());
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM deploys WHERE target_host IN ('www.meals2go.com','other.example');");
        }
    }

    // Reconcile-apply Phase 1: the APPROVE endpoint, against the real schema (the test #127 was missing).
    [SkippableFact]
    public async Task Approve_pending_plan_transitions_to_approved_and_writes_decision()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO reconcile_apply_plan (source_key, drift_type, status, plan) VALUES ('t-approve','new','pending', jsonb_build_object('summary','x'))");
        try
        {
            var fn = new ReconcileFunctions(db, new FakeRunnerJobTrigger(),
                Microsoft.Extensions.Options.Options.Create(new SynthWatch.Api.Infrastructure.RunnerJobOptions()));
            // JsonRequest → a bare HttpContext with NO principal in Items: the live who=null path #127 never ran.
            var res = await fn.ApproveReconcilePlan(JsonRequest(new { sourceKey = "t-approve", driftType = "new" }), default);
            Assert.IsType<OkObjectResult>(res);
            var status = (string?)await ScalarRaw(db, "SELECT status FROM reconcile_apply_plan WHERE source_key='t-approve'");
            Assert.Equal("approved", status);
            Assert.Equal(true, (bool?)await ScalarRaw(db, "SELECT decided_at IS NOT NULL FROM reconcile_apply_plan WHERE source_key='t-approve'"));
            // who=null here (JsonRequest has no principal in Items) → decided_by is null, which is allowed (nullable).
            Assert.Null((string?)await ScalarRaw(db, "SELECT decided_by FROM reconcile_apply_plan WHERE source_key='t-approve'"));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM reconcile_apply_plan WHERE source_key='t-approve'");
        }
    }

    // The existing fail-safe guards must hold: a BLOCKED plan (redaction strip) can never be approved, and a
    // non-pending plan is a 409 — not a silent re-decision.
    [SkippableFact]
    public async Task Approve_cannot_approve_a_blocked_or_already_decided_plan()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO reconcile_apply_plan (source_key, drift_type, status, plan) VALUES " +
            "('t-blocked','redaction_mismatch','blocked', jsonb_build_object('blockedReason','strip')), " +
            "('t-done','new','approved', jsonb_build_object('summary','x'))");
        try
        {
            var fn = new ReconcileFunctions(db, new FakeRunnerJobTrigger(),
                Microsoft.Extensions.Options.Options.Create(new SynthWatch.Api.Infrastructure.RunnerJobOptions()));
            // ★ blocked → 409 (the B10 fail-safe: a redaction strip can never be approved into action).
            Assert.Equal(409, StatusOf(await fn.ApproveReconcilePlan(
                JsonRequest(new { sourceKey = "t-blocked", driftType = "redaction_mismatch" }), default)));
            // already 'approved' → 409 (not pending).
            Assert.Equal(409, StatusOf(await fn.ApproveReconcilePlan(
                JsonRequest(new { sourceKey = "t-done", driftType = "new" }), default)));
            // unchanged.
            Assert.Equal("blocked", (string?)await ScalarRaw(db, "SELECT status FROM reconcile_apply_plan WHERE source_key='t-blocked'"));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM reconcile_apply_plan WHERE source_key IN ('t-blocked','t-done')");
        }
    }

    // ★ The approve/apply asymmetry fix: only drift_types apply can EXECUTE may be APPROVED. Approving a
    // 'changed'/'missing' plan would move it to 'approved' where ApplyReconcilePlans (WHERE drift_type='new')
    // silently ignores it forever — an indefinite no-op. The gate is approve-only: REJECT stays open for all
    // (you can always reject what can't be applied). Also delivers the absent reject integration coverage.
    [SkippableFact]
    public async Task Approve_gates_non_executable_drift_types_but_reject_allows_them()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO reconcile_apply_plan (source_key, drift_type, status, plan) VALUES " +
            "('t-new','new','pending', jsonb_build_object('summary','x')), " +
            "('t-changed','changed','pending', jsonb_build_object('summary','x')), " +
            "('t-missing','missing','pending', jsonb_build_object('summary','x'))");
        try
        {
            var fn = new ReconcileFunctions(db, new FakeRunnerJobTrigger(),
                Microsoft.Extensions.Options.Options.Create(new SynthWatch.Api.Infrastructure.RunnerJobOptions()));

            // approve 'new' + 'missing' (both EXECUTABLE now) → 200, transitions to approved.
            Assert.IsType<OkObjectResult>(await fn.ApproveReconcilePlan(JsonRequest(new { sourceKey = "t-new", driftType = "new" }), default));
            Assert.Equal("approved", (string?)await ScalarRaw(db, "SELECT status FROM reconcile_apply_plan WHERE source_key='t-new'"));
            // ★ 'missing' is now executable (this PR) → approve no longer 409s; it moves to approved.
            Assert.IsType<OkObjectResult>(await fn.ApproveReconcilePlan(JsonRequest(new { sourceKey = "t-missing", driftType = "missing" }), default));
            Assert.Equal("approved", (string?)await ScalarRaw(db, "SELECT status FROM reconcile_apply_plan WHERE source_key='t-missing'"));

            // ★ approve 'changed' (apply STILL can't execute it — PR2) → 409, and the plan STAYS pending.
            Assert.Equal(409, StatusOf(await fn.ApproveReconcilePlan(JsonRequest(new { sourceKey = "t-changed", driftType = "changed" }), default)));
            Assert.Equal("pending", (string?)await ScalarRaw(db, "SELECT status FROM reconcile_apply_plan WHERE source_key='t-changed'"));

            // ★ reject is ALWAYS allowed — you can reject a plan apply can't execute.
            Assert.IsType<OkObjectResult>(await fn.RejectReconcilePlan(JsonRequest(new { sourceKey = "t-changed", driftType = "changed" }), default));
            Assert.Equal("rejected", (string?)await ScalarRaw(db, "SELECT status FROM reconcile_apply_plan WHERE source_key='t-changed'"));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM reconcile_apply_plan WHERE source_key IN ('t-new','t-changed','t-missing')");
        }
    }

    // ═══ The APPLY EXECUTOR — the riskiest write in the repo (live config, sensitive-inline, txn/rollback) and
    // the one path with no e2e coverage (API analysis #3 / #137 follow-up). ★ These run as the postgres
    // SUPERUSER (Testcontainers default), so they cover MATERIALIZATION CORRECTNESS, not grants — a missing
    // GRANT would NOT fail here (that's pg-grant-coverage CI's job, #133). The seeded plan jsonb matches the
    // executor's read contract (plan.statements[0].values = [src,name,kind,url,flow,sensitive,redact,interval,
    // enabled(ignored),specPath], ReconcileFunctions.cs:188-216).

    // Build a materialize-plan jsonb matching what computeApplyPlan emits + what the executor reads (v[0..9]).
    private static string MaterializePlan(string src, string name, string kind, string url, string flow,
        bool sensitive, string redact, int interval, string? specPath)
    {
        var values = new object?[] { src, name, kind, url, flow, sensitive, redact, interval, false /*enabled v8 — executor ignores it, hard-codes false*/, specPath };
        var plan = new { statements = new[] { new { text = "INSERT INTO checks …", values, regions = new[] { "eastus2" } } } };
        return System.Text.Json.JsonSerializer.Serialize(plan);
    }

    private static async Task CleanupApply(SynthWatch.Api.Data.SynthWatchDbContext db, string src, string[] locs)
    {
        await db.Database.ExecuteSqlRawAsync(
            $"DELETE FROM check_locations WHERE check_id IN (SELECT id FROM checks WHERE source_key = '{src}'); " +
            $"DELETE FROM checks WHERE source_key = '{src}'; " +
            $"DELETE FROM reconcile_apply_plan WHERE source_key = '{src}';");
        foreach (var l in locs)
            await db.Database.ExecuteSqlRawAsync($"DELETE FROM locations WHERE name = '{l}'");
    }

    private static ReconcileFunctions ApplyFn(SynthWatch.Api.Data.SynthWatchDbContext db) =>
        new(db, new FakeRunnerJobTrigger(),
            Microsoft.Extensions.Options.Options.Create(new SynthWatch.Api.Infrastructure.RunnerJobOptions()));

    // ★ Happy path: an approved 'new' browser plan materializes a runnable check — spec_path set (#131/#156),
    // enabled=FALSE (the Phase-1 invariant), locations seeded for every enabled region, plan approved→applied.
    [SkippableFact]
    public async Task Apply_materializes_an_approved_new_plan_disabled_with_spec_path_and_locations()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO locations (name, enabled) VALUES ('t-eastus2', true), ('t-centralus', true), ('t-westus', true) " +
            "ON CONFLICT (name) DO UPDATE SET enabled = true");
        var plan = MaterializePlan("t-apply-browser", "Apply Browser", "browser", "https://apply.test", "apply-flow",
            sensitive: false, redact: "[]", interval: 300, specPath: "monitors/apply-test.spec.ts");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO reconcile_apply_plan (source_key, drift_type, status, plan) VALUES ('t-apply-browser','new','approved', {plan}::jsonb)");
        try
        {
            var res = Assert.IsType<OkObjectResult>(await ApplyFn(db).ApplyReconcilePlans(JsonRequest(new { }), default));
            var dto = Assert.IsType<ReconcileApplyResultDto>(res.Value);
            Assert.Contains("t-apply-browser", dto.Applied);
            Assert.DoesNotContain("t-apply-browser", dto.Failed);

            var c = await db.Checks.AsNoTracking().Where(x => x.SourceKey == "t-apply-browser").FirstAsync();
            Assert.Equal("Apply Browser", c.Name);
            Assert.Equal("browser", c.Kind);
            Assert.Equal("https://apply.test", c.TargetUrl);
            Assert.Equal("apply-flow", c.FlowName);
            Assert.Equal("monitors/apply-test.spec.ts", c.SpecPath);  // ★ #131/#156 — spec_path materialized (runnable)
            Assert.False(c.Sensitive);
            Assert.False(c.Enabled);                                   // ★ INVARIANT: enabled=FALSE on materialize

            // ★ check_locations seeded for ALL enabled regions (executor: SELECT name FROM locations WHERE enabled).
            var seeded = (long)(await ScalarRaw(db, $"SELECT count(*) FROM check_locations WHERE check_id = {c.Id}"))!;
            var enabled = (long)(await ScalarRaw(db, "SELECT count(*) FROM locations WHERE enabled"))!;
            Assert.Equal(enabled, seeded);
            Assert.Equal(true, (bool?)await ScalarRaw(db, $"SELECT EXISTS(SELECT 1 FROM check_locations WHERE check_id = {c.Id} AND location = 't-eastus2')"));

            // ★ plan flipped approved → applied, applied_at set.
            Assert.Equal("applied", (string?)await ScalarRaw(db, "SELECT status FROM reconcile_apply_plan WHERE source_key='t-apply-browser'"));
            Assert.Equal(true, (bool?)await ScalarRaw(db, "SELECT applied_at IS NOT NULL FROM reconcile_apply_plan WHERE source_key='t-apply-browser'"));
        }
        finally { await CleanupApply(db, "t-apply-browser", new[] { "t-eastus2", "t-centralus", "t-westus" }); }
    }

    // ★ Sensitive inline: an approved sensitive 'new' plan → sensitive=true straight from the INSERT (no
    // non-sensitive window), still enabled=FALSE.
    [SkippableFact]
    public async Task Apply_sets_sensitive_inline_and_enabled_false()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        var plan = MaterializePlan("t-apply-sensitive", "Apply Sensitive", "http", "https://sensitive.test", "",
            sensitive: true, redact: "[\"authorization\"]", interval: 300, specPath: null);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO reconcile_apply_plan (source_key, drift_type, status, plan) VALUES ('t-apply-sensitive','new','approved', {plan}::jsonb)");
        try
        {
            Assert.IsType<OkObjectResult>(await ApplyFn(db).ApplyReconcilePlans(JsonRequest(new { }), default));
            var c = await db.Checks.AsNoTracking().Where(x => x.SourceKey == "t-apply-sensitive").FirstAsync();
            Assert.True(c.Sensitive);   // ★ sensitive=true atomically from the INSERT — no window where it's non-sensitive
            Assert.False(c.Enabled);    // ★ still disabled on materialize
            Assert.Equal("http", c.Kind);
        }
        finally { await CleanupApply(db, "t-apply-sensitive", System.Array.Empty<string>()); }
    }

    // ★ Rollback: a materialize that fails mid-txn (interval_seconds=0 violates checks_interval_seconds_check)
    // → NO check row, NO locations, the plan STAYS 'approved' (re-appliable — never half-applied).
    [SkippableFact]
    public async Task Apply_rolls_back_a_failed_materialize_leaving_the_plan_approved()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        var plan = MaterializePlan("t-apply-fail", "Apply Fail", "http", "https://fail.test", "",
            sensitive: false, redact: "[]", interval: 0 /*→ violates checks_interval_seconds_check (>0)*/, specPath: null);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO reconcile_apply_plan (source_key, drift_type, status, plan) VALUES ('t-apply-fail','new','approved', {plan}::jsonb)");
        try
        {
            var dto = Assert.IsType<ReconcileApplyResultDto>(
                Assert.IsType<OkObjectResult>(await ApplyFn(db).ApplyReconcilePlans(JsonRequest(new { }), default)).Value);
            Assert.Contains("t-apply-fail", dto.Failed);        // reported failed, not applied
            Assert.DoesNotContain("t-apply-fail", dto.Applied);

            // ★ ROLLBACK: nothing materialized, plan stays approved for retry.
            Assert.False(await db.Checks.AsNoTracking().AnyAsync(x => x.SourceKey == "t-apply-fail"));
            Assert.Equal("approved", (string?)await ScalarRaw(db, "SELECT status FROM reconcile_apply_plan WHERE source_key='t-apply-fail'"));
            Assert.Equal(true, (bool?)await ScalarRaw(db, "SELECT applied_at IS NULL FROM reconcile_apply_plan WHERE source_key='t-apply-fail'"));
        }
        finally { await CleanupApply(db, "t-apply-fail", System.Array.Empty<string>()); }
    }

    // The runner-emitted 'missing' plan: a single soft-disable UPDATE by source_key (text overridable for the
    // guard test). Matches computeApplyPlan's missing branch (reconcile.ts): enabled=false WHERE source_key=$1.
    private static string MissingPlan(string src, string? text = null)
    {
        var stmt = new { text = text ?? "UPDATE checks SET enabled = false WHERE source_key = $1", values = new object?[] { src } };
        return System.Text.Json.JsonSerializer.Serialize(new { statements = new[] { stmt } });
    }

    // ★ 'missing' executor — a Git-deleted spec SOFT-DISABLES the check (enabled=false), NEVER deletes it. This
    // test proves the RIGHT thing: it asserts the row + its runs + its incident STILL EXIST (a hard-delete
    // would pass an enabled-only check but FAIL these), and the plan flips approved→applied.
    [SkippableFact]
    public async Task Apply_soft_disables_a_missing_check_and_preserves_its_history()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        await db.Database.ExecuteSqlRawAsync("""
            DO $$
            DECLARE cid bigint;
            BEGIN
              INSERT INTO checks (name, kind, target_url, source_key, enabled) VALUES ('miss-me','http','https://miss.test','t-miss', true) RETURNING id INTO cid;
              INSERT INTO runs (check_id, status, started_at) VALUES (cid,'pass', now()-interval '1 hour');
              INSERT INTO incidents (check_id, status, severity, opened_at, summary) VALUES (cid,'resolved','critical', now()-interval '2 hours', 'past outage');
            END $$;
            """);
        var plan = MissingPlan("t-miss");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO reconcile_apply_plan (source_key, drift_type, status, plan) VALUES ('t-miss','missing','approved', {plan}::jsonb)");
        try
        {
            var dto = Assert.IsType<ReconcileApplyResultDto>(
                Assert.IsType<OkObjectResult>(await ApplyFn(db).ApplyReconcilePlans(JsonRequest(new { }), default)).Value);
            Assert.Contains("t-miss", dto.Applied);
            Assert.DoesNotContain("t-miss", dto.Failed);

            var cid = (long)(await ScalarRaw(db, "SELECT id FROM checks WHERE source_key='t-miss'"))!;
            // ★ soft-disabled
            Assert.Equal(false, (bool?)await ScalarRaw(db, "SELECT enabled FROM checks WHERE source_key='t-miss'"));
            // ★★ MUST-GO-RED: the row + its history STILL EXIST (a hard-delete would zero these out).
            Assert.True(await db.Checks.AsNoTracking().AnyAsync(c => c.SourceKey == "t-miss"));                        // row preserved
            Assert.Equal(1L, (long)(await ScalarRaw(db, $"SELECT count(*) FROM runs WHERE check_id={cid}"))!);        // runs preserved
            Assert.Equal(1L, (long)(await ScalarRaw(db, $"SELECT count(*) FROM incidents WHERE check_id={cid}"))!);   // incident preserved
            // ★ plan → applied
            Assert.Equal("applied", (string?)await ScalarRaw(db, "SELECT status FROM reconcile_apply_plan WHERE source_key='t-miss'"));
            Assert.Equal(true, (bool?)await ScalarRaw(db, "SELECT applied_at IS NOT NULL FROM reconcile_apply_plan WHERE source_key='t-miss'"));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM incidents WHERE check_id IN (SELECT id FROM checks WHERE source_key='t-miss'); " +
                "DELETE FROM runs WHERE check_id IN (SELECT id FROM checks WHERE source_key='t-miss'); " +
                "DELETE FROM reconcile_apply_plan WHERE source_key='t-miss'; " +
                "DELETE FROM checks WHERE source_key='t-miss';");
        }
    }

    // ★ Rollback + the guard: a 'missing' plan whose statement is NOT the soft-disable (here a DELETE) is
    // REFUSED — the executor never runs it → rollback → the check is untouched (NOT deleted) + the plan stays
    // 'approved' (re-appliable). Proves the executor can't be tricked into a hard-delete via the plan text.
    [SkippableFact]
    public async Task Apply_missing_refuses_a_non_soft_disable_statement_and_rolls_back()
    {
        RequireDocker();
        await using var db = _pg.NewDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO checks (name, kind, target_url, source_key, enabled) VALUES ('dont-delete-me','http','https://x.test','t-miss-guard', true)");
        var badPlan = MissingPlan("t-miss-guard", text: "DELETE FROM checks WHERE source_key = $1"); // hostile: a delete
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO reconcile_apply_plan (source_key, drift_type, status, plan) VALUES ('t-miss-guard','missing','approved', {badPlan}::jsonb)");
        try
        {
            var dto = Assert.IsType<ReconcileApplyResultDto>(
                Assert.IsType<OkObjectResult>(await ApplyFn(db).ApplyReconcilePlans(JsonRequest(new { }), default)).Value);
            Assert.Contains("t-miss-guard", dto.Failed);            // refused → rolled back
            Assert.DoesNotContain("t-miss-guard", dto.Applied);

            // ★★ the check was NOT deleted (guard refused the DELETE), stays enabled; the plan stays approved.
            Assert.True(await db.Checks.AsNoTracking().AnyAsync(c => c.SourceKey == "t-miss-guard"));
            Assert.Equal(true, (bool?)await ScalarRaw(db, "SELECT enabled FROM checks WHERE source_key='t-miss-guard'"));
            Assert.Equal("approved", (string?)await ScalarRaw(db, "SELECT status FROM reconcile_apply_plan WHERE source_key='t-miss-guard'"));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM reconcile_apply_plan WHERE source_key='t-miss-guard'; DELETE FROM checks WHERE source_key='t-miss-guard';");
        }
    }

    private static async Task<object?> ScalarRaw(SynthWatch.Api.Data.SynthWatchDbContext db, string sql)
    {
        var conn = (Npgsql.NpgsqlConnection)db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) await conn.OpenAsync();
        try { await using var cmd = conn.CreateCommand(); cmd.CommandText = sql; var r = await cmd.ExecuteScalarAsync(); return r is DBNull ? null : r; }
        finally { if (opened) await conn.CloseAsync(); }
    }
}
