using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>
/// Pure fresh/stale/never_reported classification for /reports/region-health, with a FIXED asOf so the
/// staleness boundary is exact (no clock flake). Threshold = StalenessIntervalMultiplier × minInterval.
/// </summary>
public class RegionHealthProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);

    private static RegionHealthRow Row(string loc, DateTimeOffset? last) => new() { Location = loc, LastRunAt = last };

    [Fact]
    public void Boundary_at_exactly_threshold_is_fresh_one_second_over_is_stale()
    {
        const int min = 300;
        var threshold = RegionHealthProjection.StalenessIntervalMultiplier * min; // 900
        var dto = RegionHealthProjection.Build(min, new[]
        {
            Row("at-threshold", Now.AddSeconds(-threshold)),         // age == threshold → fresh (boundary inclusive)
            Row("just-over",    Now.AddSeconds(-(threshold + 1))),   // age == threshold+1 → stale
        }, Now);

        Assert.Equal(min, dto.MinIntervalSeconds);
        Assert.Equal(threshold, dto.StalenessThresholdSeconds);
        var byLoc = dto.Regions.ToDictionary(r => r.Location);
        Assert.Equal("fresh", byLoc["at-threshold"].Status);
        Assert.Equal(threshold, byLoc["at-threshold"].AgeSeconds);
        Assert.Equal("stale", byLoc["just-over"].Status);
    }

    // ★ the must-not-fabricate case: a region with no claim data is never_reported with NULL age/lastRunAt,
    //   NOT a fabricated fresh row.
    [Fact]
    public void Null_last_run_is_never_reported_with_null_age()
    {
        var dto = RegionHealthProjection.Build(300, new[] { Row("dark", null) }, Now);
        var r = Assert.Single(dto.Regions);
        Assert.Equal("never_reported", r.Status);
        Assert.Null(r.LastRunAt);
        Assert.Null(r.AgeSeconds);
    }

    // A cursor stamped microseconds in the future (clock skew) clamps to age 0 → fresh, never a negative age.
    [Fact]
    public void Future_timestamp_clamps_age_to_zero_and_is_fresh()
    {
        var dto = RegionHealthProjection.Build(300, new[] { Row("skewed", Now.AddSeconds(5)) }, Now);
        var r = Assert.Single(dto.Regions);
        Assert.Equal("fresh", r.Status);
        Assert.Equal(0, r.AgeSeconds);
    }

    [Fact]
    public void Regions_are_ordinal_sorted()
    {
        var dto = RegionHealthProjection.Build(300,
            new[] { Row("westus2", Now), Row("centralus", Now), Row("eastus2", Now) }, Now);
        Assert.Equal(new[] { "centralus", "eastus2", "westus2" }, dto.Regions.Select(r => r.Location).ToArray());
    }
}
