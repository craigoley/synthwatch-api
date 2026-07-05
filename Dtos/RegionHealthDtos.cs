namespace SynthWatch.Api.Dtos;

/// <summary>
/// GET /api/reports/region-health — per-region freshness so a SILENTLY-DEAD region becomes visible (F-4: a
/// dead region stops writing runs, quorum degrades gracefully and therefore invisibly). Expected regions are
/// DECLARATIVE (locations WHERE enabled); a region that's configured but dark still appears (it's a locations
/// row) with a stale/never_reported status. <see cref="StalenessThresholdSeconds"/> = the age past which a
/// region is stale (<see cref="MinIntervalSeconds"/> × a named-constant multiplier). Read-only.
/// </summary>
public record RegionHealthReportDto(
    int MinIntervalSeconds,
    int StalenessThresholdSeconds,
    IReadOnlyList<RegionHealthDto> Regions);

/// <summary>
/// One region's health. <see cref="Status"/> is one of three HONEST, distinct states:
/// <c>fresh</c> (age ≤ threshold), <c>stale</c> (age &gt; threshold — the F-4 alarm), or
/// <c>never_reported</c> (an enabled region with zero claim data — configured but never ran; distinct from
/// stale, and never a fabricated fresh row). <see cref="LastRunAt"/> and <see cref="AgeSeconds"/> are null
/// exactly when never_reported.
/// </summary>
public record RegionHealthDto(
    string Location,
    string Status,
    DateTimeOffset? LastRunAt,
    long? AgeSeconds);
