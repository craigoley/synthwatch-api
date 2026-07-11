using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>
/// The RCA prompt + context build + parse (no DB/HTTP). The contract: the DISTILLED context LEADS with the failed
/// assertion + the action-under-test's network result, demotes the baseline delta to secondary, and the system
/// prompt teaches the site-failure / monitor-verification-bug / transient decomposition. The 849441 regression
/// (cart POST 200 + a spec-code assertion TypeError) must be representable so the model can call it a monitor bug.
/// </summary>
public class LocationDiffInsightTests
{
    private static TraceDiffDto SampleDiff() => new(
        "this run (eastus2, fail)", "the monitor's last-known-good baseline",
        new DiffConsole(
            OnlyInA: [new DiffConsoleLine("error", "site", "api-east.wegmans.com", "REGION-ONLY: connect ECONNREFUSED api-east")],
            OnlyInB: [], Shared: 12),
        new DiffNetwork(512, 502, 16864, 16908, 273, 266, 1, 0,
            FailedHostsOnlyInA: ["east-fail.com"], FailedHostsOnlyInB: [],
            ThirdPartyOnlyInA: [], ThirdPartyOnlyInB: []));

    private static NetworkSummaryDto Net(
        int total, IReadOnlyList<TraceRequestDto>? failed = null, IReadOnlyList<MutationDto>? mutations = null) =>
        new(total, 0, 0, failed ?? [], [], [], [], [], mutations ?? []);

    // ── system prompt: the decomposition + grounding contract ──────────────────────────────────────────────
    [Fact]
    public void System_prompt_teaches_the_layer_decomposition_and_grounding()
    {
        var p = LocationDiffInsight.SystemPrompt;
        // the three layers + verdict taxonomy
        foreach (var v in new[] { "site-failure", "monitor-verification-bug", "transient", "undetermined" })
            Assert.Contains(v, p, StringComparison.Ordinal);
        // the decisive rule: a 2xx on the action argues against transient and points at a monitor bug
        Assert.Contains("2xx", p, StringComparison.Ordinal);
        Assert.Contains("verification", p, StringComparison.OrdinalIgnoreCase);
        // it must explicitly call out the spec-code-error / waitForResponse failure mode (the 849441 class)
        Assert.Contains("waitForResponse", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TypeError", p, StringComparison.Ordinal);
        // count deltas are demoted, not the headline
        Assert.Contains("count", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secondary", p, StringComparison.OrdinalIgnoreCase);
    }

    // ── context build: assertion + action network result LEAD; baseline is secondary; stays small ──────────
    [Fact]
    public void User_message_leads_with_the_failed_assertion_and_the_action_network_result()
    {
        var net = Net(269, mutations: [new MutationDto("POST", "https://www.meals2go.com/api/cart-items?ts=1", 200)]);
        var u = LocationDiffInsight.BuildUser(
            "this run (eastus2, error)", "the baseline", "error",
            failedStep: "add cheese pizza to cart",
            assertionError: "Cannot read properties of undefined (reading 'toBeNull')",
            failingNetwork: net, diff: SampleDiff());

        // the failed assertion is present and PRECEDES the baseline color
        Assert.Contains("add cheese pizza to cart", u, StringComparison.Ordinal);
        Assert.Contains("Cannot read properties of undefined (reading 'toBeNull')", u, StringComparison.Ordinal);
        Assert.Contains("cart-items", u, StringComparison.Ordinal);     // the action under test
        Assert.Contains("200", u, StringComparison.Ordinal);            // ...returned 200
        Assert.True(u.IndexOf("FAILED ASSERTION", StringComparison.Ordinal)
                    < u.IndexOf("BASELINE COLOR", StringComparison.Ordinal),
            "the assertion section must lead the baseline section");
        // the query string is trimmed off the mutation URL (compact + leak-resistant)
        Assert.DoesNotContain("ts=1", u, StringComparison.Ordinal);
    }

    [Fact]
    public void Distilled_context_is_smaller_than_dumping_the_whole_signals()
    {
        var net = Net(269, mutations: [new MutationDto("POST", "https://x/api/cart-items", 200)]);
        var u = LocationDiffInsight.BuildUser("r", "b", "error", "step", "err", net, SampleDiff());
        // a tight context, not a trace dump (research: distill, don't dump)
        Assert.True(u.Length < 4000, $"context should stay small; was {u.Length} chars");
    }

    // ── parse: the new verdict ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Parse_maps_the_monitor_verification_bug_verdict()
    {
        const string model = """
            {
              "verdict": "monitor-verification-bug",
              "summary": "The cart-items POST returned 200 — the add succeeded — but the verification threw a TypeError; this is a monitor bug.",
              "likelyCause": "monitor-verification-bug",
              "confidence": "high",
              "isFlaky": false,
              "findings": [ { "title": "Verification read undefined", "detail": "toBeNull on undefined response",
                              "severity": "high", "confidence": "high", "evidence": "cart-items POST 200" } ],
              "caveats": []
            }
            """;
        var i = LocationDiffInsight.Parse(model)!;
        Assert.Equal("monitor-verification-bug", i.Verdict);
        Assert.Equal("monitor-verification-bug", i.LikelyCause);
        Assert.False(i.IsFlaky);
        Assert.DoesNotContain("transient", i.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_clamps_an_off_taxonomy_verdict_to_undetermined()
    {
        var i = LocationDiffInsight.Parse("""{ "summary": "x", "verdict": "aliens", "likelyCause": "aliens" }""")!;
        Assert.Equal("undetermined", i.Verdict);
        Assert.Equal("undetermined", i.LikelyCause);
    }

    [Fact]
    public void Parse_still_allows_transient_when_the_model_says_so()
    {
        const string model = """
            { "verdict": "transient", "summary": "One-off WebSocket blip, no action request.", "likelyCause": "flaky-transient",
              "confidence": "low", "isFlaky": true, "findings": [], "caveats": ["thin"] }
            """;
        var i = LocationDiffInsight.Parse(model)!;
        Assert.Equal("transient", i.Verdict);
        Assert.True(i.IsFlaky);
    }

    [Fact]
    public void Parse_returns_null_on_garbage() => Assert.Null(LocationDiffInsight.Parse("nope"));
}

/// <summary>
/// The three RCA REGRESSION fixtures the task pins, exercised through the CONTEXT BUILDER (the part we control
/// deterministically; the model output itself is proven live). Each asserts the context carries the decisive
/// facts so a grounded model reaches the right verdict — and the 849441 case specifically can NOT be read as
/// transient (a 2xx action + a spec-code error are both right there in section 1/2).
/// </summary>
public class RcaContextRegressionTests
{
    private static readonly TraceDiffDto NoStructuralDelta = new(
        "fail", "baseline",
        new DiffConsole([], [], Shared: 40),
        new DiffNetwork(269, 285, 0, 0, 269, 285, 0, 0, [], [], [], []));  // only count deltas, no failures

    private static NetworkSummaryDto Net(int total, IReadOnlyList<TraceRequestDto>? failed, IReadOnlyList<MutationDto>? muts) =>
        new(total, 0, 0, failed ?? [], [], [], [], [], muts ?? []);

    [Fact]
    public void Case_849441_monitor_bug_context_has_the_2xx_action_and_the_spec_error()
    {
        // The motivating failure: cart-items POST 200 (action succeeded), assertion threw a TypeError, NO failures.
        var net = Net(269, failed: [], muts: [new MutationDto("POST", "https://www.meals2go.com/api/cart-items", 200)]);
        var u = LocationDiffInsight.BuildUser(
            "this run (cloud, error)", "the baseline", "error",
            "add cheese pizza to cart", "Cannot read properties of undefined (reading 'toBeNull')", net, NoStructuralDelta);

        Assert.Contains("POST", u, StringComparison.Ordinal);
        Assert.Contains("cart-items", u, StringComparison.Ordinal);
        Assert.Contains("200", u, StringComparison.Ordinal);                          // the action SUCCEEDED
        Assert.Contains("toBeNull", u, StringComparison.Ordinal);                     // spec-code error
        Assert.Contains("Failed requests (status >= 400) this run: none", u, StringComparison.Ordinal);  // nothing failed
    }

    [Fact]
    public void Case_site_failure_context_shows_the_action_4xx()
    {
        // Genuine site failure: the cart POST itself 500'd.
        var net = Net(120, failed: [new TraceRequestDto("https://www.meals2go.com/api/cart-items", 500, "fetch", 0, 0, 0, 0, "", false)],
            muts: [new MutationDto("POST", "https://www.meals2go.com/api/cart-items", 500)]);
        var u = LocationDiffInsight.BuildUser(
            "this run (cloud, fail)", "the baseline", "fail",
            "add cheese pizza to cart", "expect(response.ok()).toBeTruthy() failed", net, NoStructuralDelta);

        Assert.Contains("→ 500", u, StringComparison.Ordinal);    // the action FAILED at the site
        Assert.Contains("500 www.meals2go.com", u, StringComparison.Ordinal);  // and shows up as a failed request
    }

    [Fact]
    public void Case_transient_context_has_no_2xx_action_and_a_one_off_error()
    {
        // Genuine transient: no mutating request captured, a single one-off network error.
        var net = Net(95, failed: [new TraceRequestDto("https://cdn.example/ws", 0, "websocket", 0, 0, 0, 0, "", true)], muts: []);
        var u = LocationDiffInsight.BuildUser(
            "this run (cloud, error)", "the baseline", "error",
            "open menu", "page.waitForSelector timed out", net, NoStructuralDelta);

        Assert.Contains("(no mutating request captured in this run's trace)", u, StringComparison.Ordinal);
        Assert.DoesNotContain("→ 200", u, StringComparison.Ordinal);  // no 2xx action to argue against transient
    }
}
