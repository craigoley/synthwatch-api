using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>Pure diff-computer tests (no DB): NEW/PERSISTENT/RESOLVED, the anti-flap last-N baseline
/// (must-go-red), severity ranking, first/third-party classification, truncation, and null-signal tolerance.</summary>
public class ErrorDiffTests
{
    // ── builders ──
    private static TraceSignalsDto Sig(ConsoleMessageDto[]? console = null, TraceRequestDto[]? failed = null, int droppedError = 0) =>
        new("wegmans.com",
            new NetworkSummaryDto(0, 0, 0, failed ?? [], [], [], [], [], []),
            new ConsoleSummaryDto(console ?? [], 0, 0, droppedError));

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
}
