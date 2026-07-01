namespace SynthWatch.Api.Dtos;

/// <summary>
/// GET /api/reports/slo — fleet + per-check error-BUDGET over the window (P5 v1). Only checks WITH an
/// slo_target appear (opt-in). Mirrors the /sla response shape {window, fleet, items}.
/// ★ burnRate is INFORMATIONAL — the pooled (all-location) 30d rate slo_status computes. There are
/// deliberately NO fast/slow-burn boolean pills here: pooled burn false-pages (a single distant region
/// trips a fleet page), so the location-aware fast/slow-burn decision is a SEPARATE follow-up PR.
/// </summary>
public record SloReportResponseDto(
    string Window,
    SloReportFleetDto Fleet,
    IReadOnlyList<SloReportItemDto> Items);

/// <summary>One SLO-having check's error budget over the window. remainingPct is null when the check has
/// too few runs (insufficientData) or a zero budget — never a fabricated 0%/100%.</summary>
public record SloReportItemDto(
    long CheckId,
    string CheckName,
    string Kind,
    float Target,
    long TotalRuns,
    long DownRuns,
    decimal Budget,
    long Consumed,
    decimal Remaining,
    decimal? RemainingPct,
    decimal BurnRate,          // informational only — see the response-DTO note (no burn pills in v1)
    bool InsufficientData);

/// <summary>Run-weighted ADDITIVE fleet rollup: budget/consumed/remaining are SUMMED across the scoped
/// checks, and remainingPct is derived from those sums (1 - Σconsumed/Σbudget) — NEVER an average of
/// per-check percentages (the availability/P4 lesson). Null pct on insufficient data / zero budget.</summary>
public record SloReportFleetDto(
    long TotalRuns,
    long DownRuns,
    decimal Budget,
    long Consumed,
    decimal Remaining,
    decimal? RemainingPct,
    bool InsufficientData);
