using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Dtos;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Pure rollup of the raw /status rows into the stakeholder page: per-PROPERTY current state (up/degraded/down)
/// + windowed uptime + recent incidents. Extracted from the handler so the (subtle, outage-adjacent) state
/// rule + the additive uptime are unit-testable + contract-anchored. ★ current-state is DISTINCT from uptime:
/// a check's warn is DOWN-adjacent for the badge (degraded) but UP for availability (sla counts warn as up) —
/// they're deliberately different lenses, so they live in different fields.
/// </summary>
public static class StatusPageProjection
{
    // Too few completed runs in the window → the uptime % isn't trustworthy: report null + buildingBaseline,
    // never a fake number. Mirrors SlaProjection.MinCompletedRuns.
    public const long MinCompletedRuns = 20;

    public static StatusPageDto Build(
        string window,
        IReadOnlyList<StatusCheckRow> checks,
        IReadOnlyList<StatusSlaRow> sla,
        IReadOnlyList<StatusIncidentRow> incidents)
    {
        // Uptime per property (additive: Σup/Σcompleted, NOT an average of per-check %s).
        var uptime = sla
            .GroupBy(s => s.Property)
            .ToDictionary(g => g.Key, g =>
            {
                long completed = g.Sum(s => s.CompletedRuns), up = g.Sum(s => s.UpRuns);
                bool building = completed < MinCompletedRuns;
                return (Pct: building || completed == 0 ? (decimal?)null : Math.Round(100m * up / completed, 4), Building: building);
            });

        var properties = checks
            .GroupBy(c => c.Property)
            .Select(g =>
            {
                int down = g.Count(IsDownCritical);
                int degraded = g.Count(c => !IsDownCritical(c) && IsDegraded(c));
                int up = g.Count(c => c.Status == "pass" && !c.HasOpenIncident);
                string state = down > 0 ? "down" : degraded > 0 ? "degraded" : up > 0 ? "up" : "unknown";
                var u = uptime.TryGetValue(g.Key, out var uv) ? uv : (Pct: null, Building: true);
                return new StatusPropertyDto(
                    Name: g.Key,
                    State: state,
                    CheckCount: g.Count(),
                    UpCount: up,
                    DegradedCount: degraded,
                    DownCount: down,
                    UptimePct: u.Pct,
                    BuildingBaseline: u.Building);
            })
            // Attention first: down → degraded → up → unknown, then by name.
            .OrderBy(p => StateRank(p.State)).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var recent = incidents
            .Select(i => new StatusIncidentDto(
                Property: i.Property,
                Title: !string.IsNullOrWhiteSpace(i.Summary) ? i.Summary! : i.CheckName,
                OpenedAt: i.OpenedAt,
                ResolvedAt: i.ResolvedAt,
                Status: i.Status,
                Severity: i.Severity))
            .ToList();

        return new StatusPageDto(window, properties, recent);
    }

    // Down (critical): a critical check is currently failing, OR it has an open CRITICAL incident.
    private static bool IsDownCritical(StatusCheckRow c) =>
        (c.Severity == "critical" && (c.Status == "fail" || c.Status == "error"))
        || (c.HasOpenIncident && c.OpenSeverity == "critical");

    // Degraded: a warn run, a non-critical (warning) failure, or any open incident that isn't a critical page.
    private static bool IsDegraded(StatusCheckRow c) =>
        c.Status == "warn"
        || (c.Severity == "warning" && (c.Status == "fail" || c.Status == "error"))
        || c.HasOpenIncident;

    private static int StateRank(string state) => state switch
    {
        "down" => 0,
        "degraded" => 1,
        "up" => 2,
        _ => 3,
    };
}
