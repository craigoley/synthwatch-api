namespace SynthWatch.Api.Dtos;

/// <summary>
/// The compact, FILTERED summary extracted from a run's Playwright trace zip — a few hundred tokens, NOT the
/// ~18 MB trace. Independently useful (network waterfall + real site console errors), and the bounded payload
/// slice 2 will hand to gpt-5-mini. Ports docs/proposals/prototype/extract_trace.py (proven on real run 844486).
/// </summary>
public sealed record TraceSignalsDto(string? TargetHost, NetworkSummaryDto Network, ConsoleSummaryDto Console)
{
    /// <summary>No trace / unparseable → an empty (but well-shaped) summary, never a 500.</summary>
    public static readonly TraceSignalsDto Empty = new(null, NetworkSummaryDto.Empty, ConsoleSummaryDto.Empty);
}

/// <summary>One request from the trace's HAR-shaped network log (slimmed to the fields the signals need).</summary>
public sealed record TraceRequestDto(
    string Url, int Status, string ResourceType, int TimeMs, int WaitMs,
    long Size, long Wire, string Encoding, bool ThirdParty);

/// <summary>A third-party origin's footprint on the page (request count + bytes on the wire).</summary>
public sealed record ThirdPartyDto(string Host, int Count, long Kb);

/// <summary>
/// A MUTATING request (POST/PUT/PATCH/DELETE) and the status the site returned — i.e. what actually happened to
/// "the action under test" (add-to-cart, sign-in, submit). The single most decisive RCA signal: a 2xx here means
/// the action SUCCEEDED, so an assertion failure on top of it points at the MONITOR's verification, not the site.
/// </summary>
public sealed record MutationDto(string Method, string Url, int Status);

public sealed record NetworkSummaryDto(
    int TotalRequests, long WireKb, int ThirdPartyCount,
    IReadOnlyList<TraceRequestDto> Failed,
    IReadOnlyList<TraceRequestDto> Slowest,
    IReadOnlyList<TraceRequestDto> Largest,
    IReadOnlyList<TraceRequestDto> Uncompressed,
    IReadOnlyList<ThirdPartyDto> TopThirdParties,
    IReadOnlyList<MutationDto> Mutations)
{
    public static readonly NetworkSummaryDto Empty = new(0, 0, 0, [], [], [], [], [], []);
}

/// <summary>A kept console message: error/warning only, extension-noise filtered, tagged site vs third-party.
/// SourceHost (Error-diff P1) = the host of the RESOURCE the error is about (from the first URL in the text,
/// else the logging frame) — it drives Origin (via the first-party allowlist) and is part of the per-error
/// diff fingerprint (TraceSignalsDiff). "" when no host is derivable.</summary>
public sealed record ConsoleMessageDto(string Level, string Origin, string SourceHost, string Text);

public sealed record ConsoleSummaryDto(
    // ★ DroppedError = error-class messages (error/warning/pageerror) dropped by the MaxConsoleMessages cap.
    // info/log chatter is excluded up front (DroppedInfoLog) so an error is NEVER dropped for an info log;
    // this makes the remaining truncation (errors beyond the cap) HONEST instead of silent.
    IReadOnlyList<ConsoleMessageDto> Messages, int DroppedInfoLog, int DroppedExtensionNoise, int DroppedError,
    // ★ DroppedError SPLIT by first-party-ness (DroppedThirdParty + DroppedFirstParty == DroppedError). The
    // capture drop-policy ranks first-party above third-party, so DroppedFirstParty > 0 ONLY when first-party
    // alone overflowed the cap — the case that actually threatens the diff. Optional: older persisted rows (no
    // split) deserialize these as 0, which reads as "no first-party loss known" — the calm default.
    int DroppedThirdParty = 0, int DroppedFirstParty = 0)
{
    public static readonly ConsoleSummaryDto Empty = new([], 0, 0, 0);
}
