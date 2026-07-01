namespace SynthWatch.Api.Data.Entities;

/// <summary>Keyless projection for GET /api/reports/egress — one row per (location, distinct egress_ip) over
/// the window, from runs.egress_ip (migration 0054). Read-only; the NULL-filter (egress_ip IS NOT NULL) is
/// the correctness point (runs with no captured IP don't count). Rolled per-region by EgressReportProjection.</summary>
public class EgressRunRow
{
    public string Location { get; set; } = "";
    public string Ip { get; set; } = "";
    public long RunCount { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}
