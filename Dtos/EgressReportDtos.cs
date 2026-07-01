namespace SynthWatch.Api.Dtos;

/// <summary>
/// GET /api/reports/egress?window=all|24h — per-region egress-IP stability, for the status-page egress panel
/// (the Wegmans allowlist artifact + a live SNAT-rotation early-warning). Read-only over runs.egress_ip.
/// distinctCount &gt; 1 for a region = a rotation (multiple SNAT IPs seen in the window) — the signal the panel exists for.
/// </summary>
public record EgressReportDto(
    string Window,
    IReadOnlyList<EgressRegionDto> Regions);

/// <summary>One region's egress IPs over the window. currentIps = the distinct IPs present in the window
/// (a single stable IP when distinctCount==1; ALL of them when ≥2 — a rotation). ips[] carries each IP's
/// run count + first/last-seen so a rotation's timing is visible.</summary>
public record EgressRegionDto(
    string Location,
    IReadOnlyList<string> CurrentIps,
    int DistinctCount,
    IReadOnlyList<EgressIpDto> Ips);

/// <summary>One distinct egress IP seen for a region in the window.</summary>
public record EgressIpDto(
    string Ip,
    long RunCount,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);
