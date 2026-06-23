namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// Keyless projection of the <c>slo_status(check_id, from, to)</c> function (migration 0016; mirrors
/// sla_availability). The runner owns the SLO math; this API only reads it. Returns NO rows when the
/// check has no slo_target (opt-in) — so a missing row means "no SLO configured".
/// </summary>
public class SloStatusRow
{
    public long CheckId { get; set; }
    public float SloTarget { get; set; }            // real, e.g. 0.99
    public DateTimeOffset WindowFrom { get; set; }
    public DateTimeOffset WindowTo { get; set; }
    public long TotalRuns { get; set; }
    public long DownRuns { get; set; }
    public decimal Budget { get; set; }             // allowed down-runs = total * (1 - target)
    public long Consumed { get; set; }              // down-runs in the window
    public decimal Remaining { get; set; }          // budget - consumed (negative => over budget)
    public decimal? RemainingPct { get; set; }      // null when budget is 0
    public decimal BurnRate { get; set; }           // (down/total) / (1 - target)
}
