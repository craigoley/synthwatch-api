namespace SynthWatch.Api.Data.Entities;

// Keyless projections for the reporting read-endpoints. Availability aggregates additively from the
// daily rollup; latency/web-vitals percentiles are RECOMPUTED FROM RAW runs over the window (#88's
// non-negotiable — percentiles are NOT averageable across days). GroupValue is the tag value when
// grouped (null when ungrouped); a null CheckId in the GROUPING SETS rows marks the group-level aggregate.

/// <summary>Per-(group, check) availability aggregated from the daily rollup counts (additive).</summary>
public class AvailabilityReportRow
{
    public string? GroupValue { get; set; }
    public long CheckId { get; set; }
    public string CheckName { get; set; } = "";
    public long UpCount { get; set; }
    public long DownCount { get; set; }
    public long TotalCount { get; set; }
    public decimal DowntimeMinutes { get; set; }
    public long IncidentsOpened { get; set; }
}

/// <summary>Daily availability counts per group (the trend series), summed from the rollup.</summary>
public class AvailabilitySeriesRow
{
    public string? GroupValue { get; set; }
    public DateOnly Day { get; set; }
    public long UpCount { get; set; }
    public long DownCount { get; set; }
}

/// <summary>Latency over the window RECOMPUTED FROM RAW. CheckId null = the group-level row (GROUPING SETS).</summary>
public class LatencyReportRow
{
    public string? GroupValue { get; set; }
    public long? CheckId { get; set; }
    public long LatencyCount { get; set; }
    public double? AvgMs { get; set; }
    public int? P50Ms { get; set; }
    public int? P95Ms { get; set; }
    public int? P99Ms { get; set; }
}

/// <summary>Browser web-vitals over the window RECOMPUTED FROM RAW (run_metrics). CheckId null = group-level.</summary>
public class VitalsReportRow
{
    public string? GroupValue { get; set; }
    public long? CheckId { get; set; }
    public long VitalsCount { get; set; }
    public int? LcpP75Ms { get; set; }
    public int? FcpP75Ms { get; set; }
    public int? TtfbP75Ms { get; set; }
    public double? ClsP75 { get; set; }
}

/// <summary>Daily latency trend per group — count-weighted avg from the rollup (avg IS aggregatable).</summary>
public class LatencySeriesRow
{
    public string? GroupValue { get; set; }
    public DateOnly Day { get; set; }
    public double? AvgMs { get; set; }
}
