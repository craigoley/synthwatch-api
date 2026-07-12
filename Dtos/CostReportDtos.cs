namespace SynthWatch.Api.Dtos;

/// <summary>GET /api/reports/cost — ESTIMATED monthly ACA compute cost per monitor + fleet. ★ NOT billed
/// truth: the rate is a config tunable, ECHOED here (rateUsed/rateSource/rateSetDate) so every figure is
/// self-describing and the dashboard can label it an estimate. checks is all enabled monitors (pre-prod
/// INCLUDED — real spend); topCostDrivers is the top-N by projected, descending.</summary>
public record CostReportResponseDto(
    DateTimeOffset GeneratedAt,
    decimal RateUsed,
    string RateSource,
    string RateSetDate,
    decimal TotalProjectedMonthly,
    decimal TotalMeasuredMonthly,
    IReadOnlyList<CostCheckDto> TopCostDrivers,
    IReadOnlyList<CostCheckDto> Checks);

/// <summary>One monitor's estimated monthly cost. projectedMonthly = avgDurationS × (2,592,000/interval) ×
/// regionCount × rate. measuredMonthly7d = (7d Σduration) × rate × 30/7 (the runs sum already spans all
/// regions). divergenceRatio = measured/projected (null when projected is 0 / no runs). divergenceFlag =
/// ratio &gt; 1.5. ★ divergence is a PURE RUN-COUNT ratio (duration cancels): divergenceRatio = runCount7d /
/// expected — so a flag is a config-change straddle / confirmation / sandbox, NEVER retries. The count
/// columns (runCount7d + confirmation/sandbox + recent/prior halves) let the dashboard attribute from data.</summary>
public record CostCheckDto(
    long CheckId,
    string? SourceKey,
    string Name,
    string Kind,
    int IntervalSeconds,
    int RegionCount,
    double? AvgDurationS,
    decimal ProjectedMonthly,
    decimal MeasuredMonthly7d,
    decimal? DivergenceRatio,
    bool DivergenceFlag,
    int RunCount7d,
    int ConfirmationCount7d,
    int SandboxCount7d,
    int RunCountRecent,
    int RunCountPrior);
