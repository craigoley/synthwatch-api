using Microsoft.EntityFrameworkCore;
using Npgsql;
using SynthWatch.Api.Data;
using Testcontainers.PostgreSql;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>
/// Spins up a real postgres:16 (Testcontainers) seeded with the runner-owned schema snapshot
/// (fixtures/schema.sql — includes the sla_availability function/views + JSONB columns) plus
/// deterministic fixtures. Lets the DB-dependent behavior (SLA SQL, parity lateral joins, JSONB
/// round-trips) be tested faithfully. If Docker is unavailable (e.g. local dev), <see cref="Available"/>
/// is false and the integration tests skip rather than error — CI (ubuntu-latest) always has Docker.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    public bool Available { get; private set; }
    public string SkipReason { get; private set; } = "";
    private string _connectionString = "";

    /// <summary>The live container's connection string — for tests that need an NpgsqlDataSource (e.g. the
    /// isolated-context audit write). Only valid once <see cref="Available"/> is true.</summary>
    public string ConnectionString => _connectionString;

    // Deterministic seed: check 1 is ~10 days old with 25 completed runs in the last 24h (so the
    // 24h SLA window is SUFFICIENT but the 30d window is NOT — only ~33% covered); plus a screenshot
    // run, a no-artifact run, and a resolved incident. IDs are identity — captured via RETURNING.
    private const string SeedSql = """
        DO $$
        DECLARE cid bigint; rid bigint;
        BEGIN
          INSERT INTO checks (name, kind, target_url, created_at)
            VALUES ('seed-http', 'http', 'https://example.com', now() - interval '10 days')
            RETURNING id INTO cid;

          -- Assigned location row — the runner seeds check_locations at check creation, so a real
          -- check always has one. The grid per-location rollup keys on check_locations (not runs
          -- history), so the fixture must mirror prod or the rollup reads empty. Runs below default
          -- to location 'default', matching this row.
          INSERT INTO check_locations (check_id, location) VALUES (cid, 'default');

          -- 25 completed runs in the last 24h: 20 pass, 5 fail (so up=20, completed=25 => 80%).
          FOR i IN 1..25 LOOP
            INSERT INTO runs (check_id, status, started_at, finished_at, duration_ms)
              VALUES (cid, CASE WHEN i <= 20 THEN 'pass' ELSE 'fail' END,
                      now() - (i || ' minutes')::interval, now(), 40 + i);
          END LOOP;

          -- a run WITH a screenshot, and a run WITHOUT any artifact (for the proxy 404 path).
          INSERT INTO runs (check_id, status, started_at, finished_at, duration_ms, screenshot_url)
            VALUES (cid, 'fail', now() - interval '5 minutes', now(), 50,
                    'https://acct.blob.core.windows.net/synthwatch-artifacts/run-seed.png')
            RETURNING id INTO rid;
          INSERT INTO runs (check_id, status, started_at, finished_at, duration_ms)
            VALUES (cid, 'pass', now() - interval '4 minutes', now(), 45);

          -- resolved_at set => NOT counted as open by the metrics SQL (open = resolved_at IS NULL),
          -- so the seed check shows open_incident_count = 0 while /incidents still lists it.
          INSERT INTO incidents (check_id, status, severity, opened_at, resolved_at, consecutive_failures)
            VALUES (cid, 'resolved', 'critical', now() - interval '2 hours', now() - interval '1 hour', 3);
        END $$;
        """;

    public async Task InitializeAsync()
    {
        try
        {
            // Build() AND StartAsync() both probe Docker; treat either failing as "unavailable".
            _container = new PostgreSqlBuilder("postgres:16").Build();
            await _container.StartAsync();
        }
        catch (Exception ex)
        {
            Available = false;
            SkipReason = $"Docker/Testcontainers unavailable: {ex.GetType().Name}";
            return;
        }

        // Container is up: schema/seed errors should SURFACE (not be swallowed as a skip).
        _connectionString = _container.GetConnectionString();
        var schema = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "schema.sql"));
        await using (var conn = new NpgsqlConnection(_connectionString))
        {
            await conn.OpenAsync();
            // pg_dump emits the sla_availability function before the tables its body references; with
            // body validation on, CREATE FUNCTION would fail ("relation checks does not exist"). Defer
            // body checks for the schema batch (this is what pg_dump's own SET preamble does).
            await using (var schemaCommand = new NpgsqlCommand("SET check_function_bodies = false;\n" + schema, conn))
                await schemaCommand.ExecuteNonQueryAsync();
            await using (var seedCommand = new NpgsqlCommand(SeedSql, conn))
                await seedCommand.ExecuteNonQueryAsync();
        }
        Available = true;
    }

    public SynthWatchDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<SynthWatchDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        return new SynthWatchDbContext(options);
    }

    /// <summary>A DbContext pointed at an unreachable host — for the health DB-down path.</summary>
    public SynthWatchDbContext NewBrokenDbContext()
    {
        var options = new DbContextOptionsBuilder<SynthWatchDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=nope;Username=x;Password=y;Timeout=2;Command Timeout=2")
            .Options;
        return new SynthWatchDbContext(options);
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }
}

[CollectionDefinition("postgres")]
public class PostgresCollection : ICollectionFixture<PostgresFixture> { }
