using System.Text.Json.Serialization;

namespace SynthWatch.Api.Dtos;

// Report responses. groupBy is the tag key when grouped (null = ungrouped/fleet, served as one group
// with group=null). Availability aggregates additively from the daily rollup; latency/web-vitals
// percentiles are recomputed from raw runs over the window (NOT averaged daily percentiles).

public record AvailabilityPointDtoR(
    [property: JsonPropertyName("day")] DateOnly Day,
    [property: JsonPropertyName("availabilityPct")] decimal? AvailabilityPct,
    [property: JsonPropertyName("upCount")] long UpCount,
    [property: JsonPropertyName("downCount")] long DownCount);

public record AvailabilityCheckDto(
    [property: JsonPropertyName("checkId")] long CheckId,
    [property: JsonPropertyName("checkName")] string CheckName,
    [property: JsonPropertyName("availabilityPct")] decimal? AvailabilityPct,
    [property: JsonPropertyName("upCount")] long UpCount,
    [property: JsonPropertyName("downCount")] long DownCount,
    [property: JsonPropertyName("downtimeMinutes")] decimal DowntimeMinutes,
    [property: JsonPropertyName("incidentsOpened")] long IncidentsOpened);

public record AvailabilityGroupDto(
    [property: JsonPropertyName("group")] string? Group,
    [property: JsonPropertyName("availabilityPct")] decimal? AvailabilityPct,
    [property: JsonPropertyName("upCount")] long UpCount,
    [property: JsonPropertyName("downCount")] long DownCount,
    [property: JsonPropertyName("totalCount")] long TotalCount,
    [property: JsonPropertyName("downtimeMinutes")] decimal DowntimeMinutes,
    [property: JsonPropertyName("incidentsOpened")] long IncidentsOpened,
    [property: JsonPropertyName("checks")] IReadOnlyList<AvailabilityCheckDto> Checks,
    [property: JsonPropertyName("series")] IReadOnlyList<AvailabilityPointDtoR> Series);

public record AvailabilityReportDto(
    [property: JsonPropertyName("window")] string Window,
    [property: JsonPropertyName("groupBy")] string? GroupBy,
    [property: JsonPropertyName("groups")] IReadOnlyList<AvailabilityGroupDto> Groups);

/// <summary>One verdict-taxonomy bucket: an rca.classification (or "unclassified") + its incident count and
/// share of the window's total. classification is one of the 5 enum values or "unclassified".</summary>
public record IncidentBreakdownBucketDto(
    [property: JsonPropertyName("classification")] string Classification,
    [property: JsonPropertyName("count")] long Count,
    [property: JsonPropertyName("pctOfTotal")] decimal PctOfTotal);

/// <summary>
/// Reports P6 — the alert-quality answer to "how many reds were real vs monitor-bug vs transient", from
/// incidents.rca.classification over the window. ★ ALERT PRECISION = realOutages / classified (the fraction of
/// JUDGED reds that were genuine real-outages); null when classified == 0 (honest empty, not a fake 0%).
/// unclassified is an explicit bucket — incidents with no RCA classification yet are never dropped.
/// </summary>
public record IncidentBreakdownDto(
    [property: JsonPropertyName("window")] string Window,
    [property: JsonPropertyName("total")] long Total,
    [property: JsonPropertyName("classified")] long Classified,
    [property: JsonPropertyName("unclassified")] long Unclassified,
    [property: JsonPropertyName("realOutages")] long RealOutages,
    [property: JsonPropertyName("precision")] decimal? Precision,
    [property: JsonPropertyName("buckets")] IReadOnlyList<IncidentBreakdownBucketDto> Buckets);

/// <summary>Latency over the window — percentiles recomputed from raw (NOT averaged daily percentiles).</summary>
public record LatencyDto(
    [property: JsonPropertyName("sampleCount")] long SampleCount,
    [property: JsonPropertyName("avgMs")] double? AvgMs,
    [property: JsonPropertyName("p50Ms")] int? P50Ms,
    [property: JsonPropertyName("p95Ms")] int? P95Ms,
    [property: JsonPropertyName("p99Ms")] int? P99Ms);

/// <summary>Browser web-vitals over the window (p75, recomputed from raw). Null for groups/checks with no browser runs. No INP.</summary>
public record WebVitalsDto(
    [property: JsonPropertyName("sampleCount")] long SampleCount,
    [property: JsonPropertyName("lcpP75Ms")] int? LcpP75Ms,
    [property: JsonPropertyName("fcpP75Ms")] int? FcpP75Ms,
    [property: JsonPropertyName("ttfbP75Ms")] int? TtfbP75Ms,
    [property: JsonPropertyName("clsP75")] double? ClsP75,
    // ★ INP is captured only on interaction runs (~half; load-only runs have none). inpP75Ms is the p75 over
    // the non-null subset (null when zero); inpCount is that subset's size — DISTINCT from sampleCount, so the
    // UI can honestly show "INP over N runs" and not present a half-sample p75 as if it were the full sample.
    [property: JsonPropertyName("inpP75Ms")] int? InpP75Ms,
    [property: JsonPropertyName("inpCount")] long InpCount,
    // avg resources/page (a 100%-captured page-weight sibling — averaged like page weight, not a percentile).
    [property: JsonPropertyName("resourceCount")] int? ResourceCount);

public record LatencyPointDto(
    [property: JsonPropertyName("day")] DateOnly Day,
    [property: JsonPropertyName("avgMs")] double? AvgMs);

public record PerformanceCheckDto(
    [property: JsonPropertyName("checkId")] long CheckId,
    [property: JsonPropertyName("checkName")] string CheckName,
    [property: JsonPropertyName("latency")] LatencyDto Latency,
    [property: JsonPropertyName("webVitals")] WebVitalsDto? WebVitals);

public record PerformanceGroupDto(
    [property: JsonPropertyName("group")] string? Group,
    [property: JsonPropertyName("latency")] LatencyDto Latency,
    [property: JsonPropertyName("webVitals")] WebVitalsDto? WebVitals,
    [property: JsonPropertyName("checks")] IReadOnlyList<PerformanceCheckDto> Checks,
    [property: JsonPropertyName("series")] IReadOnlyList<LatencyPointDto> Series);

public record PerformanceReportDto(
    [property: JsonPropertyName("window")] string Window,
    [property: JsonPropertyName("groupBy")] string? GroupBy,
    [property: JsonPropertyName("groups")] IReadOnlyList<PerformanceGroupDto> Groups);

// ── §D1 Monitor-Trust Scorecard (the "every green shown with its proof" surface) ─────────────────────────
// ★ THE LOAD-BEARING DESIGN RULE: there is NO synthesized 0-100 trust score anywhere in this shape. Every
// field is a MEASURED FACT carrying its own sample size; `trust` is a CHIP derived from STATED, AUDITABLE
// rules (see TrustReportProjection) — never a magic composite. A blended score would silently imply
// red-test coverage that does not exist (Signal 1 is uncaptured), which is the exact false-confidence this
// feature exists to kill. Honesty is the product: null sample → null (never a fake 0); never-green →
// lastGreenAt null (a first-class state, not an error); redTest is an explicit "not captured" slot.

/// <summary>The verdict-taxonomy breakdown for ONE monitor over the window. All six named buckets +
/// unclassified RECONCILE to total (perf-regression is its own bucket so nothing counted in total goes
/// unrepresented). ★ monitor-noise (the "cry wolf" signal) = flakyTransient + selectorDrift only —
/// real-outage / perf-regression / environment-regional are reds the monitor correctly caught, NOT noise.</summary>
public record TrustIncidentsDto(
    [property: JsonPropertyName("total")] long Total,
    [property: JsonPropertyName("realOutage")] long RealOutage,
    [property: JsonPropertyName("flakyTransient")] long FlakyTransient,
    [property: JsonPropertyName("selectorDrift")] long SelectorDrift,
    [property: JsonPropertyName("environmentRegional")] long EnvironmentRegional,
    [property: JsonPropertyName("perfRegression")] long PerfRegression,
    [property: JsonPropertyName("unclassified")] long Unclassified);

/// <summary>★ THE HONEST CONTRACT SLOT. Whether a HARNESS-CONFIRMED red-test (or an attested-manual record) is
/// recorded for this monitor (§D1 v2, runner migration 0057). captured=true ONLY when a red_tests row exists —
/// NEVER inferred from a fail run or RCA. When captured, testedAt + method are populated; when not, both are
/// null (the honest "not red-tested — not captured" slot, never a fabricated status). ADDITIVE: v1 shipped this
/// as {captured:false}; testedAt/method default null so the wire shape is unchanged for a not-captured monitor.</summary>
public record TrustRedTestDto(
    [property: JsonPropertyName("captured")] bool Captured,
    // method ∈ 'executed-red-fixture' | 'attested-manual' — rendered DISTINCTLY on the scorecard (executed =
    // an automated proof; attested = a human-recorded proof — the method distinction IS the honesty).
    [property: JsonPropertyName("testedAt")] DateTimeOffset? TestedAt = null,
    [property: JsonPropertyName("method")] string? Method = null);

/// <summary>★ B3-2 — the DISTINCT trust DIMENSIONS that replace the OR-collapsed chip's hidden internals. Each
/// axis carries its OWN state ∈ {ok, elevated, flaky} from a NAMED threshold (TrustReportProjection); the chip
/// is now a DERIVATION over these, and the scorecard surfaces WHICH dimension flags — never a lossy single
/// verdict. proven-live requires EVERY dimension `ok`; ANY dimension `flaky` ⇒ the chip is `flaky`. The numeric
/// value each dimension grades (flapRate / retryRate / the incident counts) already lives on the parent row.</summary>
public record TrustDimensionsDto(
    [property: JsonPropertyName("flap")] TrustDimensionDto Flap,
    [property: JsonPropertyName("retry")] TrustDimensionDto Retry,
    [property: JsonPropertyName("monitorNoise")] TrustDimensionDto MonitorNoise,
    // ★ B3-2 stage 2: monitor-side transients ÷ scheduled (the "cried wolf on a monitor-side red" axis 222
    // needed). ONLY monitor-side counts — a service-side transient (a real brief outage) never flags this.
    [property: JsonPropertyName("spuriousRed")] TrustDimensionDto SpuriousRed);

/// <summary>★ B3-2 stage 2 — the window's SUPERSEDED transients split by WHOSE FAULT (runner 0079). monitorSide
/// = the monitor cried wolf (feeds spuriousRed + burns B3-3's flake budget); serviceSide = a real brief outage
/// the monitor caught (must NOT penalise it); indeterminate = no signals to tell. spuriousRedRate = monitorSide
/// ÷ scheduled (null when no scheduled runs). indeterminate is surfaced so an operator can judge trust — a rate
/// over mostly-indeterminate data is not reliable.</summary>
public record TrustTransientsDto(
    [property: JsonPropertyName("monitorSide")] long MonitorSide,
    [property: JsonPropertyName("serviceSide")] long ServiceSide,
    [property: JsonPropertyName("indeterminate")] long Indeterminate,
    [property: JsonPropertyName("spuriousRedRate")] decimal? SpuriousRedRate);

/// <summary>One trust dimension's graded verdict. <c>state</c> ∈ {ok, elevated, flaky} for a measured axis, plus
/// two APPLICABILITY markers (not health verdicts) the client must render DISTINCTLY: <c>not-applicable</c> = the
/// axis is structurally dead for this monitor kind (no trace_signals — NEVER fills in); <c>no-data-yet</c> = a
/// measurable axis with too little history to certify (WILL fill in — "ask again later", not the same dead-end as
/// not-applicable). "ok" is reserved for MEASURED-and-fine — never emitted for either marker. See
/// TrustReportProjection for the exact per-axis thresholds + the applicability rules the legend renders.</summary>
public record TrustDimensionDto(
    [property: JsonPropertyName("state")] string State);

/// <summary>Spec-integrity provenance from the monitor's most recent run that carried it: the sha256 of the
/// assertion code that ACTUALLY executed + its spec path. A real integrity-of-execution fact ("the monitor
/// ran the committed, hash-pinned version") — NOT a red-test and NOT a headline score. Null when no run in
/// history carried provenance.</summary>
public record TrustProvenanceDto(
    [property: JsonPropertyName("executedSha256")] string? ExecutedSha256,
    [property: JsonPropertyName("specPath")] string? SpecPath);

/// <summary>★ B3-3 — the MONITOR trust budget. <c>consumed</c> = MONITOR-SIDE transients ONLY (the safety gate: a
/// service-side transient is a real, if brief, blip the monitor CAUGHT and must NEVER burn its budget); serviceSide
/// + indeterminate are surfaced, never consumed. <c>state</c> ∈ {ok, degraded-as-a-monitor} for a measured budget,
/// plus the same two applicability markers as a dimension (<c>not-applicable</c> = no trace_signals, the meter can
/// never move; <c>no-data-yet</c> = too few scheduled runs to be a verdict yet) — DELIBERATELY DISTINCT
/// from a service outage: this says "MY MONITOR is unreliable", a different problem with a different owner than
/// "Wegmans is down". <c>directedTask</c> (non-null only when degraded) names the failing dimension + the evidence
/// — a FIX task, NEVER a mute/auto-suppression (the flake budget has no write path to alerting). <c>targetIsDefault</c>
/// = the fleet default (2%) is in force. indeterminate is surfaced so a budget over partial data reads honestly.</summary>
public record TrustFlakeBudgetDto(
    [property: JsonPropertyName("target")] decimal Target,
    [property: JsonPropertyName("targetIsDefault")] bool TargetIsDefault,
    [property: JsonPropertyName("scheduledRuns")] long ScheduledRuns,
    [property: JsonPropertyName("monitorSide")] long MonitorSide,
    [property: JsonPropertyName("serviceSide")] long ServiceSide,
    [property: JsonPropertyName("indeterminate")] long Indeterminate,
    [property: JsonPropertyName("budget")] decimal Budget,
    [property: JsonPropertyName("consumed")] long Consumed,
    [property: JsonPropertyName("remaining")] decimal Remaining,
    [property: JsonPropertyName("remainingPct")] decimal? RemainingPct,
    [property: JsonPropertyName("burnRate")] decimal BurnRate,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("directedTask")] string? DirectedTask);

/// <summary>One monitor's trust row: measured facts + the derived chip. <c>lastGreenAt</c> null = NEVER
/// verified green (a first-class state). <c>retryRate</c> = retryCount/runCount, null when runCount = 0
/// (honest empty, not 0). <c>trust</c> ∈ {proven-live, flaky, unverified, nominal} — see TrustReportProjection
/// for the exact rules the chip legend renders.</summary>
public record TrustMonitorDto(
    [property: JsonPropertyName("checkId")] long CheckId,
    [property: JsonPropertyName("checkName")] string CheckName,
    [property: JsonPropertyName("sensitive")] bool Sensitive,
    [property: JsonPropertyName("lastGreenAt")] DateTimeOffset? LastGreenAt,
    [property: JsonPropertyName("lastRunAt")] DateTimeOffset? LastRunAt,
    [property: JsonPropertyName("runCount")] long RunCount,
    [property: JsonPropertyName("retryCount")] long RetryCount,
    [property: JsonPropertyName("retryRate")] decimal? RetryRate,
    // ★ "degrading-but-green" early warning: PASS/WARN runs that STILL needed a real retry over the window. A
    // DISPLAY-ONLY annotation — NOT an input to `trust` (DeriveChip). A proven-live monitor with retried passes
    // STAYS proven-live; this only flags "watch it, it's working harder to stay green".
    [property: JsonPropertyName("retriedPasses")] long RetriedPasses,
    // ★ Confirmation-retry P2 — flakiness surfaced: transient failures (superseded_by_run_id set, confirmed
    // not-real, excluded from health) ÷ scheduled (non-sandbox) runs. Raw counts + the rate so the UI can say
    // "6 transient failures in 142 runs (4.2%)". flapRate is null (never a fake 0) when scheduledCount == 0.
    [property: JsonPropertyName("flapCount")] long FlapCount,
    [property: JsonPropertyName("scheduledCount")] long ScheduledCount,
    [property: JsonPropertyName("flapRate")] decimal? FlapRate,
    // ★ B3-2 stage 2: the flap's transients split monitor-side / service-side / indeterminate + the spurious-red rate.
    [property: JsonPropertyName("transients")] TrustTransientsDto Transients,
    [property: JsonPropertyName("incidents")] TrustIncidentsDto Incidents,
    [property: JsonPropertyName("redTest")] TrustRedTestDto RedTest,
    [property: JsonPropertyName("specProvenance")] TrustProvenanceDto SpecProvenance,
    // ★ B3-2: the distinct per-dimension states (flap / retry / monitor-noise) — the SURFACED replacement for
    // the OR-collapse. The chip is derived FROM these; the scorecard shows which dimension flagged.
    [property: JsonPropertyName("dimensions")] TrustDimensionsDto Dimensions,
    // ★ B3-3: the MONITOR trust budget — "degraded as a monitor" + the directed fix task. Burns MONITOR-SIDE only.
    [property: JsonPropertyName("flakeBudget")] TrustFlakeBudgetDto FlakeBudget,
    [property: JsonPropertyName("trust")] string Trust);

/// <summary>GET /reports/trust — the fleet scorecard: one row per ENABLED check over the window.</summary>
public record TrustReportDto(
    [property: JsonPropertyName("window")] string Window,
    [property: JsonPropertyName("monitors")] IReadOnlyList<TrustMonitorDto> Monitors);

/// <summary>One day of the retry-rate trend for the detail sparkline. retryRate null when the day had no runs.</summary>
public record TrustRetryPointDto(
    [property: JsonPropertyName("day")] DateOnly Day,
    [property: JsonPropertyName("runCount")] long RunCount,
    [property: JsonPropertyName("retryCount")] long RetryCount,
    [property: JsonPropertyName("retryRate")] decimal? RetryRate);

/// <summary>GET /reports/trust/{checkId} — one monitor's trust row + its daily retry-rate series.</summary>
public record TrustMonitorDetailDto(
    [property: JsonPropertyName("window")] string Window,
    [property: JsonPropertyName("monitor")] TrustMonitorDto Monitor,
    [property: JsonPropertyName("retrySeries")] IReadOnlyList<TrustRetryPointDto> RetrySeries);
