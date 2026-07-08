using System;
using System.Collections.Generic;
using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>Pure cost-model tests (no DB/clock). Pins the confirmed model (recon #220 / #229 ~$67/mo):
/// projected = avg_s × (2,592,000/interval) × regions × rate; measured = 7d Σs × rate × 30/7; divergence =
/// measured/projected with a &gt;1.5 flag. Also pins that the RATE is config-driven (changing it changes
/// the output) and self-describing (echoed).</summary>
public class CostReportTests
{
    private const decimal Rate = 0.00003m; // the documented default (ACA vCPU-second)

    [Fact]
    public void Projected_and_measured_follow_the_confirmed_model()
    {
        // 10s avg, 300s interval (8,640 runs/mo/region), 2 regions, rate 0.00003:
        //   projected = 10 × 8640 × 2 × 0.00003 = $5.184 → 5.18
        //   measured  = 40,320 × 0.00003 × 30/7 = $5.184 → 5.18  (ratio 1.0, not divergent)
        var rows = new List<CostReportRow>
        {
            new() { CheckId = 1, SourceKey = "a", CheckName = "A", Kind = "http",
                    IntervalSeconds = 300, RegionCount = 2, AvgDurationS = 10.0, SumDurationS7d = 40320.0 },
        };
        var r = CostReportProjection.Build(rows, Rate, "src", "2026-07-08", DateTimeOffset.UnixEpoch);
        var a = Assert.Single(r.Checks);
        Assert.Equal(5.18m, a.ProjectedMonthly);
        Assert.Equal(5.18m, a.MeasuredMonthly7d);
        Assert.Equal(1.0m, a.DivergenceRatio);
        Assert.False(a.DivergenceFlag);
        Assert.Equal(5.18m, r.TotalProjectedMonthly);
        Assert.Equal(5.18m, r.TotalMeasuredMonthly);
        // self-describing: the rate + provenance are echoed verbatim.
        Assert.Equal(Rate, r.RateUsed);
        Assert.Equal("src", r.RateSource);
        Assert.Equal("2026-07-08", r.RateSetDate);
    }

    [Fact]
    public void Divergence_ratio_over_1_5_flags_retry_amplification()
    {
        // Same projected ($5.18) but the monitor actually burned ~2× the modeled compute (retries/failing flow).
        var rows = new List<CostReportRow>
        {
            new() { CheckId = 2, CheckName = "B", Kind = "http",
                    IntervalSeconds = 300, RegionCount = 2, AvgDurationS = 10.0, SumDurationS7d = 80640.0 },
        };
        var b = Assert.Single(CostReportProjection.Build(rows, Rate, "s", "d", DateTimeOffset.UnixEpoch).Checks);
        Assert.True(b.DivergenceRatio > 1.5m, $"ratio was {b.DivergenceRatio}");
        Assert.True(b.DivergenceFlag);
    }

    [Fact]
    public void No_runs_yields_zero_cost_and_null_divergence_never_fabricated()
    {
        var rows = new List<CostReportRow>
        {
            new() { CheckId = 3, CheckName = "C", Kind = "ssl",
                    IntervalSeconds = 300, RegionCount = 1, AvgDurationS = null, SumDurationS7d = null },
        };
        var c = Assert.Single(CostReportProjection.Build(rows, Rate, "s", "d", DateTimeOffset.UnixEpoch).Checks);
        Assert.Equal(0m, c.ProjectedMonthly);
        Assert.Equal(0m, c.MeasuredMonthly7d);
        Assert.Null(c.DivergenceRatio);   // honest empty, not a fabricated 0.0
        Assert.False(c.DivergenceFlag);
    }

    [Fact]
    public void Rate_is_a_linear_multiplier_and_checks_sort_by_projected_desc()
    {
        var rows = new List<CostReportRow>
        {
            new() { CheckId = 1, CheckName = "small", Kind = "http",   IntervalSeconds = 300, RegionCount = 1, AvgDurationS = 10.0 },
            new() { CheckId = 2, CheckName = "big",   Kind = "browser", IntervalSeconds = 60,  RegionCount = 3, AvgDurationS = 20.0 },
        };
        var at1x = CostReportProjection.Build(rows, 0.00003m, "s", "d", DateTimeOffset.UnixEpoch);
        var at2x = CostReportProjection.Build(rows, 0.00006m, "s", "d", DateTimeOffset.UnixEpoch);

        // ★ changing the (config) rate changes the output — doubling the rate doubles projected cost.
        Assert.Equal(at1x.Checks[0].ProjectedMonthly * 2m, at2x.Checks[0].ProjectedMonthly);
        Assert.Equal(0.00006m, at2x.RateUsed);
        // sorted by projected desc — the "big" driver leads, and top drivers mirror it.
        Assert.Equal("big", at1x.Checks[0].Name);
        Assert.Equal("big", at1x.TopCostDrivers[0].Name);
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
