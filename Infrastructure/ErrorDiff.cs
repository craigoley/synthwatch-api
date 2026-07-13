using System.Text.RegularExpressions;
using SynthWatch.Api.Dtos;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// The PURE error-diff computer (P2): given this run's trace_signals + the last-N settled runs' signals, split
/// the run's errors into NEW / PERSISTENT / RESOLVED. No DB, no zip — unit-testable in isolation.
///
/// ★ ANTI-FLAP: the baseline is the UNION of fingerprints across the last N settled runs, NOT just the previous
/// run. A transient third-party blip that appeared in ONE recent run is therefore in the baseline and is NOT
/// re-reported as NEW every other run. (A last-run-only baseline would flap — the must-go-red test pins this.)
///
/// ★ Fingerprint reuses <see cref="TraceSignalsDiff.Canonicalize"/> (the P1 stable signature: strip timestamps
/// / query strings / long id-hash tokens) plus the P1 origin + sourceHost, so the SAME error matches across
/// runs while a same-text error at a different host/origin stays distinct.
/// </summary>
public static partial class ErrorDiff
{
    /// <summary>Baseline size (N) — last-N SETTLED runs. Named so the anti-flap window is one obvious knob.</summary>
    public const int BaselineRuns = 4;

    /// <summary>Shared empty mute set — the default when a caller passes no mutes (older callers + unit tests).</summary>
    private static readonly IReadOnlySet<string> EmptyFingerprints = new HashSet<string>(StringComparer.Ordinal);

    [GeneratedRegex(@"content security policy|violates the following.*directive|refused to (load|execute|apply|connect|frame)",
        RegexOptions.IgnoreCase)]
    private static partial Regex CspText();

    /// <summary>A run's signals for the diff: its id + parsed signals (null = no/unparseable signals → empty
    /// error set, excluded from the baseline union but never a 500) + whether its console set was truncated.</summary>
    public sealed record RunSignals(long RunId, TraceSignalsDto? Signals, bool Truncated);

    public static ErrorDiffDto Compute(
        long checkId, long runId, DateTimeOffset runStartedAt, string? location,
        RunSignals target, IReadOnlyList<RunSignals> baseline,
        // ★ P4 MUTE: fingerprints the operator has muted for this check. A would-be-NEW error whose fingerprint is
        // here is diverted to the Muted bucket instead of New (never silently dropped). Pure — just a set; the DB
        // load happens in the endpoint. Null/empty = no mutes (older callers + the unit tests are unaffected).
        IReadOnlySet<string>? mutedFingerprints = null)
    {
        var muted = mutedFingerprints ?? EmptyFingerprints;
        var thisRun = Fingerprint(target.Signals);

        // Baseline = the UNION of fingerprints across ALL baseline runs (anti-flap). Keep a representative item
        // per fingerprint (newest baseline run wins, since callers pass baseline newest-first) for RESOLVED.
        var baselineFps = new HashSet<string>(StringComparer.Ordinal);
        var baselineReps = new Dictionary<string, ErrorItemDto>(StringComparer.Ordinal);
        foreach (var b in baseline)
        {
            foreach (var (fp, item) in Fingerprint(b.Signals))
            {
                baselineFps.Add(fp);
                baselineReps.TryAdd(fp, item);
            }
        }

        var @new = new List<ErrorItemDto>();
        var persistent = new List<ErrorItemDto>();
        var mutedItems = new List<ErrorItemDto>();
        foreach (var (fp, item) in thisRun)
        {
            if (baselineFps.Contains(fp))
                persistent.Add(item);            // already known (in the baseline union) — mute doesn't touch it
            else if (muted.Contains(fp))
                mutedItems.Add(item with { FirstSeenRunId = runId }); // would-be-NEW, but muted → out of New
            else
                @new.Add(item with { FirstSeenRunId = runId }); // debuts THIS run
        }

        var resolved = baselineReps
            .Where(kv => !thisRun.ContainsKey(kv.Key))
            .Select(kv => kv.Value)
            .ToList();

        // Sort every bucket most-severe-first (then by count) so the top is the likeliest real regression.
        @new.Sort(Rank);
        persistent.Sort(Rank);
        resolved.Sort(Rank);
        mutedItems.Sort(Rank);

        var counts = new ErrorDiffCountsDto(
            NewFirstParty: CountFp(@new, first: true), NewThirdParty: CountFp(@new, first: false),
            PersistentFirstParty: CountFp(persistent, first: true), PersistentThirdParty: CountFp(persistent, first: false),
            ResolvedFirstParty: CountFp(resolved, first: true), ResolvedThirdParty: CountFp(resolved, first: false),
            Muted: mutedItems.Count);

        var truncated = target.Truncated || baseline.Any(b => b.Truncated);
        // ★ Split the truncation signal by owner so the UI can be HONEST *and* INFORMATIVE. First-party
        // truncation (first-party signal lost) is the case that actually threatens the diff and must stay LOUD;
        // third-party-only truncation (tracker noise dropped) is benign — the panel can say "first-party
        // complete". DroppedThirdParty is the TARGET run's count (the run being viewed) for the panel's "N".
        var firstPartyTruncated =
            IsFirstPartyTruncated(target.Signals) || baseline.Any(b => IsFirstPartyTruncated(b.Signals));
        var droppedThirdParty = target.Signals?.Console?.DroppedThirdParty ?? 0;

        return new ErrorDiffDto(
            checkId, runId, runStartedAt, location,
            BaselineRunIds: baseline.Select(b => b.RunId).ToList(),
            New: @new, Persistent: persistent, Resolved: resolved,
            Counts: counts, Truncated: truncated, BaselineRunCount: baseline.Count,
            Muted: mutedItems, FirstPartyTruncated: firstPartyTruncated, DroppedThirdParty: droppedThirdParty);
    }

    /// <summary>True when a run's console set was truncated by the cap (errors beyond it were dropped).</summary>
    public static bool IsTruncated(TraceSignalsDto? s) => (s?.Console?.DroppedError ?? 0) > 0;

    /// <summary>True when the cap dropped a FIRST-PARTY message (not just tracker noise) — the LOUD case. The
    /// drop-policy ranks first-party last-to-drop, so this is > 0 only when first-party alone overflowed.</summary>
    public static bool IsFirstPartyTruncated(TraceSignalsDto? s) => (s?.Console?.DroppedFirstParty ?? 0) > 0;

    // ── fingerprint a run's error set → { fingerprint : representative item (count aggregated within the run) } ──
    private static Dictionary<string, ErrorItemDto> Fingerprint(TraceSignalsDto? s)
    {
        var map = new Dictionary<string, ErrorItemDto>(StringComparer.Ordinal);
        if (s is null) return map;

        foreach (var m in s.Console?.Messages ?? [])
        {
            var origin = OriginOf(m.Origin);
            var canon = TraceSignalsDiff.Canonicalize(m.Text);
            var kind = ConsoleKind(m.Level, canon);
            var fp = $"c|{m.Level}|{origin}|{m.SourceHost}|{canon}";
            Add(map, fp, () =>
            {
                var (sev, label) = Severity(kind, origin);
                return new ErrorItemDto(fp, kind, origin, m.Level, null, m.SourceHost, canon, 1, sev, label, null);
            });
        }

        foreach (var r in s.Network?.Failed ?? [])
        {
            var origin = r.ThirdParty ? "third-party" : "first-party";
            var host = HostOf(r.Url);
            var canon = TraceSignalsDiff.Canonicalize(r.Url);
            var (kind, statusClass) = NetKind(r.Status);
            var fp = $"n|{statusClass}|{origin}|{host}|{canon}";
            Add(map, fp, () =>
            {
                var (sev, label) = Severity(kind, origin);
                return new ErrorItemDto(fp, kind, origin, null, r.Status, host, canon, 1, sev, label, null);
            });
        }

        return map;
    }

    // First occurrence creates the item; repeats just bump Count (near-identical text folds to one fingerprint).
    private static void Add(Dictionary<string, ErrorItemDto> map, string fp, Func<ErrorItemDto> make)
    {
        if (map.TryGetValue(fp, out var existing))
            map[fp] = existing with { Count = existing.Count + 1 };
        else
            map[fp] = make();
    }

    private static string OriginOf(string origin) =>
        string.Equals(origin, "third-party", StringComparison.OrdinalIgnoreCase) ? "third-party" : "first-party";

    private static string ConsoleKind(string level, string canonicalText) => level switch
    {
        "pageerror" => "pageerror",
        "warning" => "warning",
        _ => CspText().IsMatch(canonicalText) ? "csp" : "console-error", // a CSP error ranks below a plain error
    };

    private static (string kind, string statusClass) NetKind(int status) =>
        status >= 500 ? ("net-5xx", "5xx")
        : status >= 400 ? ("net-4xx", "4xx")
        : ("net-abort", "abort"); // <= 0 (aborts -1/0, captured post-R2)

    /// <summary>Severity rank: fp-5xx(6) &gt; fp-4xx(5) &gt; fp-error/pageerror(4) &gt; abort(3) &gt;
    /// csp/warning(2) &gt; ANY third-party(1). ★ Any third-party error is the lowest tier — the consumer
    /// defaults to first-party and hides third-party noise.</summary>
    private static (int severity, string label) Severity(string kind, string origin)
    {
        if (origin == "third-party") return (1, "third-party");
        return kind switch
        {
            "net-5xx" => (6, "first-party-5xx"),
            "net-4xx" => (5, "first-party-4xx"),
            "console-error" or "pageerror" => (4, "first-party-error"),
            "net-abort" => (3, "abort"),
            "csp" or "warning" => (2, "warning"),
            _ => (2, "other"),
        };
    }

    private static int Rank(ErrorItemDto a, ErrorItemDto b)
    {
        var s = b.Severity.CompareTo(a.Severity); // severity desc
        if (s != 0) return s;
        var c = b.Count.CompareTo(a.Count);       // then count desc
        return c != 0 ? c : string.CompareOrdinal(a.Fingerprint, b.Fingerprint); // stable
    }

    private static int CountFp(IEnumerable<ErrorItemDto> items, bool first) =>
        items.Count(i => (i.Origin == "first-party") == first);

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "";
}
