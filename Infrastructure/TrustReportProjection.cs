using System.Globalization;
using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Dtos;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// §D1 Monitor-Trust Scorecard projection — turns raw <see cref="TrustMonitorRow"/> facts into the
/// <see cref="TrustMonitorDto"/> the scorecard renders. Extracted from the handler so the CHIP RULES are in
/// ONE legible, unit-testable place the dashboard can mirror as a legend.
///
/// ★ THE LOAD-BEARING DESIGN RULE — there is NO synthesized 0-100 trust score. The trust CHIP is derived
/// from STATED, AUDITABLE thresholds below; every other field is a measured fact carrying its own sample
/// size. A blended score would silently imply red-test coverage that does not exist (Signal 1 is uncaptured)
/// — the exact false-confidence this feature exists to kill.
///
/// ★ B3-2 — DISTINCT DIMENSIONS, NOT AN OR-COLLAPSE. The chip used to be a single verdict OR-ed over
/// independent, sometimes-CONTRADICTORY thresholds (retryRate ≥ 0.50 OR monitor-noise OR flapRate ≥ 0.10),
/// and that collapse LOST THE SIGNAL: a monitor flapping 6.25% read "proven live" (it missed the 10% cutoff),
/// and retryRate 11% vs flapRate 0% on the same monitor read as one flat "nominal". Now each axis is graded
/// on its OWN state ∈ {ok, elevated, flaky} from a NAMED threshold, and the chip is a DERIVATION over those
/// states that always surfaces WHICH dimension flagged (see <see cref="Dimensions"/>):
///   • flap    — superseded transients ÷ scheduled runs (confirmation-retry P2). Browser/multistep only —
///               http/dns/ssl produce no superseded runs, so their flap is honestly 0 (they flag on retry).
///   • retry   — runs needing a real retry (retry_count &gt; 1) ÷ runs. Covers ALL kinds.
///   • monitor-noise — RCA "cry wolf" incidents (flaky-transient + selector-drift). A COUNT, not a rate.
///
/// ★ THE CHIP RULES (evaluated top-to-bottom; first match wins — the precedence IS the contract):
///   1. "unverified"  — never green (lastGreenAt == null) OR no runs in the window (runCount == 0).
///                      No live evidence to trust; this truth dominates, so it is checked first.
///   2. "flaky"       — ANY dimension is `flaky` (flap ≥ 5% with ≥ 2 transients, OR retry ≥ 10%, OR any
///                      monitor-noise incident). The OR is now over NAMED, SURFACED dimensions — the chip
///                      names which one, it does not swallow it.
///   3. "proven-live" — last green within 2 check intervals AND EVERY dimension `ok` (no flaky, NO elevated).
///                      A green here is PROVEN trustworthy — recent real pass, clean on every axis.
///   4. "nominal"     — in between: green exists but stale, OR a dimension is `elevated` (above the fleet's
///                      well-behaved band, worth watching, not yet pathological).
///
/// ★ THE THRESHOLDS ARE DERIVED FROM THE MEASURED 30d FLEET DISTRIBUTION (2026-07-13), not round numbers —
/// "9.7% reading clean is a bug, not a philosophy". Justifications sit on each constant below.
///
/// ★ Honesty: retryRate/flapRate are null (never 0) when the denominator is 0; monitor-noise EXCLUDES
/// real-outage / perf-regression / environment-regional (those are reds the monitor correctly caught, not
/// noise); redTest.captured is a hard <c>false</c> (Signal 1 has no data — a visible v2 contract slot).
/// </summary>
public static class TrustReportProjection
{
    // ── Per-dimension thresholds (the auditable contract; the dashboard legend renders these verbatim) ──

    // FLAP — the fleet's non-zero flap rates were {0.05% (222: 2 flaps / 3777), 1.16% (395 canary: 1 flap),
    // 6.25% (355: 3 flaps / 48)}. 355 sits ~5× above the next. A 1% `elevated` floor puts any repeat-flapper
    // above the well-behaved band; a 5% `flaky` floor isolates 355. The ≥ 2 count floor keeps 395's lone
    // canary flap and 222's negligible 2-in-3777 out (one flap is noise; a pattern needs repetition).
    public const decimal FlapElevatedRate = 0.01m;
    public const decimal FlapFlakyRate = 0.05m;
    public const long FlapMinCount = 2;

    // RETRY — the fleet's retry rates cluster ≤ 0.6% (well-behaved), then jump to 3.9% (224), 6.4% (341),
    // 11.3% (342). A 2% `elevated` floor separates the clean cluster from that trio; a 10% `flaky` floor
    // isolates 342 (the only double-digit) and MATCHES the pre-existing proven-live boundary — retry < 10%
    // was already required for proven-live, so ≥ 10% was already "not proven-live"; now it is correctly
    // labelled flaky instead of silently "nominal". (The old 0.50 flaky floor was DEAD — nothing approached it.)
    public const decimal RetryElevatedRate = 0.02m;
    public const decimal RetryFlakyRate = 0.10m;

    // SPURIOUS-RED (B3-2 stage 2) — a MONITOR-SIDE transient (a superseded transient the monitor caused: no NEW
    // first-party service error, runner 0079) ÷ scheduled runs. This is the dimension 222's paint-race reds
    // needed — flap/retry/monitor-noise all read it clean. Same band as flap (elevated ≥ 1%, flaky ≥ 5%, ≥ 2)
    // because it measures the same "the monitor cried wolf" concept, just restricted to the proven-monitor-side
    // subset. ★ SERVICE-side + indeterminate transients are DELIBERATELY excluded — a service-side transient is
    // a real, if brief, outage the monitor CAUGHT; penalising the monitor for it would invert monitoring.
    public const decimal SpuriousRedElevatedRate = 0.01m;
    public const decimal SpuriousRedFlakyRate = 0.05m;
    public const long SpuriousRedMinCount = 2;

    // proven-live recency: the last green must be within this many check intervals (unchanged).
    public const int ProvenLiveMaxIntervalsSinceGreen = 2;

    public const string ChipProvenLive = "proven-live";
    public const string ChipFlaky = "flaky";
    public const string ChipUnverified = "unverified";
    public const string ChipNominal = "nominal";

    // Per-dimension states (worst-first: flaky > elevated > ok).
    public const string StateFlaky = "flaky";
    public const string StateElevated = "elevated";
    public const string StateOk = "ok";
    // ★ APPLICABILITY MARKER (not a health verdict): a dimension that CANNOT fire for this monitor's kind. A
    // spurious-red / flake-budget "ok" for an http/dns/ssl check is a LIE — those kinds capture NO trace_signals,
    // so their transients can only ever classify `indeterminate`, monitor_side is unreachable, and the dimension
    // is structurally dead. "not-applicable" is the API refusing to guess, not a green light. The API owns this
    // rule (it knows the kind); the dashboard must render it distinctly, never as a calm "ok".
    public const string StateNotApplicable = "not-applicable";

    // Kinds that DON'T capture trace_signals → spurious-red / flake-budget cannot classify a monitor-side
    // transient. Verified in prod: 0 of dns/http/ssl runs carry trace_signals. tcp/ping are network probes,
    // same story. Only browser/multistep (the trace-capturing kinds) can produce a monitor-side red — an
    // UNKNOWN/new kind defaults to APPLICABLE (show the real state) so we never silently mute a live signal.
    private static readonly HashSet<string> NonTraceSignalKinds =
        new(StringComparer.OrdinalIgnoreCase) { "http", "dns", "ssl", "tcp", "ping" };

    /// <summary>Whether a trace-signal-derived dimension (spurious-red, the flake budget) CAN fire for this
    /// kind. False for http/dns/ssl/tcp/ping (no trace_signals ⇒ monitor_side unreachable ⇒ structurally dead).</summary>
    public static bool TraceSignalDimensionsApply(string? kind) => !NonTraceSignalKinds.Contains(kind ?? "");

    // ★ B3-3 flake-budget states — DELIBERATELY DISTINCT idiom from a service outage: "degraded as a MONITOR"
    // (my monitor is unreliable) vs "the SERVICE is down". Different problems, different owners.
    public const string FlakeBudgetOk = "ok";
    public const string FlakeBudgetDegraded = "degraded-as-a-monitor";

    /// <summary>Monitor-noise = the "cry wolf" verdicts (monitor bugs), NOT reds the monitor correctly caught.</summary>
    public static long MonitorNoise(TrustMonitorRow r) => r.FlakyTransient + r.SelectorDrift;

    /// <summary>flapRate = flapCount / scheduledCount (superseded transients ÷ non-sandbox runs); null when
    /// scheduledCount == 0 (honest empty, never a fake 0).</summary>
    public static decimal? FlapRate(long flapCount, long scheduledCount) =>
        scheduledCount > 0 ? Math.Round((decimal)flapCount / scheduledCount, 4) : null;

    /// <summary>retryRate = retryCount / runCount; null when runCount == 0 (honest empty, never a fake 0).</summary>
    public static decimal? RetryRate(long retryCount, long runCount) =>
        runCount > 0 ? Math.Round((decimal)retryCount / runCount, 4) : null;

    /// <summary>spuriousRedRate = MONITOR-SIDE transients ÷ scheduledCount (service-side + indeterminate are
    /// NOT in the numerator — only proven monitor-caused reds). null when scheduledCount == 0.</summary>
    public static decimal? SpuriousRedRate(long monitorSideTransients, long scheduledCount) =>
        scheduledCount > 0 ? Math.Round((decimal)monitorSideTransients / scheduledCount, 4) : null;

    // ── The three dimension states (each graded on its OWN axis from the named thresholds above) ──

    /// <summary>flap dimension: ok below the count floor / band, `elevated` in [1%, 5%), `flaky` at ≥ 5% —
    /// both gated on ≥ 2 transient failures (one flap is noise). null denominator → ok (nothing to grade).</summary>
    public static string FlapState(TrustMonitorRow r)
    {
        if (r.FlapCount < FlapMinCount) return StateOk;                 // a pattern needs ≥ 2 (395's lone flap = ok)
        if (FlapRate(r.FlapCount, r.ScheduledCount) is not decimal fr) return StateOk;
        if (fr >= FlapFlakyRate) return StateFlaky;
        if (fr >= FlapElevatedRate) return StateElevated;
        return StateOk;
    }

    /// <summary>retry dimension: `elevated` in [2%, 10%), `flaky` at ≥ 10%. null denominator → ok (no runs;
    /// the `unverified` chip rule handles the no-evidence case first).</summary>
    public static string RetryState(TrustMonitorRow r)
    {
        if (RetryRate(r.RetryCount, r.RunCount) is not decimal rr) return StateOk;
        if (rr >= RetryFlakyRate) return StateFlaky;
        if (rr >= RetryElevatedRate) return StateElevated;
        return StateOk;
    }

    /// <summary>monitor-noise dimension: any "cry wolf" incident (flaky-transient + selector-drift &gt; 0) is a
    /// flag — a COUNT, not a rate (a single monitor-bug incident is already a real false alarm).</summary>
    public static string MonitorNoiseState(TrustMonitorRow r) =>
        MonitorNoise(r) > 0 ? StateFlaky : StateOk;

    /// <summary>spurious-red dimension (B3-2 stage 2): monitor-side transients ÷ scheduled — `elevated` in
    /// [1%, 5%), `flaky` at ≥ 5%, gated on ≥ 2 monitor-side transients. ★ ONLY monitor-side counts; a
    /// service-side transient (a real brief outage the monitor caught) NEVER flags this. null denom → ok.</summary>
    public static string SpuriousRedState(TrustMonitorRow r)
    {
        if (r.MonitorSideTransients < SpuriousRedMinCount) return StateOk;   // one is noise; a pattern needs ≥ 2
        if (SpuriousRedRate(r.MonitorSideTransients, r.ScheduledCount) is not decimal sr) return StateOk;
        if (sr >= SpuriousRedFlakyRate) return StateFlaky;
        if (sr >= SpuriousRedElevatedRate) return StateElevated;
        return StateOk;
    }

    /// <summary>The graded dimensions for the row — the SURFACED replacement for the OR-collapse. The UI pairs
    /// each state with the numeric value that already lives on the row (flapRate / retryRate / the incident +
    /// transient counts).</summary>
    public static TrustDimensionsDto Dimensions(TrustMonitorRow r) => new(
        Flap: new TrustDimensionDto(FlapState(r)),
        Retry: new TrustDimensionDto(RetryState(r)),
        MonitorNoise: new TrustDimensionDto(MonitorNoiseState(r)),
        // ★ spurious-red is trace_signals-derived → NOT-APPLICABLE for kinds that carry none (http/dns/ssl/tcp/
        // ping): reporting "ok" there is a lie (the dimension is structurally dead, not clean). DeriveChip is
        // UNCHANGED — it consumes the raw SpuriousRedState (which is "ok" for those kinds since monitor_side is
        // 0), so a clean http check is still proven-live; only the SURFACED dimension marker changes.
        SpuriousRed: new TrustDimensionDto(
            TraceSignalDimensionsApply(r.Kind) ? SpuriousRedState(r) : StateNotApplicable));

    /// <summary>★★ THE MONITOR TRUST BUDGET STATE. "degraded-as-a-monitor" when the monitor has spent MORE than
    /// its budget on MONITOR-SIDE transients — i.e. <c>FlakeConsumed</c> (monitor-side ONLY, from flake_status'
    /// gated SQL) exceeds <c>FlakeBudget</c> (= target × scheduled). Gated on ≥ 2 (a lone spurious red is noise,
    /// mirroring SpuriousRed). ★ SERVICE-side + indeterminate transients are NOT in FlakeConsumed, so a monitor
    /// that flaps only because the SERVICE is flaky (355) NEVER goes degraded — that is the safety property.</summary>
    public static string FlakeBudgetState(TrustMonitorRow r)
    {
        if (r.FlakeConsumed < SpuriousRedMinCount) return FlakeBudgetOk;    // a lone monitor-side red is noise
        return r.FlakeConsumed > r.FlakeBudget ? FlakeBudgetDegraded : FlakeBudgetOk;
    }

    /// <summary>★ THE DIRECTED FIX TASK (never a mute). Non-null ONLY when the budget is degraded. It names the
    /// failing DIMENSION and the EVIDENCE so an operator can act — e.g. "222: spurious-red 4.1% (budget 2%) — 2
    /// monitor-side reds with no new first-party service error over 49 scheduled runs". It is a plain string
    /// surfaced on the monitor; SynthWatch has no monitor-owner concept, so ROUTING is a separate decision — this
    /// deliberately does NOT auto-suppress, auto-mute, or touch alert routing.</summary>
    public static string? FlakeDirectedTask(TrustMonitorRow r)
    {
        if (FlakeBudgetState(r) != FlakeBudgetDegraded) return null;
        var ratePct = SpuriousRedRate(r.FlakeConsumed, r.FlakeScheduledRuns) is decimal sr
            ? (sr * 100m).ToString("0.#", CultureInfo.InvariantCulture)
            : "n/a";
        var budgetPct = (r.FlakeTarget * 100m).ToString("0.#", CultureInfo.InvariantCulture);
        return $"{r.CheckName}: spurious-red {ratePct}% (budget {budgetPct}%) — {r.FlakeConsumed} monitor-side "
             + $"red(s) with no new first-party service error over {r.FlakeScheduledRuns} scheduled runs. "
             + "Fix the flaky assertion/selector — this is a MONITOR problem, not a service outage.";
    }

    /// <summary>The MONITOR trust budget DTO — consumed = monitor-side ONLY; service/indeterminate surfaced,
    /// never consumed; the directed task (null unless degraded).</summary>
    public static TrustFlakeBudgetDto FlakeBudget(TrustMonitorRow r) => new(
        Target: r.FlakeTarget,
        TargetIsDefault: r.FlakeTargetIsDefault,
        ScheduledRuns: r.FlakeScheduledRuns,
        MonitorSide: r.MonitorSideTransients,
        ServiceSide: r.ServiceSideTransients,
        Indeterminate: r.IndeterminateTransients,
        Budget: r.FlakeBudget,
        Consumed: r.FlakeConsumed,
        Remaining: r.FlakeRemaining,
        RemainingPct: r.FlakeRemainingPct,
        BurnRate: r.FlakeBurnRate,
        // ★ A budget that CANNOT be consumed is not a healthy budget. For kinds with no trace_signals the
        // consumed can only ever be 0 (monitor_side unreachable), so a "0% consumed → ok" reads as a perfect
        // score for a meter that will never move. Mark it not-applicable instead of vacuously-green.
        State: TraceSignalDimensionsApply(r.Kind) ? FlakeBudgetState(r) : StateNotApplicable,
        DirectedTask: FlakeDirectedTask(r));

    /// <summary>
    /// The chip, DERIVED from the per-dimension states above. <paramref name="asOf"/> is the reference clock
    /// for the "within 2 intervals" recency test (pass DateTimeOffset.UtcNow from the handler; a fixed value
    /// in tests).
    /// </summary>
    public static string DeriveChip(TrustMonitorRow r, DateTimeOffset asOf)
    {
        // 1. unverified — no live evidence at all (dominates; checked first).
        if (r.LastGreenAt is null || r.RunCount == 0)
            return ChipUnverified;

        var flap = FlapState(r);
        var retry = RetryState(r);
        var noise = MonitorNoiseState(r);
        var spurious = SpuriousRedState(r);

        // 2. flaky — ANY dimension is pathological. The OR now names WHICH dimension (Dimensions), never hides it.
        if (flap == StateFlaky || retry == StateFlaky || noise == StateFlaky || spurious == StateFlaky)
            return ChipFlaky;

        // 3. proven-live — recent real green AND EVERY dimension clean (no `elevated`, no `flaky`). A monitor
        //    flapping 6.25% (flap `flaky`), retrying 6% (retry `elevated`), or crying wolf on monitor-side reds
        //    (spurious-red `flaky`) can NO LONGER read proven-live.
        var freshCutoff = asOf - TimeSpan.FromSeconds((double)r.IntervalSeconds * ProvenLiveMaxIntervalsSinceGreen);
        var greenIsFresh = r.LastGreenAt.Value >= freshCutoff;
        if (greenIsFresh && flap == StateOk && retry == StateOk && noise == StateOk && spurious == StateOk)
            return ChipProvenLive;

        // 4. nominal — green exists but stale, OR a dimension is `elevated` (watch it, not yet flaky).
        return ChipNominal;
    }

    public static TrustMonitorDto ToDto(TrustMonitorRow r, DateTimeOffset asOf) => new(
        CheckId: r.CheckId,
        CheckName: r.CheckName,
        Sensitive: r.Sensitive,
        LastGreenAt: r.LastGreenAt,
        LastRunAt: r.LastRunAt,
        RunCount: r.RunCount,
        RetryCount: r.RetryCount,
        RetryRate: RetryRate(r.RetryCount, r.RunCount),
        // ★ Display-only annotation — carried straight from the row, NEVER routed through DeriveChip.
        RetriedPasses: r.RetriedPasses,
        // ★ Confirmation-retry P2: surface the flap counts + rate (transient failures ÷ scheduled runs). Raw
        // counts too, so the UI can say "6 transient failures in 142 runs". The flap DIMENSION (not this raw
        // field) feeds the chip — a repeated flap is real flakiness, surfaced by name.
        FlapCount: r.FlapCount,
        ScheduledCount: r.ScheduledCount,
        FlapRate: FlapRate(r.FlapCount, r.ScheduledCount),
        // ★ B3-2 stage 2: the flap's transients split by whose fault + the spurious-red rate (monitor-side ÷
        // scheduled). SpuriousRedRate + the counts let the UI say "222: spurious-red 4.1% (2 monitor-side, 1
        // service-side, 3 indeterminate)". ★ indeterminate is surfaced so an operator sees how much is
        // UNCLASSIFIED — a spurious-red rate over mostly-indeterminate data is not trustworthy, and the UI says so.
        Transients: new TrustTransientsDto(
            MonitorSide: r.MonitorSideTransients,
            ServiceSide: r.ServiceSideTransients,
            Indeterminate: r.IndeterminateTransients,
            SpuriousRedRate: SpuriousRedRate(r.MonitorSideTransients, r.ScheduledCount)),
        Incidents: new TrustIncidentsDto(
            Total: r.IncidentTotal,
            RealOutage: r.RealOutage,
            FlakyTransient: r.FlakyTransient,
            SelectorDrift: r.SelectorDrift,
            EnvironmentRegional: r.EnvironmentRegional,
            PerfRegression: r.PerfRegression,
            Unclassified: r.Unclassified),
        // ★ Signal 1 (§D1 v2, 0057): captured=true ONLY when a harness-confirmed red_tests row exists (the SQL
        // takes the latest outcome='red' per check). No row → the honest {captured:false, testedAt:null,
        // method:null}. NEVER inferred from a fail run / RCA — a red_tests row is the only backing.
        RedTest: r.RedTestedAt is DateTimeOffset testedAt
            ? new TrustRedTestDto(Captured: true, TestedAt: testedAt, Method: r.RedTestMethod)
            : new TrustRedTestDto(Captured: false),
        SpecProvenance: new TrustProvenanceDto(r.ExecutedSha256, r.SpecPath),
        // ★ B3-2: the distinct dimension states — the SURFACED replacement for the OR-collapse.
        Dimensions: Dimensions(r),
        // ★ B3-3: the MONITOR trust budget (burns monitor-side ONLY) + the directed fix task. NOT an input to the
        // chip / not routed anywhere — a reporting field with a task, never a mute.
        FlakeBudget: FlakeBudget(r),
        Trust: DeriveChip(r, asOf));
}
