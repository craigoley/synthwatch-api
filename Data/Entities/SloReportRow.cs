namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// Keyless projection for GET /api/reports/slo — one row per SLO-having check, produced by
/// <c>CROSS JOIN LATERAL slo_status(c.id, from, to)</c> (the existing per-check function; no new fleet
/// function) joined to the check's name/kind. A check with no slo_target yields zero slo_status rows, so
/// the LATERAL naturally drops it (SLO is opt-in). Mirrors <see cref="SloStatusRow"/> plus name/kind.
/// </summary>
public class SloReportRow
{
    public long CheckId { get; set; }
    public string CheckName { get; set; } = "";
    public string Kind { get; set; } = "";
    public float SloTarget { get; set; }
    public long TotalRuns { get; set; }
    public long DownRuns { get; set; }
    public decimal Budget { get; set; }             // allowed down-runs = total * (1 - target)
    public long Consumed { get; set; }              // down-runs in the window (== DownRuns)
    public decimal Remaining { get; set; }          // budget - consumed (negative => over budget)
    public decimal? RemainingPct { get; set; }      // null when budget is 0
    public decimal BurnRate { get; set; }           // (down/total) / (1 - target) — informational (pooled, window)
    // ★ P5 PR2 — the LOCATION-AWARE burn STATE from slo_burn_status(c.id): the SAME verdict the runner pages
    // on (read == page). 'fast' | 'slow' | 'none'; reported_burn = max at-floor burn of the firing window.
    public string BurnState { get; set; } = "none";
    public double ReportedBurn { get; set; }
}
