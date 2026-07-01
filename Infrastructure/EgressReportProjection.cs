using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Dtos;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Pure roll-up of the per-(location, egress_ip) rows into the /reports/egress response: one region per
/// location, its distinct IPs (each with run count + first/last-seen), distinctCount, and currentIps.
/// ★ Never dedupes a region's 2nd+ IP away — a rotation MUST stay visible in the payload (that's the panel's
/// whole point). Extracted from the handler so the roll-up is unit-testable + contract-anchorable.
/// </summary>
public static class EgressReportProjection
{
    public static EgressReportDto Build(string window, IReadOnlyList<EgressRunRow> rows)
    {
        var regions = rows
            .GroupBy(r => r.Location)
            .Select(g =>
            {
                // Each element of the group is one DISTINCT egress_ip for this region (SQL GROUP BY location,
                // egress_ip), ordered by first-seen so the rotation timeline reads oldest→newest.
                var ips = g
                    .OrderBy(r => r.FirstSeen)
                    .Select(r => new EgressIpDto(r.Ip, r.RunCount, r.FirstSeen, r.LastSeen))
                    .ToList();
                var currentIps = ips.Select(i => i.Ip).ToList(); // window already scopes "current"
                return new EgressRegionDto(g.Key, currentIps, ips.Count, ips);
            })
            .OrderBy(r => r.Location, StringComparer.Ordinal)
            .ToList();

        return new EgressReportDto(window, regions);
    }
}
