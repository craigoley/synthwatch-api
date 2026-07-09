namespace SynthWatch.Api.Data.Entities;

/// <summary>Keyless projection for GET /api/reports/cost — one row per ENABLED check from the SHARED cost
/// model <c>cost_projection(rate)</c> (runner migration 0069). The $ math (projected/measured/divergence) now
/// lives ONLY in that SQL function, called with the CONFIG rate — so /reports/cost and the runner narrative
/// fact pack are byte-identical by construction (no second C# copy of the formula). Projected/Measured are
/// rounded 2dp for display; ProjectedRaw/MeasuredRaw are unrounded (sum these for the fleet total, THEN round
/// — no per-check rounding drift). ★ Pre-prod INCLUDED — a staging monitor is real vCPU spend.</summary>
public class CostReportRow
{
    public long CheckId { get; set; }
    public string? SourceKey { get; set; }        // null for hand-made (non-manifest) checks
    public string CheckName { get; set; } = "";
    public string Kind { get; set; } = "";
    public int IntervalSeconds { get; set; }
    public int RegionCount { get; set; }           // count of assigned check_locations
    public double? AvgDurationS { get; set; }       // avg(duration_ms)/1000 over last 7d; null = no runs in window
    public decimal Projected { get; set; }          // rounded 2dp (display)
    public decimal Measured { get; set; }           // rounded 2dp (display)
    public decimal? Divergence { get; set; }        // rounded 3dp; null when projected = 0
    public bool DivergenceFlag { get; set; }        // divergence > 1.5
    public decimal ProjectedRaw { get; set; }       // unrounded — sum for the fleet total
    public decimal MeasuredRaw { get; set; }
}
