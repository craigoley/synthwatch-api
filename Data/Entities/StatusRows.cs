namespace SynthWatch.Api.Data.Entities;

// Keyless projections for GET /status (the internal/stakeholder status page). Each groups by the `area:` tag
// value (the curated PROPERTY, e.g. wegmans.com / meals2go / restaurants). Only area-tagged, enabled checks
// participate — internal/untagged checks (API health, httpbin, the dashboard itself) are naturally excluded,
// so the page never leaks internal targets. The pure StatusPageProjection does the up/degraded/down rollup.

/// <summary>One area-tagged enabled check's CURRENT signal — its latest-run status + severity + open-incident
/// severity. The projection classifies it into up/degraded/down and rolls the property up.</summary>
public class StatusCheckRow
{
    public string Property { get; set; } = "";
    public string Severity { get; set; } = "";           // 'critical' | 'warning'
    public string Status { get; set; } = "";             // latest run: pass|warn|fail|error|running, or 'unknown'
    public bool HasOpenIncident { get; set; }
    public string? OpenSeverity { get; set; }            // max severity among this check's OPEN incidents
}

/// <summary>One area-tagged check's SLA counts over the window — summed per property for the uptime %
/// (Σup/Σcompleted; additive, never an average of per-check %s — the P4 lesson).</summary>
public class StatusSlaRow
{
    public string Property { get; set; } = "";
    public long CompletedRuns { get; set; }
    public long UpRuns { get; set; }
    public long DownRuns { get; set; }
}

/// <summary>A recent incident on an area-tagged check — property-level only (title/when/status/severity); no
/// raw check id or target_url is exposed to the stakeholder surface.</summary>
public class StatusIncidentRow
{
    public string Property { get; set; } = "";
    public string CheckName { get; set; } = "";
    public string? Summary { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string Status { get; set; } = "";             // 'open' | 'resolved'
    public string Severity { get; set; } = "";           // 'critical' | 'warning'
}
