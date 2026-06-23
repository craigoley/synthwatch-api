using Azure.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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
}
