using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Dtos;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Pure roll-up of per-region MAX(check_locations.last_run_at) into the /reports/region-health response:
/// classify each enabled region as fresh / stale / never_reported against a staleness threshold. Extracted
/// from the handler so the classification (the F-4 alarm boundary) is unit-testable + contract-anchorable.
/// </summary>
public static class RegionHealthProjection
{
    /// <summary>
    /// Staleness threshold = this multiple of the fleet's MIN enabled check interval. A live region claims a
    /// due check — advancing check_locations.last_run_at at CLAIM time — at least every min-interval, so N
    /// consecutive intervals of silence means the region's runner is dark (the F-4 alarm). A NAMED constant,
    /// deliberately not a literal: bump it if claim/scheduling jitter needs more slack before paging.
    /// </summary>
    public const int StalenessIntervalMultiplier = 3;

    // The three honest, mutually-exclusive states (see RegionHealthDto).
    public const string Fresh = "fresh";
    public const string Stale = "stale";
    public const string NeverReported = "never_reported";

    public static RegionHealthReportDto Build(int minIntervalSeconds, IReadOnlyList<RegionHealthRow> rows, DateTimeOffset asOf)
    {
        var threshold = StalenessIntervalMultiplier * minIntervalSeconds;
        var regions = rows
            .Select(r =>
            {
                // NULL max = zero claim data (no cursors, or only never-claimed ones): the region is configured
                // but has never run. An honest never_reported, NOT a fabricated fresh row (age/lastRunAt null).
                if (r.LastRunAt is null)
                    return new RegionHealthDto(r.Location, NeverReported, null, null);
                // Clamp at 0 so a cursor stamped microseconds in the future (clock skew) reads age 0, not negative.
                var age = (long)Math.Max(0, (asOf - r.LastRunAt.Value).TotalSeconds);
                return new RegionHealthDto(r.Location, age > threshold ? Stale : Fresh, r.LastRunAt, age);
            })
            .OrderBy(r => r.Location, StringComparer.Ordinal)
            .ToList();

        return new RegionHealthReportDto(minIntervalSeconds, threshold, regions);
    }
}
