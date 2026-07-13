namespace SynthWatch.Api.Dtos;

/// <summary>
/// Error-diff (P2) for GET /api/checks/{id}/error-diff: the errors this run has that are NEW vs the last-N
/// settled runs, still PERSISTENT, or RESOLVED since. Computed entirely from persisted <c>runs.trace_signals</c>
/// (no zip re-parse), against a LAST-N baseline (anti-flap: a one-off blip in a single baseline run is not NEW).
/// NEW is the headline; each item carries a severity so the consumer ranks likely-real first-party regressions
/// above third-party noise, and an origin so it can default to first-party.
/// </summary>
public sealed record ErrorDiffDto(
    long CheckId,
    long RunId,
    System.DateTimeOffset RunStartedAt,
    string? Location,
    IReadOnlyList<long> BaselineRunIds,
    IReadOnlyList<ErrorItemDto> New,
    IReadOnlyList<ErrorItemDto> Persistent,
    IReadOnlyList<ErrorItemDto> Resolved,
    ErrorDiffCountsDto Counts,
    // ★ TRUNCATION HONESTY: trace_signals is a capped top-N summary (console cap; DroppedError counts errors
    // dropped by it). True when THIS run OR any baseline run hit the cap — the diff is over an incomplete set,
    // so the UI must be able to say so rather than imply completeness.
    bool Truncated,
    // N actually used (may be < the configured baseline size on a young check / after a location change).
    int BaselineRunCount,
    // ★ P4 MUTE (never silently drop): errors that WOULD be NEW but the operator has muted for this check. They
    // are REMOVED from New (so the panel stays must-go-red for real regressions) and surfaced HERE instead — the
    // UI shows a collapsed "N muted" disclosure with an unmute action. A muted error that is instead PERSISTENT
    // stays in Persistent (mute only intercepts the would-be-NEW signal). Default [] (no mutes / older callers).
    IReadOnlyList<ErrorItemDto>? Muted = null,
    // ★ TRUNCATION, BY CLASS (makes `Truncated` informative instead of just scary). FirstPartyTruncated = the
    // cap dropped a FIRST-PARTY message on this run or a baseline — the diff may have lost real signal, so the
    // UI stays LOUD. When Truncated but NOT FirstPartyTruncated, only tracker noise was dropped and the UI can
    // say "N third-party dropped — first-party capture is complete" (DroppedThirdParty = the target run's count).
    bool FirstPartyTruncated = false,
    int DroppedThirdParty = 0);

/// <summary>One error in the diff — a stable per-error identity + its classification and severity.</summary>
public sealed record ErrorItemDto(
    // {console|net} + level/status-class + origin + sourceHost + canonical(text|url) — stable across runs.
    string Fingerprint,
    // net-5xx | net-4xx | net-abort | console-error | pageerror | csp | warning
    string Kind,
    // first-party | third-party (from the P1 allowlist classifier) — the consumer defaults to first-party.
    string Origin,
    // console level (error/warning/pageerror); null for a network error.
    string? Level,
    // http status for a network error (or -1/0 for an abort); null for a console error.
    int? Status,
    string SourceHost,
    // canonical text (console) or canonical url (network) — the volatile parts already stripped.
    string Message,
    // occurrences folded into this fingerprint within its run (the runner already deduped near-identical text).
    int Count,
    // higher = likelier a real first-party regression: fp-5xx(6) > fp-4xx(5) > fp-error/pageerror(4) >
    // abort(3) > csp/warning(2) > ANY third-party(1). The consumer sorts NEW by this.
    int Severity,
    string SeverityLabel,
    // set to RunId for a NEW error (it debuts this run — cheap); null otherwise. Full first-seen history is future.
    long? FirstSeenRunId,
    // ★ P4 DEPLOY CORRELATION: the deploy (if any) that landed in the window between the previous settled run
    // (which LACKED this fingerprint) and this run — i.e. the deploy this NEW error first appeared after. Null
    // when no deploy landed in that window, when the check has no prior baseline run to bound the window, or when
    // the deploys table isn't populated. Set only on NEW items (a would-be-NEW error's debut) — never causation,
    // a correlation the UI renders as "first seen after deploy abc1234 · 2h ago". Default null (older callers).
    FirstSeenAfterDeployDto? FirstSeenAfterDeploy = null);

/// <summary>The deploy a NEW error first appeared after (P4 correlation) — the marker the runner auto-detected
/// on the check's host, inside the inter-run window. sha is empty for a non-SHA marker (etag/sentry-release);
/// deployedAt is the deploy's own time (not detection time). Correlation, never causation.</summary>
public sealed record FirstSeenAfterDeployDto(
    string Sha,
    System.DateTimeOffset DeployedAt,
    string TargetHost);

/// <summary>First/third-party splits per bucket, so the UI can render e.g. "3 new — and 7 third-party".</summary>
public sealed record ErrorDiffCountsDto(
    int NewFirstParty, int NewThirdParty,
    int PersistentFirstParty, int PersistentThirdParty,
    int ResolvedFirstParty, int ResolvedThirdParty,
    // ★ P4: errors muted for this check that would otherwise be NEW (surfaced in the Muted bucket, not New).
    // Default 0 so existing positional constructions in ErrorDiff.Compute stay valid until updated.
    int Muted = 0);
