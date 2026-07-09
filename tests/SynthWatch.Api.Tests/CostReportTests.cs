using System;
using System.Collections.Generic;
using System.Linq;
using SynthWatch.Api.Data.Entities;
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
    public void CostRate_reads_the_config_env_var_and_falls_back_to_the_default()
    {
        try
        {
            Environment.SetEnvironmentVariable("COST_RATE_PER_VCPU_SECOND", "0.00009");
            Assert.Equal(0.00009m, CostRate.PerVcpuSecond); // config wins — no code deploy needed

            Environment.SetEnvironmentVariable("COST_RATE_PER_VCPU_SECOND", "not-a-number");
            Assert.Equal(CostRate.DefaultPerVcpuSecond, CostRate.PerVcpuSecond); // invalid → safe default
        }
        finally
        {
            Environment.SetEnvironmentVariable("COST_RATE_PER_VCPU_SECOND", null);
        }
        Assert.Equal(CostRate.DefaultPerVcpuSecond, CostRate.PerVcpuSecond); // unset → default
    }
}
