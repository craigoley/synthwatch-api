using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Dtos;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Pure projection of raw <see cref="MttrIncidentRow"/>s into the /reports/mttr response: per-check + fleet
/// MTTR (mean + median over RESOLVED incidents), the classification breakdown (unclassified shown), and the
/// MTTR trend. Extracted from the handler so this outage-adjacent math is unit-testable + contract-anchored.
/// ★ Honesty: mean/median are null (never 0) when there are too few resolved incidents; the fleet figures are
/// computed over the FULL resolved set, never an average/median of the per-check aggregates.
/// </summary>
public static class MttrReportProjection
{
    // Fewer than this many resolved incidents → the mean/median isn't a reliable "time to resolve"; report
    // null + insufficientData rather than a noisy number. Counts are still shown.
    public const int MinResolved = 2;
    private const string Unclassified = "unclassified";

    public record Result(
        MttrFleetDto Fleet,
        IReadOnlyList<MttrCheckDto> Items,
        IReadOnlyList<MttrClassificationBucketDto> Classification,
        IReadOnlyList<MttrTrendPointDto> Trend);

    public static Result Build(IReadOnlyList<MttrIncidentRow> rows, int windowDays)
    {
        // ── per-check items ──
        var items = rows
            .GroupBy(r => r.CheckId)
            .Select(g =>
            {
                var first = g.First();
                var durations = ResolvedDurations(g);
                var insufficient = durations.Count < MinResolved;
                var mttd = MttdProxies(g);
                return new MttrCheckDto(
                    CheckId: first.CheckId,
                    CheckName: first.CheckName,
                    Kind: first.Kind,
                    ResolvedCount: durations.Count,
                    OpenCount: g.Count(r => r.Status == "open"),
                    MeanSeconds: insufficient ? null : Mean(durations),
                    MedianSeconds: insufficient ? null : Median(durations),
                    MttdProxySeconds: mttd.Count > 0 ? Mean(mttd) : null,
                    InsufficientData: insufficient);
            })
            // Worst (slowest) first so the fleet's attention items lead; null MTTR (thin) sorts last.
            .OrderByDescending(i => i.MeanSeconds ?? double.MinValue)
            .ThenBy(i => i.CheckName, StringComparer.Ordinal)
            .ToList();

        // ── fleet rollup: over the FULL resolved set (NOT a median-of-medians / mean-of-means) ──
        var allDurations = ResolvedDurations(rows);
        var fleetInsufficient = allDurations.Count < MinResolved;
        var allMttd = MttdProxies(rows);
        var fleet = new MttrFleetDto(
            ResolvedCount: allDurations.Count,
            OpenCount: rows.Count(r => r.Status == "open"),
            TotalIncidents: rows.Count,
            MeanSeconds: fleetInsufficient ? null : Mean(allDurations),
            MedianSeconds: fleetInsufficient ? null : Median(allDurations),
            MttdProxySeconds: allMttd.Count > 0 ? Mean(allMttd) : null,
            InsufficientData: fleetInsufficient);

        // ── classification breakdown (P6: unclassified always shown, sorted last) ──
        long total = rows.Count;
        var classification = rows
            .GroupBy(r => string.IsNullOrEmpty(r.Classification) ? Unclassified : r.Classification)
            .Select(g => new MttrClassificationBucketDto(
                g.Key, g.Count(), total > 0 ? Math.Round((decimal)g.Count() / total, 4) : 0m))
            .OrderBy(b => b.Classification == Unclassified).ThenByDescending(b => b.Count)
            .ThenBy(b => b.Classification, StringComparer.Ordinal)
            .ToList();

        // ── MTTR trend: resolved incidents bucketed by opened_at (day ≤30d, week for 90d) ──
        var weekly = windowDays > 30;
        var trend = rows
            .Where(IsResolved)
            .GroupBy(r => BucketStart(r.OpenedAt, weekly))
            .Select(g => new MttrTrendPointDto(
                g.Key, g.Count(), Mean(g.Select(DurationSeconds).ToList())))
            .OrderBy(p => p.BucketStart)
            .ToList();

        return new Result(fleet, items, classification, trend);
    }

    private static bool IsResolved(MttrIncidentRow r) => r.Status == "resolved" && r.ResolvedAt.HasValue;
    private static double DurationSeconds(MttrIncidentRow r) => (r.ResolvedAt!.Value - r.OpenedAt).TotalSeconds;

    private static List<double> ResolvedDurations(IEnumerable<MttrIncidentRow> rows) =>
        rows.Where(IsResolved).Select(DurationSeconds).ToList();

    private static List<double> MttdProxies(IEnumerable<MttrIncidentRow> rows) =>
        rows.Where(r => r.IntervalSeconds > 0)
            .Select(r => (double)r.ConsecutiveFailures * r.IntervalSeconds)
            .ToList();

    private static double Mean(List<double> xs) => Math.Round(xs.Average());

    private static double Median(List<double> xs)
    {
        var s = xs.OrderBy(x => x).ToList();
        int n = s.Count;
        return Math.Round(n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2.0);
    }

    // Truncate to the bucket start in UTC — midnight for a day, the preceding Sunday for a week.
    private static DateTimeOffset BucketStart(DateTimeOffset t, bool weekly)
    {
        var utc = t.ToUniversalTime();
        var day = new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
        return weekly ? day.AddDays(-(int)day.DayOfWeek) : day;
    }
}
