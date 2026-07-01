using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Dtos;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Pure projection of the per-check <see cref="SloReportRow"/>s into the /reports/slo response: per-check
/// items + a run-weighted ADDITIVE fleet rollup with insufficient-data honesty. Extracted from the handler
/// so this outage-adjacent aggregation is unit-testable and contract-anchored (mirrors SlaProjection).
/// </summary>
public static class SloReportProjection
{
    // Too few runs in the window → the budget/remaining% aren't trustworthy; report insufficientData and a
    // null pct rather than a fabricated number. Mirrors SlaProjection.MinCompletedRuns (20).
    public const long MinRuns = 20;

    public record Result(IReadOnlyList<SloReportItemDto> Items, SloReportFleetDto Fleet);

    public static Result Build(IReadOnlyList<SloReportRow> rows)
    {
        var items = new List<SloReportItemDto>(rows.Count);
        long sumTotal = 0, sumDown = 0;
        decimal sumBudget = 0m;

        foreach (var r in rows)
        {
            var insufficient = r.TotalRuns < MinRuns;
            items.Add(new SloReportItemDto(
                CheckId: r.CheckId,
                CheckName: r.CheckName,
                Kind: r.Kind,
                Target: r.SloTarget,
                TotalRuns: r.TotalRuns,
                DownRuns: r.DownRuns,
                Budget: r.Budget,
                Consumed: r.Consumed,
                Remaining: r.Remaining,
                RemainingPct: insufficient ? null : r.RemainingPct,   // never a fake % on thin data
                BurnRate: r.BurnRate,
                InsufficientData: insufficient));

            sumTotal += r.TotalRuns;
            sumDown += r.DownRuns;
            sumBudget += r.Budget;
        }

        // ★ ADDITIVE rollup — sum the budget + consumed, then derive the fleet % from those SUMS
        // (1 - Σconsumed/Σbudget). NEVER average the per-check percentages. An empty scope → an honest
        // zero/insufficient fleet, not a fabricated 100%.
        var fleetInsufficient = sumTotal < MinRuns;
        var fleet = new SloReportFleetDto(
            TotalRuns: sumTotal,
            DownRuns: sumDown,
            Budget: sumBudget,
            Consumed: sumDown,
            Remaining: sumBudget - sumDown,
            RemainingPct: (fleetInsufficient || sumBudget <= 0m)
                ? null
                : Math.Round(1m - (decimal)sumDown / sumBudget, 6),
            InsufficientData: fleetInsufficient);

        return new Result(items, fleet);
    }
}
