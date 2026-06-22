using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Dtos;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Pure projection of raw SLA-view rows into the API response (per-check items + run-weighted fleet)
/// with insufficient-data handling. Extracted from the handler so this subtle, outage-adjacent
/// behavior is unit-testable without a database.
/// </summary>
public static class SlaProjection
{
    // A window's availability is only meaningful with enough samples AND enough coverage. A check
    // younger than the window would otherwise show a precise % over a fraction of the claimed period.
    public const long MinCompletedRuns = 20;   // need at least this many completed runs
    public const double MinCoverage = 0.8;     // check must have existed for >=80% of the window

    public record Result(IReadOnlyList<SlaDto> Items, SlaFleetDto Fleet);

    public static Result Build(
        IReadOnlyList<SlaAvailabilityRow> rows,
        IReadOnlyDictionary<long, DateTimeOffset> createdById)
    {
        var items = new List<SlaDto>(rows.Count);
        double maxCoverage = 0;
        long sumUp = 0, sumDown = 0, sumCompleted = 0;

        foreach (var r in rows)
        {
            var createdAt = createdById.TryGetValue(r.CheckId, out var ca) ? ca : r.WindowFrom;
            var coverage = WindowCoverage(r.WindowFrom, r.WindowTo, createdAt);
            var insufficient = r.CompletedRuns < MinCompletedRuns || coverage < MinCoverage;

            items.Add(new SlaDto(
                r.CheckId, r.CheckName, r.Kind, r.WindowFrom, r.WindowTo,
                r.CompletedRuns, r.UpRuns, r.DownRuns,
                AvailabilityPct: insufficient ? null : r.AvailabilityPct,
                InsufficientData: insufficient));

            sumUp += r.UpRuns;
            sumDown += r.DownRuns;
            sumCompleted += r.CompletedRuns;
            if (coverage > maxCoverage) maxCoverage = coverage;
        }

        var fleetInsufficient = sumCompleted < MinCompletedRuns || maxCoverage < MinCoverage;
        var fleet = new SlaFleetDto(
            CompletedRuns: sumCompleted,
            UpRuns: sumUp,
            DownRuns: sumDown,
            AvailabilityPct: fleetInsufficient || sumCompleted == 0
                ? null
                : Math.Round(100m * sumUp / sumCompleted, 4),
            InsufficientData: fleetInsufficient);

        return new Result(items, fleet);
    }

    /// <summary>Fraction (0..1) of [from,to) for which the check has existed.</summary>
    public static double WindowCoverage(DateTimeOffset from, DateTimeOffset to, DateTimeOffset createdAt)
    {
        var total = (to - from).TotalSeconds;
        if (total <= 0) return 0;
        var start = createdAt > from ? createdAt : from;
        var covered = (to - start).TotalSeconds / total;
        return covered < 0 ? 0 : covered > 1 ? 1 : covered;
    }
}
