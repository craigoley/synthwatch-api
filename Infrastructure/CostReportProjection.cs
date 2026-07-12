using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Dtos;

namespace SynthWatch.Api.Infrastructure;

/// <summary>Assembles the /reports/cost response from rows the SHARED cost_projection(rate) SQL function
/// already scored (runner 0069). The $ MODEL (projected/measured/divergence formulas) lives ONLY in that
/// function — the SAME one the runner narrative fact pack calls — so the two are byte-identical by
/// construction. This class no longer does per-check math; it only AGGREGATES: fleet total = sum of the
/// UNROUNDED per-check figures then rounded (no rounding drift), sorted by projected desc, topN drivers.</summary>
public static class CostReportProjection
{
    public const decimal DivergenceFlagThreshold = 1.5m; // measured/projected above this (in the SQL fn) = EXTRA runs vs the current schedule — a pure run-count ratio (config-change straddle / confirmation / sandbox), NOT retries

    public static CostReportResponseDto Build(
        IReadOnlyList<CostReportRow> rows, decimal rate, string rateSource, string rateSetDate,
        DateTimeOffset now, int topN = 10)
    {
        var checks = rows
            .OrderByDescending(r => r.ProjectedRaw).ThenBy(r => r.CheckId)
            .Select(r => new CostCheckDto(
                r.CheckId, r.SourceKey, r.CheckName, r.Kind, r.IntervalSeconds, r.RegionCount, r.AvgDurationS,
                r.Projected, r.Measured, r.Divergence, r.DivergenceFlag,
                r.RunCount7d, r.ConfirmationCount7d, r.SandboxCount7d, r.RunCountRecent, r.RunCountPrior))
            .ToList();

        var totalProjected = Round(rows.Sum(r => r.ProjectedRaw)); // sum RAW, then round (no per-check drift)
        var totalMeasured = Round(rows.Sum(r => r.MeasuredRaw));
        return new CostReportResponseDto(
            now, rate, rateSource, rateSetDate,
            totalProjected, totalMeasured,
            checks.Take(topN).ToList(), checks);
    }

    private static decimal Round(decimal v, int dp = 2) => Math.Round(v, dp, MidpointRounding.AwayFromZero);
}
