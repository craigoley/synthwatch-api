using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>
/// Pins <see cref="PostgresFixture"/>'s PROVIDED-URL-MUST-BE-REACHABLE contract: when a Postgres URL is
/// supplied, an unreachable Postgres is a HARD FAILURE, never a skip.
///
/// ★ WHY THESE EXIST. The behavior was already correct, but only INCIDENTALLY — nothing wrapped the
/// OpenAsync call, so the exception escaped by default. Verified on the pre-change tree: an unreachable
/// DATABASE_URL gave `Failed: 127, Passed: 0, Skipped: 0` (exit 1). Nothing pinned that, so a single
/// defensive try/catch in InitializeAsync would have silently turned a RED suite into green-with-skips —
/// the exact silent-skip class that once shipped four never-executed DB tests. These tests make that
/// regression impossible to merge: catch-and-skip reds them immediately.
///
/// ★ NO DATABASE REQUIRED, deliberately. They point at a closed port (127.0.0.1:1 → connection refused),
/// so they are NOT in the "postgres" collection and run in every job, including one with no Postgres and
/// no Docker at all. A contract test that itself needed the DB could not police the no-DB failure mode.
/// </summary>
public class PostgresFixtureContractTests
{
    // Port 1 is privileged-and-unbound: connect fails immediately with ECONNREFUSED rather than hanging
    // for the 15s Npgsql default, so these stay fast (the whole file runs in well under a second).
    private const string UnreachableUrl = "postgres://u:p@127.0.0.1:1/nope";

    [Fact]
    public async Task Provided_url_that_is_unreachable_THROWS_rather_than_skipping()
    {
        var fixture = new PostgresFixture(UnreachableUrl);

        // ThrowsAnyAsync, not ThrowsAsync: the contract is "this must fail loudly", not "it must fail with
        // one specific type" — pinning the exact type would just make a better error message a red test.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => fixture.InitializeAsync());

        Assert.Contains("hard failure", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Provided_url_that_is_unreachable_NEVER_takes_the_skip_path()
    {
        var fixture = new PostgresFixture(UnreachableUrl);

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.InitializeAsync());

        // The real regression guard. A catch-and-skip refactor would leave SkipReason populated and
        // Available false WITHOUT throwing — tests would then quietly Skip.IfNot past a broken DB. Assert
        // the skip path was not entered at all: no reason recorded, and never reported as usable.
        Assert.Equal("", fixture.SkipReason);
        Assert.False(fixture.Available);
    }

    [Fact]
    public async Task Unreachable_url_surfaces_the_url_problem_actionably()
    {
        var fixture = new PostgresFixture(UnreachableUrl);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => fixture.InitializeAsync());

        // The message has to tell whoever hit this what to DO, and must preserve the underlying cause —
        // a bare "hard failure" with no inner exception sends people hunting.
        Assert.NotNull(ex.InnerException);
        Assert.Contains("unset it", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("postgres://user:pw@db.example.com:6432/synthwatch", "db.example.com", 6432, "synthwatch")]
    [InlineData("postgresql://u:p@127.0.0.1/postgres", "127.0.0.1", 5432, "postgres")]
    public void Url_form_is_normalized_to_the_keyword_form_Npgsql_needs(
        string url, string host, int port, string database)
    {
        // Guards the seam the contract rides on: if normalization silently mangled the URL, the fixture
        // could connect somewhere unintended instead of failing.
        var keyword = PostgresFixture.NormalizeConnectionString(url);

        Assert.Contains($"Host={host}", keyword);
        Assert.Contains($"Port={port}", keyword);
        Assert.Contains($"Database={database}", keyword);
    }

    [Fact]
    public void An_Npgsql_keyword_string_is_passed_through_untouched()
    {
        const string keyword = "Host=localhost;Port=5432;Database=synthwatch;Username=x;Password=y";

        Assert.Equal(keyword, PostgresFixture.NormalizeConnectionString(keyword));
    }
}
