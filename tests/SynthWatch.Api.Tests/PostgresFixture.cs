using Microsoft.EntityFrameworkCore;
using Npgsql;
using SynthWatch.Api.Data;
using Testcontainers.PostgreSql;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>
/// Provides a REAL postgres:16 seeded with the runner-owned schema snapshot (fixtures/schema.sql —
/// includes the sla_availability function/views + JSONB columns) plus deterministic fixtures, so the
/// DB-dependent behavior (SLA SQL, parity lateral joins, JSONB round-trips) is tested faithfully.
///
/// ★ TWO WAYS TO GET THAT POSTGRES, in priority order:
///   1. DATABASE_URL / TEST_DATABASE_URL — an ALREADY-RUNNING Postgres (a CI service container, a local
///      `docker run`, a dev server). Preferred, and the one CI exercises.
///   2. Testcontainers — the original path, kept as a fallback for anyone who has Docker and sets nothing.
/// Everything downstream reads <see cref="ConnectionString"/>, so the two paths are interchangeable.
///
/// ★ WHY THIS EXISTS. Testcontainers requires a Docker DAEMON, not just a Postgres. When it is absent the
/// DB-backed tests SKIP — silently, and reported as "N passed, M skipped" as though that were a clean run.
/// That is not hypothetical: four DB-backed tests in the preview-credentials PR were written, reviewed, and
/// pushed having NEVER ONCE EXECUTED; CI was their first run and all four failed on a wrong-exact-type
/// assertion. A test that never runs is worth exactly what a test that cannot fail is worth. Accepting a
/// plain connection string removes the daemon requirement without giving up one bit of fidelity: still a
/// real postgres:16, the same schema.sql, the same seed. No SQLite, no in-memory, no EF InMemory.
///
/// ★ FIDELITY NOTE (unchanged by this): both paths connect as a SUPERUSER with no `synthwatch-api` role, so
/// a missing GRANT is still invisible here — that gap is covered by scripts/check-pg-grant-coverage.mjs.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    public bool Available { get; private set; }
    public string SkipReason { get; private set; } = "";
    private string _connectionString = "";
    /// <summary>True when the DB came from DATABASE_URL/TEST_DATABASE_URL rather than a fresh container.</summary>
    private bool _persistent;

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

    /// <summary>
    /// Resolve a Postgres for this run: an externally-provided one if DATABASE_URL / TEST_DATABASE_URL is
    /// set, otherwise a Testcontainers container. Returns null (with SkipReason set) if neither is possible.
    /// </summary>
    private async Task<string?> ResolveConnectionStringAsync()
    {
        // TEST_DATABASE_URL wins so a developer can point the SUITE somewhere without disturbing a
        // DATABASE_URL their app/tooling is already using.
        var url = Environment.GetEnvironmentVariable("TEST_DATABASE_URL")
                  ?? Environment.GetEnvironmentVariable("DATABASE_URL");
        if (!string.IsNullOrWhiteSpace(url))
        {
            _persistent = true;
            return NormalizeConnectionString(url!);
        }

        try
        {
            // Build() AND StartAsync() both probe Docker; treat either failing as "unavailable".
            _container = new PostgreSqlBuilder("postgres:16").Build();
            await _container.StartAsync();
        }
        catch (Exception ex)
        {
            Available = false;
            SkipReason =
                $"No Postgres: set DATABASE_URL (or TEST_DATABASE_URL) to a postgres:16, or make Docker available " +
                $"for the Testcontainers fallback. Testcontainers failed with {ex.GetType().Name}.";
            return null;
        }
        return _container.GetConnectionString();
    }

    /// <summary>
    /// Accept EITHER a URI-style `postgres://user:pass@host:port/db` (what DATABASE_URL conventionally holds,
    /// and what the runner uses) OR an Npgsql keyword string (`Host=…;Username=…`), and return the keyword
    /// form Npgsql needs. Detecting by scheme rather than guessing keeps a keyword string untouched.
    /// </summary>
    internal static string NormalizeConnectionString(string value)
    {
        var v = value.Trim();
        if (!v.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !v.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return v; // already an Npgsql keyword string

        var uri = new Uri(v);
        var userInfo = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = uri.AbsolutePath.TrimStart('/'),
        };
        if (userInfo.Length > 0 && userInfo[0].Length > 0) builder.Username = Uri.UnescapeDataString(userInfo[0]);
        if (userInfo.Length > 1 && userInfo[1].Length > 0) builder.Password = Uri.UnescapeDataString(userInfo[1]);
        return builder.ConnectionString;
    }

    public async Task InitializeAsync()
    {
        var resolved = await ResolveConnectionStringAsync();
        if (resolved is null) return; // SkipReason already set
        _connectionString = resolved;

        // Postgres is up: schema/seed errors should SURFACE (not be swallowed as a skip).
        var schema = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "schema.sql"));
        await using (var conn = new NpgsqlConnection(_connectionString))
        {
            await conn.OpenAsync();

            // ★ RESET — the PERSISTENT path only. A fresh container is empty by construction; a
            //   DATABASE_URL Postgres is NOT, because it survives the run. Without this, a second run
            //   re-applies SeedSql on top of the first run's rows and inserts a SECOND 'seed-http' check —
            //   at which point IntegrationTests.cs:122's `Assert.Single(checks, c => c.Name == "seed-http")`
            //   fails, along with every other seed-scoped assertion. Dropping and recreating the schema
            //   reproduces the fresh-container guarantee that Testcontainers gave us for free.
            //   ★ This assumes ONE test run at a time against a given DATABASE_URL: two concurrent runs
            //   would DROP SCHEMA out from under each other. Same property the runner lives with, and CI
            //   gives each job its own service container, so nothing shares one.
            if (_persistent)
            {
                await using var reset = new NpgsqlCommand("DROP SCHEMA public CASCADE; CREATE SCHEMA public;", conn);
                await reset.ExecuteNonQueryAsync();
            }

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
