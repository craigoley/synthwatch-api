namespace SynthWatch.Api.Dtos;

/// <summary>The failing run being explained.</summary>
public sealed record DiffRunRef(long RunId, string? Location, string Status);

/// <summary>The known-good run the failure is compared against (the check's success-trace baseline).</summary>
public sealed record DiffBaselineRef(string Source, DateTimeOffset? CapturedAt, string? Location);

/// <summary>
/// The AOAI comparison over the DELTA — categorized toward the regional-cause taxonomy, with the honest
/// flakiness call. <c>Findings</c> reuse <see cref="AiInsightDto"/> so the dashboard's insight-card renders them.
/// </summary>
public sealed record DiffInsight(
    string Summary,
    // ★ The PRIMARY classification: which LAYER failed.
    // site-failure | monitor-verification-bug | transient | undetermined
    string Verdict,
    // Finer cause (kept for the regional taxonomy + the dashboard card):
    // site-failure | monitor-verification-bug | regional-waf-cdn | network-allowlist | geo-dns | region-timeout
    // | third-party-blocked | flaky-transient | undetermined
    string LikelyCause,
    string Confidence,
    bool IsFlaky,
    IReadOnlyList<AiInsightDto> Findings,
    IReadOnlyList<string> Caveats);

/// <summary>
/// "Why does this run fail when the last-known-good baseline passed?" — the diff (always present, even with
/// AOAI off) + the AI comparison (when configured). ★ Honest framing: it compares the failing run vs the
/// monitor's last-known-good BASELINE, NOT directly vs the passing location (passing runs have no trace).
/// <c>Configured</c>/<c>Note</c>/<c>Retryable</c> mirror the ai-insights states (#96/#104).
/// </summary>
public sealed record LocationDiffDto(
    bool Configured,
    string? Note,
    bool Retryable,
    DiffRunRef Failing,
    DiffBaselineRef Baseline,
    TraceDiffDto Diff,
    DiffInsight? Insight)
{
    public static LocationDiffDto NotConfigured(DiffRunRef f, DiffBaselineRef b, TraceDiffDto d) =>
        new(false, "AI insights are not configured for this environment yet.", false, f, b, d, null);

    public static LocationDiffDto Unavailable(DiffRunRef f, DiffBaselineRef b, TraceDiffDto d, string note, bool retryable) =>
        new(true, note, retryable, f, b, d, null);

    public static LocationDiffDto Ok(DiffRunRef f, DiffBaselineRef b, TraceDiffDto d, DiffInsight insight) =>
        new(true, null, false, f, b, d, insight);
}
