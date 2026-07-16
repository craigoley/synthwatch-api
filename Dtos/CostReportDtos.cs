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
    IReadOnlyList<CostCheckDto> Checks,
    // ★ The HONEST dollar headline: Azure's OWN number, PULLED (runner azureCost.ts → azure_cost, 0090), not
    // modeled. NULL when we couldn't reach Cost Management (no pull yet / role not propagated / API error) OR
    // the cached figure is for a past billing month (stale-as-current guard). ★ null ≠ 0: the dashboard reads
    // absence as "see Azure Cost Management" (deep link), NEVER as "$0 spent". A modeled fleet $ is NOT served
    // here — the tool DISPLAYS Azure's total, it does not COMPETE with it. See the demoted per-check $ columns.
    AzureCostDto? Azure);

/// <summary>Azure Cost Management figures for the RG scope, cached by the runner (azure_cost, 0090) and served
/// VERBATIM — Azure's numbers, not modeled. MtdActual = month-to-date actual (all meters in scope); MtdDays =
/// days elapsed (the ramp denominator, so the dashboard can show "$47 over 16d"); ForecastMonth = Azure's OWN
/// end-of-month forecast (null when the forecast API returned none); PortalUrl = deep link to Cost Management;
/// FetchedAt = when the runner pulled it, so the dashboard shows "as of &lt;time&gt;" and judges staleness
/// itself. The whole object is null when absent/stale — the truth-about-absence the fallback depends on.</summary>
public record AzureCostDto(
    string Scope,
    string Currency,
    DateOnly BillingMonth,
    decimal MtdActual,
    int MtdDays,
    decimal? ForecastMonth,
    string PortalUrl,
    DateTimeOffset FetchedAt);

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
    // ★ 0089 — the attributable per-monitor metric: activeSeconds (Σ measured active-seconds over 7d) +
    // activeSecondsPct (share of FLEET compute; null when no monitor ran). Rank by this, not by the demoted $.
    decimal ActiveSeconds,
    decimal? ActiveSecondsPct,
    decimal ProjectedMonthly,   // DEMOTED from-zero $ (kept for the staged runner→api→dashboard migration)
    decimal MeasuredMonthly7d,  // DEMOTED ×30/7 annualizer
    decimal? DivergenceRatio,
    bool DivergenceFlag,
    int RunCount7d,
    int ConfirmationCount7d,
    int SandboxCount7d,
    int RunCountRecent,
    int RunCountPrior);
