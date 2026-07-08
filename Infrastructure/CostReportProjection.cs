using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Dtos;

namespace SynthWatch.Api.Infrastructure;

/// <summary>Pure cost math (no DB, no clock — everything passed in, so it's fully unit-testable). Applies
/// the CONFIG rate to the raw per-check durations/interval/regions to produce the ESTIMATE. Totals are
/// summed from the UNROUNDED per-check figures then rounded (no rounding drift on the aggregate), so the
/// fleet total reproduces the ~$67/mo model. Sorted by projected desc; topN drivers surfaced.</summary>
public static class CostReportProjection
{
    public const decimal DivergenceFlagThreshold = 1.5m;      // measured/projected above this = retry-amplification/failing-flow
    private const decimal SecondsPerMonth = 2_592_000m;        // 30 * 86400
    private const decimal MeasuredExtrapolation = 30m / 7m;    // 7d window -> month

    public static CostReportResponseDto Build(
        IReadOnlyList<CostReportRow> rows, decimal rate, string rateSource, string rateSetDate,
        DateTimeOffset now, int topN = 10)
    {
        var scored = rows.Select(r => Score(r, rate))
            .OrderByDescending(s => s.Projected).ThenBy(s => s.Dto.CheckId)
            .ToList();

        var totalProjected = Round(scored.Sum(s => s.Projected));   // sum RAW, then round (no per-check drift)
        var totalMeasured = Round(scored.Sum(s => s.Measured));
        var checks = scored.Select(s => s.Dto).ToList();
        return new CostReportResponseDto(
            now, rate, rateSource, rateSetDate,
            totalProjected, totalMeasured,
            checks.Take(topN).ToList(), checks);
    }

    private readonly record struct Scored(decimal Projected, decimal Measured, CostCheckDto Dto);

    private static Scored Score(CostReportRow r, decimal rate)
    {
        // projected = avg_s × runs/month × regions × rate;  runs/month = 2,592,000 / interval.
        decimal projected = r.AvgDurationS is double a && r.IntervalSeconds > 0
            ? (decimal)a * (SecondsPerMonth / r.IntervalSeconds) * r.RegionCount * rate
            : 0m;
        // measured = 7d Σseconds × rate × 30/7. The runs sum already spans all regions (one row per run per region).
        decimal measured = r.SumDurationS7d is double s ? (decimal)s * rate * MeasuredExtrapolation : 0m;
        decimal? ratio = projected > 0m ? Round(measured / projected, 3) : null;
        var dto = new CostCheckDto(
            r.CheckId, r.SourceKey, r.CheckName, r.Kind, r.IntervalSeconds, r.RegionCount, r.AvgDurationS,
            Round(projected), Round(measured), ratio, ratio is decimal d && d > DivergenceFlagThreshold);
        return new Scored(projected, measured, dto);
    }

    private static decimal Round(decimal v, int dp = 2) => Math.Round(v, dp, MidpointRounding.AwayFromZero);
}
