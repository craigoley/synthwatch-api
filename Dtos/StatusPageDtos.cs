namespace SynthWatch.Api.Dtos;

/// <summary>
/// GET /api/status — the internal/stakeholder status page (§A3). A curated, property-level rollup: is
/// wegmans.com / meals2go / … up right now, its uptime over the window, and recent incidents. Composed from
/// data that already exists (current check status + SLA + incidents); NO new capture. ★ Property-level ONLY —
/// no raw check ids/URLs/internal hosts, so it's safe for stakeholders. `state` is the CURRENT badge;
/// `uptimePct` is HISTORICAL — deliberately separate fields so a green "up" now can't be read as a claim
/// about the window's availability.
/// </summary>
public record StatusPageDto(
    string Window,
    IReadOnlyList<StatusPropertyDto> Properties,
    IReadOnlyList<StatusIncidentDto> RecentIncidents);

/// <summary>One property (an `area:` tag value) rolled up from its checks. state = up (all pass) | degraded
/// (any warn / non-critical fail / open warning) | down (any critical fail / open critical) | unknown (no
/// data yet). uptimePct is null while buildingBaseline (too few completed runs) — never a fabricated %.</summary>
public record StatusPropertyDto(
    string Name,
    string State,
    int CheckCount,
    int UpCount,
    int DegradedCount,
    int DownCount,
    decimal? UptimePct,
    bool BuildingBaseline);

/// <summary>A recent incident, property-scoped. title = the incident summary (or the check name) — a human
/// label, not a raw internal id/URL.</summary>
public record StatusIncidentDto(
    string Property,
    string Title,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ResolvedAt,
    string Status,
    string Severity);
