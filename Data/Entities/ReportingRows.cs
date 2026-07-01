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
    // INP is captured only on interaction runs (~half; load-only runs have NULL inp_ms — correct, not a bug),
    // so its sample size differs from VitalsCount. InpCount = count of non-null inp_ms; InpP75Ms is the p75 over
    // that subset (null when zero). ResourceCount = avg resources/page (a 100%-captured page-weight sibling).
    public int? InpP75Ms { get; set; }
    public long InpCount { get; set; }
    public int? ResourceCount { get; set; }
}

/// <summary>Daily latency trend per group — count-weighted avg from the rollup (avg IS aggregatable).</summary>
public class LatencySeriesRow
{
    public string? GroupValue { get; set; }
    public DateOnly Day { get; set; }
    public double? AvgMs { get; set; }
}

/// <summary>One GROUP BY row of the incident verdict-taxonomy breakdown: an rca.classification (null =
/// unclassified) + how many incidents in the window carried it. Read via raw SQL only (keyless).</summary>
public class IncidentBreakdownRow
{
    public string? Classification { get; set; }
    public long Count { get; set; }
}

/// <summary>§D1 trust scorecard — one raw row per ENABLED check: run/retry/last-green aggregates, the RCA
/// verdict counts, and the latest run's spec provenance. All MEASURED facts; the trust chip + retryRate are
/// derived in TrustReportProjection (kept out of SQL so the rules stay legible + unit-testable). Keyless,
/// raw SQL only. LastGreenAt null = never verified green (a first-class state, not a missing row).</summary>
public class TrustMonitorRow
{
    public long CheckId { get; set; }
    public string CheckName { get; set; } = "";
    public bool Sensitive { get; set; }
    public int IntervalSeconds { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? LastGreenAt { get; set; }
    public long RunCount { get; set; }
    public long RetryCount { get; set; }
    public long IncidentTotal { get; set; }
    public long RealOutage { get; set; }
    public long FlakyTransient { get; set; }
    public long SelectorDrift { get; set; }
    public long EnvironmentRegional { get; set; }
    public long PerfRegression { get; set; }
    public long Unclassified { get; set; }
    public string? ExecutedSha256 { get; set; }
    public string? SpecPath { get; set; }
}

/// <summary>§D1 trust detail — one day of the retry-rate trend for a single check (the detail sparkline).
/// RetryCount = runs that day needing ≥1 retry; RunCount = total that day. Keyless, raw SQL only.</summary>
public class TrustRetryDayRow
{
    public DateOnly Day { get; set; }
    public long RunCount { get; set; }
    public long RetryCount { get; set; }
}
