namespace SynthWatch.Api.Data.Entities;

/// <summary>Keyless projection for GET /api/reports/cost — one row per ENABLED check with the RAW cost
/// inputs measured from the DB. The projected/measured DOLLAR figures are NOT in SQL: CostReportProjection
/// applies the CONFIG rate in C#, so the rate stays a deploy-free tunable. avg/sum duration are over the
/// last 7d (float seconds); region_count = the check's assigned check_locations. ★ Pre-prod is INCLUDED
/// (unlike the SLO report's prod-only filter) — a staging monitor is real vCPU spend.</summary>
public class CostReportRow
{
    public long CheckId { get; set; }
    public string? SourceKey { get; set; }        // null for hand-made (non-manifest) checks
    public string CheckName { get; set; } = "";
    public string Kind { get; set; } = "";
    public int IntervalSeconds { get; set; }
    public int RegionCount { get; set; }           // count of assigned check_locations
    public double? AvgDurationS { get; set; }       // avg(duration_ms)/1000 over last 7d; null = no runs in window
    public double? SumDurationS7d { get; set; }     // sum(duration_ms)/1000 over last 7d; null = no runs in window
}
