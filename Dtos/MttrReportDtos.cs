namespace SynthWatch.Api.Dtos;

/// <summary>
/// GET /api/reports/mttr — fleet + per-check incident analytics over the window (§A5). Mirrors the
/// /reports/{incident-breakdown,slo} shape ({window, fleet, items, …}) + the ?tag=key:value AND-filter.
/// ★ MTTR (mean + median time-to-resolve) is computed ONLY over RESOLVED incidents; open incidents are
/// EXCLUDED from the durations but COUNTED (openCount) — never a fabricated resolve time. Both mean AND
/// median are reported (a few long-tail incidents skew the mean; the median shows the typical case).
/// meanSeconds/medianSeconds are null on insufficient data — never 0 (0 would read as "instant recovery").
/// </summary>
public record MttrReportResponseDto(
    string Window,
    MttrFleetDto Fleet,
    IReadOnlyList<MttrCheckDto> Items,
    IReadOnlyList<MttrClassificationBucketDto> Classification,
    IReadOnlyList<MttrTrendPointDto> Trend);

/// <summary>One check's incident analytics over the window. mean/median are null when the check has fewer
/// than the minimum resolved incidents (insufficientData) — the counts stay honest even then. mttdProxySeconds
/// = mean(consecutive_failures × interval) — a DETECTION-lag PROXY (time from first failure to open), not a
/// measured MTTD.</summary>
public record MttrCheckDto(
    long CheckId,
    string CheckName,
    string Kind,
    long ResolvedCount,
    long OpenCount,
    double? MeanSeconds,
    double? MedianSeconds,
    double? MttdProxySeconds,
    bool InsufficientData);

/// <summary>Fleet rollup — mean/median computed over ALL scoped resolved incidents' durations (NOT an
/// average/median of the per-check figures; the P4/availability lesson). Null on insufficient resolved data.</summary>
public record MttrFleetDto(
    long ResolvedCount,
    long OpenCount,
    long TotalIncidents,
    double? MeanSeconds,
    double? MedianSeconds,
    double? MttdProxySeconds,
    bool InsufficientData);

/// <summary>One rca.classification bucket (or "unclassified", always shown, never dropped — the P6 lesson).</summary>
public record MttrClassificationBucketDto(
    string Classification,
    long Count,
    decimal PctOfTotal);

/// <summary>One MTTR-trend bucket (day for ≤30d windows, week for 90d) — resolved incidents grouped by
/// opened_at, so "are we getting faster?" is visible. meanSeconds is over that bucket's resolved incidents.</summary>
public record MttrTrendPointDto(
    DateTimeOffset BucketStart,
    long ResolvedCount,
    double? MeanSeconds);
