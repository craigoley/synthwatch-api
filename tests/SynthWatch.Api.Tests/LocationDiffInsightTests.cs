using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>The comparison prompt + parse (no DB/HTTP). The regional-cause taxonomy + the flakiness honesty are
/// part of the contract — assert they're in the system prompt and that parse normalizes them.</summary>
public class LocationDiffInsightTests
{
    private static TraceDiffDto SampleDiff() => new(
        "this run (eastus2, fail)", "the monitor's last-known-good baseline",
        new DiffConsole(
            OnlyInA: [new DiffConsoleLine("error", "site", "REGION-ONLY: connect ECONNREFUSED api-east")],
            OnlyInB: [], Shared: 12),
        new DiffNetwork(512, 502, 16864, 16908, 273, 266, 1, 0,
            FailedHostsOnlyInA: ["east-fail.com"], FailedHostsOnlyInB: [],
            ThirdPartyOnlyInA: [], ThirdPartyOnlyInB: []));

    [Fact]
    public void System_prompt_has_the_regional_taxonomy_and_flakiness_honesty()
    {
        var p = LocationDiffInsight.SystemPrompt;
        foreach (var cause in new[] { "regional-waf-cdn", "network-allowlist", "geo-dns", "region-timeout",
                                      "third-party-blocked", "flaky-transient", "undetermined" })
            Assert.Contains(cause, p, StringComparison.Ordinal);
        Assert.Contains("isFlaky", p, StringComparison.Ordinal);
        Assert.Contains("may be transient", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("last-known-good", p, StringComparison.OrdinalIgnoreCase);   // honest framing
        Assert.Contains("third-party", p, StringComparison.OrdinalIgnoreCase);       // site vs third-party
        Assert.Contains("couldn't determine", p, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void User_message_carries_the_labels_and_the_delta()
    {
        var u = LocationDiffInsight.BuildUser("this run (eastus2, fail)", "the baseline", SampleDiff());
        Assert.Contains("eastus2", u, StringComparison.Ordinal);
        Assert.Contains("REGION-ONLY", u, StringComparison.Ordinal);  // the delta line
        Assert.Contains("east-fail.com", u, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_maps_a_categorized_regional_cause()
    {
        const string model = """
            {
              "summary": "eastus2 can't reach api-east.",
              "likelyCause": "network-allowlist",
              "confidence": "medium",
              "isFlaky": false,
              "findings": [
                { "title": "Region IP refused", "detail": "ECONNREFUSED only in eastus2", "severity": "high",
                  "confidence": "medium", "evidence": "connect ECONNREFUSED api-east" }
              ],
              "caveats": ["two single runs"]
            }
            """;
        var i = LocationDiffInsight.Parse(model)!;
        Assert.Equal("network-allowlist", i.LikelyCause);
        Assert.False(i.IsFlaky);
        Assert.Equal("Region IP refused", Assert.Single(i.Findings).Title);
        Assert.Single(i.Caveats);
    }

    [Fact]
    public void Parse_marks_a_thin_delta_as_flaky_transient()
    {
        const string model = """
            { "summary": "Just a one-off WebSocket blip.", "likelyCause": "flaky-transient",
              "confidence": "low", "isFlaky": true, "findings": [], "caveats": ["thin delta"] }
            """;
        var i = LocationDiffInsight.Parse(model)!;
        Assert.True(i.IsFlaky);
        Assert.Equal("flaky-transient", i.LikelyCause);
    }

    [Fact]
    public void Parse_clamps_an_off_taxonomy_cause_to_undetermined()
    {
        var i = LocationDiffInsight.Parse("""{ "summary": "x", "likelyCause": "aliens", "confidence": "high" }""")!;
        Assert.Equal("undetermined", i.LikelyCause);
    }

    [Fact]
    public void Parse_returns_null_on_garbage() => Assert.Null(LocationDiffInsight.Parse("nope"));
}
