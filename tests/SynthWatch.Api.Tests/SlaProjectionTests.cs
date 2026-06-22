using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>
/// Pure tests for the SLA insufficient-data + run-weighted fleet logic — subtle, regressed-adjacent
/// behavior (two prod tasks touched it). No DB: feeds synthetic view rows through SlaProjection.
/// </summary>
public class SlaProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    private static SlaAvailabilityRow Row(long id, long completed, long up, decimal pct, TimeSpan window) => new()
    {
        CheckId = id, CheckName = $"c{id}", Kind = "http",
        WindowFrom = Now - window, WindowTo = Now,
        CompletedRuns = completed, UpRuns = up, DownRuns = completed - up, AvailabilityPct = pct
    };

    [Fact]
    public void Sufficient_window_reports_real_percentages()
    {
        var rows = new[] { Row(1, 139, 134, 96.40m, TimeSpan.FromHours(24)) };
        // check existed for the whole 24h window -> sufficient
        var created = new Dictionary<long, DateTimeOffset> { [1] = Now - TimeSpan.FromHours(24) };

        var r = SlaProjection.Build(rows, created);

        Assert.False(r.Items[0].InsufficientData);
        Assert.Equal(96.40m, r.Items[0].AvailabilityPct);
        Assert.False(r.Fleet.InsufficientData);
        Assert.Equal(Math.Round(100m * 134 / 139, 4), r.Fleet.AvailabilityPct); // run-weighted
    }

    [Fact]
    public void Window_longer_than_check_age_is_insufficient_and_nulls_pct()
    {
        // 30d window but the check is only ~21h old -> <3% coverage -> insufficient
        var rows = new[] { Row(1, 45, 8, 17.77m, TimeSpan.FromDays(30)) };
        var created = new Dictionary<long, DateTimeOffset> { [1] = Now - TimeSpan.FromHours(21) };

        var r = SlaProjection.Build(rows, created);

        Assert.True(r.Items[0].InsufficientData);
        Assert.Null(r.Items[0].AvailabilityPct);   // misleading precise % suppressed
        Assert.True(r.Fleet.InsufficientData);
        Assert.Null(r.Fleet.AvailabilityPct);
        Assert.Equal(45, r.Items[0].CompletedRuns); // raw counts still surfaced
    }

    [Fact]
    public void Too_few_completed_runs_is_insufficient_even_with_full_coverage()
    {
        var rows = new[] { Row(1, 5, 5, 100m, TimeSpan.FromHours(24)) }; // full coverage, only 5 runs (<20)
        var created = new Dictionary<long, DateTimeOffset> { [1] = Now - TimeSpan.FromHours(48) };

        var r = SlaProjection.Build(rows, created);
        Assert.True(r.Items[0].InsufficientData);
        Assert.Null(r.Items[0].AvailabilityPct);
    }

    [Fact]
    public void Fleet_is_run_weighted_across_checks()
    {
        var rows = new[]
        {
            Row(1, 139, 134, 96.40m, TimeSpan.FromHours(24)),
            Row(2, 45, 8, 17.78m, TimeSpan.FromHours(24)),
        };
        var created = new Dictionary<long, DateTimeOffset>
        {
            [1] = Now - TimeSpan.FromHours(24),
            [2] = Now - TimeSpan.FromHours(24),
        };

        var r = SlaProjection.Build(rows, created);

        // run-weighted (134+8)/(139+45), NOT the naive avg of 96.40 & 17.78
        Assert.Equal(Math.Round(100m * (134 + 8) / (139 + 45), 4), r.Fleet.AvailabilityPct);
        Assert.NotEqual(Math.Round((96.40m + 17.78m) / 2, 4), r.Fleet.AvailabilityPct);
        Assert.False(r.Fleet.InsufficientData);
    }
}
