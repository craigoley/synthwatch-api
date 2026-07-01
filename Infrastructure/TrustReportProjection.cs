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
/// ★ THE CHIP RULES (evaluated top-to-bottom; first match wins — the precedence IS the contract):
///   1. "unverified"  — never green (lastGreenAt == null) OR no runs in the window (runCount == 0).
///                      No live evidence to trust; this truth dominates, so it is checked first.
///   2. "flaky"       — retryRate ≥ 0.50 OR any monitor-noise incident (flakyTransient + selectorDrift &gt; 0).
///                      The monitor limps over the line or has cried wolf; trust is degraded.
///   3. "proven-live" — last green within 2 check intervals AND retryRate &lt; 0.10 AND zero monitor-noise.
///                      A green here is PROVEN trustworthy — recent real pass, clean, no false alarms.
///   4. "nominal"     — everything in between (green exists but stale, or 0.10 ≤ retryRate &lt; 0.50), no noise.
///
/// ★ Honesty: retryRate is null (never 0) when runCount == 0; monitor-noise EXCLUDES real-outage /
/// perf-regression / environment-regional (those are reds the monitor correctly caught, not noise);
/// redTest.captured is a hard <c>false</c> (Signal 1 has no data — a visible v2 contract slot, never faked).
/// </summary>
public static class TrustReportProjection
{
    // ── The chip thresholds (the auditable contract; the dashboard legend renders these verbatim) ──
    public const decimal ProvenLiveMaxRetryRate = 0.10m;
    public const decimal FlakyMinRetryRate = 0.50m;
    public const int ProvenLiveMaxIntervalsSinceGreen = 2;

    public const string ChipProvenLive = "proven-live";
    public const string ChipFlaky = "flaky";
    public const string ChipUnverified = "unverified";
    public const string ChipNominal = "nominal";

    /// <summary>Monitor-noise = the "cry wolf" verdicts (monitor bugs), NOT reds the monitor correctly caught.</summary>
    public static long MonitorNoise(TrustMonitorRow r) => r.FlakyTransient + r.SelectorDrift;

    /// <summary>retryRate = retryCount / runCount; null when runCount == 0 (honest empty, never a fake 0).</summary>
    public static decimal? RetryRate(long retryCount, long runCount) =>
        runCount > 0 ? Math.Round((decimal)retryCount / runCount, 4) : null;

    /// <summary>
    /// The chip, from the stated rules above. <paramref name="asOf"/> is the reference clock for the
    /// "within 2 intervals" recency test (pass DateTimeOffset.UtcNow from the handler; a fixed value in tests).
    /// </summary>
    public static string DeriveChip(TrustMonitorRow r, DateTimeOffset asOf)
    {
        // 1. unverified — no live evidence at all.
        if (r.LastGreenAt is null || r.RunCount == 0)
            return ChipUnverified;

        var retryRate = RetryRate(r.RetryCount, r.RunCount);
        var monitorNoise = MonitorNoise(r);

        // 2. flaky — limping over the line, or has cried wolf.
        if ((retryRate is decimal rr && rr >= FlakyMinRetryRate) || monitorNoise > 0)
            return ChipFlaky;

        // 3. proven-live — recent real pass, clean, no false alarms.
        var freshCutoff = asOf - TimeSpan.FromSeconds((double)r.IntervalSeconds * ProvenLiveMaxIntervalsSinceGreen);
        var greenIsFresh = r.LastGreenAt.Value >= freshCutoff;
        if (greenIsFresh && retryRate is decimal rr2 && rr2 < ProvenLiveMaxRetryRate && monitorNoise == 0)
            return ChipProvenLive;

        // 4. nominal — in between.
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
        Trust: DeriveChip(r, asOf));
}
