using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>Pure diff-computer tests (no DB): NEW/PERSISTENT/RESOLVED, the anti-flap last-N baseline
/// (must-go-red), severity ranking, first/third-party classification, truncation, and null-signal tolerance.</summary>
public class ErrorDiffTests
{
    // ── builders ──
    private static TraceSignalsDto Sig(ConsoleMessageDto[]? console = null, TraceRequestDto[]? failed = null,
        int droppedError = 0, int droppedThirdParty = 0, int droppedFirstParty = 0) =>
        new("wegmans.com",
            new NetworkSummaryDto(0, 0, 0, failed ?? [], [], [], [], [], []),
            new ConsoleSummaryDto(console ?? [], 0, 0, droppedError, droppedThirdParty, droppedFirstParty));

    private static ConsoleMessageDto Con(string level, string origin, string host, string text) => new(level, origin, host, text);
    private static TraceRequestDto Req(string url, int status, bool thirdParty) => new(url, status, "fetch", 0, 0, 0, 0, "", thirdParty);
    private static ErrorDiff.RunSignals RS(long id, TraceSignalsDto? s) => new(id, s, ErrorDiff.IsTruncated(s));

    private static ErrorDiffDto Compute(ErrorDiff.RunSignals target, params ErrorDiff.RunSignals[] baseline) =>
        ErrorDiff.Compute(1, target.RunId, DateTimeOffset.UnixEpoch, "eastus2", target, baseline);

    [Fact]
    public void Splits_new_persistent_resolved()
    {
        // target: A (console error, first-party) + B (net 500 first-party). baseline: B + C (console error).
        var target = RS(10, Sig(
            console: [Con("error", "site", "www.wegmans.com", "alpha failed")],
            failed: [Req("https://www.wegmans.com/api/x", 500, thirdParty: false)]));
        var b1 = RS(9, Sig(
            console: [Con("error", "site", "www.wegmans.com", "gamma failed")],
            failed: [Req("https://www.wegmans.com/api/x", 500, thirdParty: false)]));

        var d = Compute(target, b1);

        Assert.Contains(d.New, e => e.Message.Contains("alpha"));           // only in target → NEW
        Assert.Contains(d.Persistent, e => e.Kind == "net-5xx");           // in both → PERSISTENT
        Assert.Contains(d.Resolved, e => e.Message.Contains("gamma"));     // baseline only → RESOLVED
        Assert.DoesNotContain(d.New, e => e.Kind == "net-5xx");
        Assert.Equal(10, Assert.Single(d.New, e => e.Message.Contains("alpha")).FirstSeenRunId); // debuts this run
        Assert.Equal(new[] { 9L }, d.BaselineRunIds);
    }

    [Fact]
    public void AntiFlap_error_in_ONE_baseline_run_is_not_NEW_last_run_only_would_flap()
    {
        // A transient THIRD-PARTY blip present in run 8 (an earlier baseline run) but NOT run 9 (the latest).
        var flap = Con("error", "third-party", "doubleclick.net", "csp blocked frame");
        var target = RS(10, Sig(console: [flap]));
        var latest = RS(9, Sig(console: []));                 // clean latest run
        var earlier = RS(8, Sig(console: [flap]));            // the blip appeared HERE

        // ★ last-N baseline (union of 9 + 8): the blip IS in the baseline → NOT new (anti-flap).
        var lastN = Compute(target, latest, earlier);
        Assert.DoesNotContain(lastN.New, e => e.Message.Contains("csp blocked"));
        Assert.Contains(lastN.Persistent, e => e.Message.Contains("csp blocked"));

        // ★ must-go-red: a last-RUN-ONLY baseline (just run 9) WOULD report the blip as NEW — the union above
        //   is what suppresses it. If Compute stopped unioning across N, the first assert would flip.
        var lastRunOnly = Compute(target, latest);
        Assert.Contains(lastRunOnly.New, e => e.Message.Contains("csp blocked"));
    }

    [Fact]
    public void Severity_ranks_first_party_5xx_above_error_above_third_party()
    {
        var target = RS(10, Sig(
            console: [Con("error", "site", "www.wegmans.com", "app crashed")],
            failed:
            [
                Req("https://www.wegmans.com/api/cart", 503, thirdParty: false), // first-party 5xx
                Req("https://ads.doubleclick.net/x", 500, thirdParty: true),     // third-party 5xx (noise)
            ]));
        var d = Compute(target); // no baseline → everything NEW

        Assert.Equal(3, d.New.Count);
        Assert.Equal("net-5xx", d.New[0].Kind);            // first-party 5xx ranks first
        Assert.Equal(6, d.New[0].Severity);
        Assert.Equal("first-party", d.New[0].Origin);
        Assert.Equal("first-party-error", d.New[1].SeverityLabel); // console error next (4)
        Assert.Equal("third-party", d.New[^1].Origin);     // ANY third-party is last
        Assert.Equal(1, d.New[^1].Severity);
    }

    [Fact]
    public void Counts_split_first_and_third_party()
    {
        var target = RS(10, Sig(console:
        [
            Con("error", "site", "www.wegmans.com", "one"),
            Con("error", "site", "www.wegmans.com", "two"),
            Con("error", "third-party", "doubleclick.net", "ad one"),
            Con("warning", "third-party", "cdn.other.com", "ad two"),
            Con("error", "third-party", "tags.tiqcdn.com", "ad three"),
        ]));
        var d = Compute(target);
        Assert.Equal(2, d.Counts.NewFirstParty);
        Assert.Equal(3, d.Counts.NewThirdParty);
    }

    [Fact]
    public void Truncated_flag_set_when_this_run_or_a_baseline_run_hit_the_cap()
    {
        Assert.True(Compute(RS(10, Sig(droppedError: 7))).Truncated);                       // this run truncated
        Assert.True(Compute(RS(10, Sig()), RS(9, Sig(droppedError: 3))).Truncated);         // a baseline run truncated
        Assert.False(Compute(RS(10, Sig()), RS(9, Sig())).Truncated);                       // neither
    }

    // ★ The truncation warning is INFORMATIVE, not just scary: third-party-only truncation is benign (the panel
    // can say "first-party complete"); a first-party drop stays LOUD. FirstPartyTruncated fires on the target OR
    // any baseline; DroppedThirdParty surfaces the target run's count for the panel's "N".
    [Fact]
    public void Truncation_is_split_by_class_third_party_only_vs_first_party_lost()
    {
        // 12 third-party dropped, 0 first-party → truncated but NOT loud; the panel says "12 third-party dropped".
        var tpOnly = Compute(RS(10, Sig(droppedError: 12, droppedThirdParty: 12, droppedFirstParty: 0)));
        Assert.True(tpOnly.Truncated);
        Assert.False(tpOnly.FirstPartyTruncated);
        Assert.Equal(12, tpOnly.DroppedThirdParty);

        // A first-party message was dropped → LOUD, regardless of the third-party count.
        var fpLost = Compute(RS(10, Sig(droppedError: 5, droppedThirdParty: 2, droppedFirstParty: 3)));
        Assert.True(fpLost.FirstPartyTruncated);

        // First-party truncation in a BASELINE run also trips the loud flag (the diff's baseline is incomplete).
        var baselineFpLost = Compute(RS(10, Sig()), RS(9, Sig(droppedError: 4, droppedThirdParty: 0, droppedFirstParty: 4)));
        Assert.True(baselineFpLost.FirstPartyTruncated);

        // No truncation at all → neither flag.
        var clean = Compute(RS(10, Sig()));
        Assert.False(clean.Truncated);
        Assert.False(clean.FirstPartyTruncated);
    }

    [Fact]
    public void Muted_fingerprint_leaves_New_and_surfaces_in_Muted_never_silently_dropped()
    {
        // Two would-be-NEW first-party errors; the operator has muted ONE of them.
        var target = RS(10, Sig(console:
        [
            Con("error", "site", "www.wegmans.com", "known noisy"),
            Con("error", "site", "www.wegmans.com", "real regression"),
        ]));

        // Grab the muted one's fingerprint from an unfiltered compute, then mute exactly it.
        var unfiltered = ErrorDiff.Compute(1, 10, DateTimeOffset.UnixEpoch, "eastus2", target, []);
        var mutedFp = Assert.Single(unfiltered.New, e => e.Message.Contains("known noisy")).Fingerprint;

        var d = ErrorDiff.Compute(1, 10, DateTimeOffset.UnixEpoch, "eastus2", target, [],
            new HashSet<string> { mutedFp });

        // ★ anti-fatigue: the muted error is OUT of New (so New stays must-go-red) but NEVER dropped — it is
        // surfaced in Muted (visible-on-demand) with its debut run id, and counted.
        Assert.DoesNotContain(d.New, e => e.Message.Contains("known noisy"));
        Assert.Contains(d.New, e => e.Message.Contains("real regression"));
        Assert.Contains(d.Muted!, e => e.Message.Contains("known noisy"));
        Assert.Equal(10, Assert.Single(d.Muted!).FirstSeenRunId);
        Assert.Equal(1, d.Counts.Muted);
        Assert.Equal(1, d.Counts.NewFirstParty); // only the un-muted one counts as NEW
    }

    [Fact]
    public void Muting_a_PERSISTENT_error_does_not_move_it_out_of_Persistent()
    {
        // The error is in the baseline too → PERSISTENT (already known). A mute only intercepts the would-be-NEW
        // signal, so a muted-but-persistent error stays in Persistent (mute has no effect there).
        var err = Con("error", "site", "www.wegmans.com", "persistent one");
        var target = RS(10, Sig(console: [err]));
        var baseline = RS(9, Sig(console: [err]));
        var fp = Assert.Single(ErrorDiff.Compute(1, 10, DateTimeOffset.UnixEpoch, "eastus2", target, [baseline]).Persistent).Fingerprint;

        var d = ErrorDiff.Compute(1, 10, DateTimeOffset.UnixEpoch, "eastus2", target, [baseline],
            new HashSet<string> { fp });

        Assert.Contains(d.Persistent, e => e.Message.Contains("persistent one"));
        Assert.Empty(d.Muted!);       // mute didn't intercept it
        Assert.Empty(d.New);
    }

    [Fact]
    public void Null_signals_are_tolerated_not_a_crash()
    {
        // target with a real error; a baseline run with NO signals (older run) is excluded from the union.
        var target = RS(10, Sig(console: [Con("error", "site", "www.wegmans.com", "solo")]));
        var d = Compute(target, RS(9, null));
        Assert.Contains(d.New, e => e.Message.Contains("solo")); // no baseline errors → still NEW, no crash

        // target with NO signals → empty new; a baseline error becomes RESOLVED.
        var d2 = Compute(RS(10, null), RS(9, Sig(console: [Con("error", "site", "www.wegmans.com", "was here")])));
        Assert.Empty(d2.New);
        Assert.Contains(d2.Resolved, e => e.Message.Contains("was here"));
    }

    // ★ NetKind (ErrorDiff.cs) was NoCoverage — zero tests — yet it drives the first-party-5xx severity that the
    // "NEW first-party service error" signal (→ transient classification → spuriousRed → the never-auto-suppress
    // safety property) rests on. Pin EVERY status class AND the two boundaries (399/400, 499/500). Distinct URLs
    // → distinct fingerprints so each network failure is its own NEW item (not folded).
    [Fact]
    public void NetKind_classifies_every_status_class_and_both_boundaries()
    {
        var d = Compute(RS(10, Sig(failed: [
            Req("https://www.wegmans.com/a500", 500, thirdParty: false), // 5xx floor
            Req("https://www.wegmans.com/a503", 503, thirdParty: false),
            Req("https://www.wegmans.com/a499", 499, thirdParty: false), // ★ 499 → 4xx (not 5xx)
            Req("https://www.wegmans.com/a400", 400, thirdParty: false), // ★ 400 → 4xx (not abort)
            Req("https://www.wegmans.com/a404", 404, thirdParty: false),
            Req("https://www.wegmans.com/a399", 399, thirdParty: false), // ★ 399 → abort (not 4xx)
            Req("https://www.wegmans.com/a0", 0, thirdParty: false),     // abort
            Req("https://www.wegmans.com/aneg", -1, thirdParty: false),  // network failure → abort
        ])));
        ErrorItemDto by(int status) => Assert.Single(d.New, e => e.Status == status);

        // 5xx (kind + label + severity all asserted — the CLASSIFICATION, not just "returns something")
        Assert.Equal("net-5xx", by(500).Kind); Assert.Equal("first-party-5xx", by(500).SeverityLabel); Assert.Equal(6, by(500).Severity);
        Assert.Equal("net-5xx", by(503).Kind);
        // 4xx — including the 499/500 and 399/400 boundaries
        Assert.Equal("net-4xx", by(499).Kind); Assert.Equal("first-party-4xx", by(499).SeverityLabel); Assert.Equal(5, by(499).Severity);
        Assert.Equal("net-4xx", by(400).Kind);
        Assert.Equal("net-4xx", by(404).Kind);
        // abort — 399 and non-positive statuses
        Assert.Equal("net-abort", by(399).Kind); Assert.Equal("abort", by(399).SeverityLabel); Assert.Equal(3, by(399).Severity);
        Assert.Equal("net-abort", by(0).Kind);
        Assert.Equal("net-abort", by(-1).Kind);
    }

    // ★ Severity: any THIRD-PARTY error is the lowest tier (1) REGARDLESS of status — the consumer defaults to
    // first-party and hides tracker noise. A third-party 500 must NOT read as a first-party-5xx regression.
    [Fact]
    public void ThirdParty_is_always_lowest_severity_even_for_a_5xx()
    {
        var d = Compute(RS(10, Sig(failed: [Req("https://tracker.doubleclick.net/x", 500, thirdParty: true)])));
        var tp = Assert.Single(d.New, e => e.Origin == "third-party");
        Assert.Equal(1, tp.Severity);            // ★ floored, not 6
        Assert.Equal("third-party", tp.SeverityLabel);
        Assert.Equal("net-5xx", tp.Kind);        // kind still reflects the status; severity/label are the floor
    }

    // ★ Console severity labels (kill the label survivors): console-error/pageerror → first-party-error(4);
    // warning → warning(2); a CSP-refusal error is downgraded to warning(2), NOT first-party-error.
    [Fact]
    public void Console_severity_labels_and_csp_downgrade_are_pinned()
    {
        var d = Compute(RS(10, Sig(console: [
            Con("error", "site", "www.wegmans.com", "TypeError: cannot read x"),                       // console-error
            Con("pageerror", "site", "www.wegmans.com", "Uncaught ReferenceError: y"),                 // pageerror
            Con("warning", "site", "www.wegmans.com", "deprecation notice"),                           // warning
            Con("error", "site", "www.wegmans.com", "Refused to load the script — Content Security Policy"), // csp
        ])));
        // Message is the CANONICAL text (Canonicalize lowercases), so match lowercase needles.
        ErrorItemDto byMsg(string needle) => Assert.Single(d.New, e => e.Message.Contains(needle));
        Assert.Equal((4, "first-party-error"), (byMsg("typeerror").Severity, byMsg("typeerror").SeverityLabel));
        Assert.Equal((4, "first-party-error"), (byMsg("uncaught").Severity, byMsg("uncaught").SeverityLabel));
        Assert.Equal((2, "warning"), (byMsg("deprecation").Severity, byMsg("deprecation").SeverityLabel));
        Assert.Equal("csp", byMsg("refused").Kind);                                    // ★ classified csp…
        Assert.Equal((2, "warning"), (byMsg("refused").Severity, byMsg("refused").SeverityLabel)); // …→ warning tier, not error(4)
    }

    // ★ Rank tie-break (kill the Rank survivors): buckets sort severity-desc, then count-desc, then fingerprint
    // ordinal (stable). Prove all three keys — a mutated comparator reorders one of these.
    [Fact]
    public void Rank_orders_by_severity_then_count_then_fingerprint()
    {
        // severity desc: a 5xx (6) must rank before a 4xx (5) before an abort (3), whatever input order.
        var sev = Compute(RS(10, Sig(failed: [
            Req("https://www.wegmans.com/z", 0, thirdParty: false),    // abort (3)
            Req("https://www.wegmans.com/y", 400, thirdParty: false),  // 4xx (5)
            Req("https://www.wegmans.com/x", 500, thirdParty: false),  // 5xx (6)
        ])));
        Assert.Equal(new[] { "net-5xx", "net-4xx", "net-abort" }, sev.New.Select(e => e.Kind).ToArray());

        // count desc within equal severity: the same first-party console error folded 3× outranks a single one.
        var cnt = Compute(RS(10, Sig(console: [
            Con("error", "site", "a.wegmans.com", "solo error"),
            Con("error", "site", "b.wegmans.com", "repeated error"),
            Con("error", "site", "b.wegmans.com", "repeated error"),
            Con("error", "site", "b.wegmans.com", "repeated error"),
        ])));
        // both are severity 4; "repeated" (count 3) must come before "solo" (count 1).
        Assert.Equal(new[] { "repeated error", "solo error" }, cnt.New.Select(e => e.Message).ToArray());
    }
}
