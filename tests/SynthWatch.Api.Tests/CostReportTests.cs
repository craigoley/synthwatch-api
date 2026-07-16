using System;
using System.Collections.Generic;
using System.Linq;
using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>Cost AGGREGATION tests (the $ MODEL now lives in the shared cost_projection(rate) SQL function —
/// runner 0069 — and is verified end-to-end by the /reports/cost integration test). Build no longer computes
/// per-check math; it maps the function's already-scored rows and aggregates: fleet total = sum of the RAW
/// per-check figures then rounded (no per-check rounding drift), sorted by projected desc, topN drivers.
/// Also pins that the config RATE is echoed self-describing.</summary>
public class CostReportTests
{
    private const decimal Rate = 0.00003m;

    // A row as the SQL function returns it: rounded projected/measured for display + the raw for totals.
    private static CostReportRow Scored(long id, string name, decimal raw, decimal? div = null, bool flag = false) =>
        new()
        {
            CheckId = id, CheckName = name, Kind = "http", IntervalSeconds = 300, RegionCount = 1, AvgDurationS = 10.0,
            Projected = Math.Round(raw, 2, MidpointRounding.AwayFromZero),
            Measured = Math.Round(raw, 2, MidpointRounding.AwayFromZero),
            Divergence = div, DivergenceFlag = flag, ProjectedRaw = raw, MeasuredRaw = raw,
        };

    [Fact]
    public void Fleet_total_sums_the_RAW_per_check_figures_then_rounds_no_drift()
    {
        // Two rows whose RAW sum (0.014 + 0.014 = 0.028 → 0.03) differs from summing the ROUNDED per-check
        // (0.01 + 0.01 = 0.02). Build must sum RAW then round → 0.03.
        var rows = new List<CostReportRow> { Scored(1, "a", 0.014m), Scored(2, "b", 0.014m) };
        var r = CostReportProjection.Build(rows, Rate, "src", "2026-07-08", DateTimeOffset.UnixEpoch);
        Assert.Equal(0.03m, r.TotalProjectedMonthly);   // sum RAW (0.028) then round — NOT 0.02
        Assert.Equal(0.03m, r.TotalMeasuredMonthly);
    }

    [Fact]
    public void Checks_sort_by_projected_desc_and_top_drivers_mirror_the_order()
    {
        var rows = new List<CostReportRow> { Scored(1, "small", 1.00m), Scored(2, "big", 9.00m), Scored(3, "mid", 5.00m) };
        var r = CostReportProjection.Build(rows, Rate, "s", "d", DateTimeOffset.UnixEpoch, topN: 2);
        Assert.Equal(new[] { "big", "mid", "small" }, r.Checks.Select(c => c.Name).ToArray());
        Assert.Equal(new[] { "big", "mid" }, r.TopCostDrivers.Select(c => c.Name).ToArray()); // topN=2 slice
        Assert.Equal(3, r.Checks.Count);                                                       // full list unbounded
    }

    [Fact]
    public void Divergence_and_flag_pass_through_from_the_function_verbatim()
    {
        var rows = new List<CostReportRow> { Scored(1, "x", 5.00m, div: 1.8m, flag: true) };
        var c = Assert.Single(CostReportProjection.Build(rows, Rate, "s", "d", DateTimeOffset.UnixEpoch).Checks);
        Assert.Equal(1.8m, c.DivergenceRatio);
        Assert.True(c.DivergenceFlag);
    }

    [Fact]
    public void Rate_and_provenance_are_echoed_self_describing()
    {
        var r = CostReportProjection.Build(new List<CostReportRow> { Scored(1, "a", 1m) }, Rate, "src", "2026-07-08", DateTimeOffset.UnixEpoch);
        Assert.Equal(Rate, r.RateUsed);
        Assert.Equal("src", r.RateSource);
        Assert.Equal("2026-07-08", r.RateSetDate);
    }

    [Fact]
    public void Count_columns_pass_through_from_the_function_verbatim()
    {
        var row = new CostReportRow
        {
            CheckId = 1, CheckName = "x", Kind = "browser", IntervalSeconds = 300, RegionCount = 1, AvgDurationS = 10.0,
            Projected = 5m, Measured = 9m, Divergence = 1.9m, DivergenceFlag = true, ProjectedRaw = 5m, MeasuredRaw = 9m,
            RunCount7d = 200, ConfirmationCount7d = 7, SandboxCount7d = 5, RunCountRecent = 100, RunCountPrior = 100,
        };
        var c = Assert.Single(CostReportProjection.Build(new List<CostReportRow> { row }, Rate, "s", "d", DateTimeOffset.UnixEpoch).Checks);
        Assert.Equal(200, c.RunCount7d);
        Assert.Equal(7, c.ConfirmationCount7d);
        Assert.Equal(5, c.SandboxCount7d);
        Assert.Equal(100, c.RunCountRecent);
        Assert.Equal(100, c.RunCountPrior);
    }

    [Fact]
    public void Estimated_dollar_is_primary_null_safe_and_the_fleet_total_sums_the_per_monitor_estimates()
    {
        var rows = new List<CostReportRow>
        {
            new() { CheckId = 1, CheckName = "shop", Kind = "browser", IntervalSeconds = 300, RegionCount = 1, AvgDurationS = 20.0,
                    EstimatedMonthly = 7.33m, FleetBillableMonthly = 12.99m, ActiveSecondsPct = 56m, ProjectedRaw = 10.37m },
            new() { CheckId = 2, CheckName = "cheap", Kind = "dns", IntervalSeconds = 60, RegionCount = 1, AvgDurationS = 0.1,
                    EstimatedMonthly = 0.18m, FleetBillableMonthly = 12.99m, ActiveSecondsPct = 0.3m, ProjectedRaw = 0.26m },
            new() { CheckId = 3, CheckName = "norun", Kind = "http", IntervalSeconds = 300, RegionCount = 1, AvgDurationS = null,
                    EstimatedMonthly = null, FleetBillableMonthly = 12.99m, ActiveSecondsPct = null, ProjectedRaw = 0m },
        };
        var r = CostReportProjection.Build(rows, Rate, "s", "d", DateTimeOffset.UnixEpoch);
        // ranked by the DOLLAR (primary), null last
        Assert.Equal(new[] { "shop", "cheap", "norun" }, r.Checks.Select(c => c.Name).ToArray());
        Assert.Equal(7.33m, r.Checks[0].EstimatedMonthly);
        Assert.Equal(0.18m, r.Checks[1].EstimatedMonthly);      // cheap monitor: NON-ZERO $ (grant spread, not zeroed)
        Assert.Null(r.Checks[2].EstimatedMonthly);              // no-runs → null, never a fake $0
        // fleet estimate = Σ per-monitor estimates (7.33 + 0.18 = 7.51)
        Assert.Equal(7.51m, r.EstimatedMonthlyTotal);
    }

    [Fact]
    public void Azure_headline_passes_through_null_when_absent_and_verbatim_when_present()
    {
        var rows = new List<CostReportRow> { Scored(1, "a", 1m) };
        // Absent (the deploy-safe / no-pull default) → Azure is NULL, never a fabricated 0.
        Assert.Null(CostReportProjection.Build(rows, Rate, "s", "d", DateTimeOffset.UnixEpoch).Azure);
        // Present → served verbatim (Azure's numbers, not modeled).
        var az = new AzureCostDto("resourceGroups/synthwatch-rg", "USD", new DateOnly(2026, 7, 1), 47.17m, 16, 76.30m, "https://portal/x", DateTimeOffset.UnixEpoch);
        var r = CostReportProjection.Build(rows, Rate, "s", "d", DateTimeOffset.UnixEpoch, azure: az);
        Assert.Same(az, r.Azure);
        Assert.Equal(47.17m, r.Azure!.MtdActual);
        Assert.Equal(76.30m, r.Azure.ForecastMonth);
    }

    [Fact]
    public void ActiveSeconds_and_share_round_trip_from_the_function_row()
    {
        var row = new CostReportRow
        {
            CheckId = 1, CheckName = "shop", Kind = "browser", IntervalSeconds = 300, RegionCount = 1, AvgDurationS = 20.0,
            ActiveSeconds = 60.000m, ActiveSecondsPct = 85.11m,  // 0089 — the attributable metric
            Projected = 5m, Measured = 9m, ProjectedRaw = 5m, MeasuredRaw = 9m,
        };
        var c = Assert.Single(CostReportProjection.Build(new List<CostReportRow> { row }, Rate, "s", "d", DateTimeOffset.UnixEpoch).Checks);
        Assert.Equal(60.000m, c.ActiveSeconds);
        Assert.Equal(85.11m, c.ActiveSecondsPct);
    }

    [Fact]
    public void MUST_GO_RED_the_rate_is_the_two_meter_blend_of_the_live_allocation_2x_the_old_scalar()
    {
        // The old scalar 0.00003 was the 1.0/2 blend; the current 2.0/4 shape is EXACTLY 2×.
        Assert.Equal(0.00003m, CostRate.Blend(1.0m, 2m));
        Assert.Equal(0.00006m, CostRate.Blend(2.0m, 4m));
        Assert.Equal(0.00006m, CostRate.DefaultPerActiveSecond);
        Assert.Equal(2m, CostRate.Blend(2.0m, 4m) / CostRate.Blend(1.0m, 2m));
    }

    [Fact]
    public void CostRate_derives_from_the_stamped_allocation_and_honours_an_override()
    {
        try
        {
            // Change the stamped allocation → the derived rate changes (proves it reads the LIVE allocation).
            Environment.SetEnvironmentVariable("SYNTHWATCH_RUNNER_CPU", "1.0");
            Environment.SetEnvironmentVariable("SYNTHWATCH_RUNNER_MEMORY_GIB", "2");
            Assert.Equal(0.00003m, CostRate.PerActiveSecond);
            Environment.SetEnvironmentVariable("SYNTHWATCH_RUNNER_CPU", "4.0");
            Environment.SetEnvironmentVariable("SYNTHWATCH_RUNNER_MEMORY_GIB", "8");
            Assert.Equal(0.00012m, CostRate.PerActiveSecond);

            // An explicit override wins over the derivation.
            Environment.SetEnvironmentVariable("COST_RATE_PER_ACTIVE_SECOND", "0.000099");
            Assert.Equal(0.000099m, CostRate.PerActiveSecond);
            Environment.SetEnvironmentVariable("COST_RATE_PER_ACTIVE_SECOND", "not-a-number");
            Assert.Equal(0.00012m, CostRate.PerActiveSecond); // invalid override → back to the derivation
        }
        finally
        {
            Environment.SetEnvironmentVariable("SYNTHWATCH_RUNNER_CPU", null);
            Environment.SetEnvironmentVariable("SYNTHWATCH_RUNNER_MEMORY_GIB", null);
            Environment.SetEnvironmentVariable("COST_RATE_PER_ACTIVE_SECOND", null);
        }
        Assert.Equal(CostRate.DefaultPerActiveSecond, CostRate.PerActiveSecond); // unset → current-shape fallback
    }
}
