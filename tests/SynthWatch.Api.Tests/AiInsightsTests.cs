using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>The prompt + model-response mapping (no DB/HTTP). The system prompt's HONESTY rules are part of
/// the contract — assert they're present so a future edit can't quietly drop them.</summary>
public class AiInsightsTests
{
    private static TraceSignalsDto SampleSignals() => new(
        TargetHost: "www.wegmans.com",
        Network: new NetworkSummaryDto(
            TotalRequests: 561, WireKb: 11431, ThirdPartyCount: 287,
            Failed: [], Slowest: [], Largest: [], Uncompressed: [],
            TopThirdParties: [new ThirdPartyDto("images.wegmans.com", 70, 6663)]),
        Console: new ConsoleSummaryDto(
            Messages: [new ConsoleMessageDto("error", "site", "SiteHeaderSearch: Invalid discovery pages storage data")],
            DroppedInfoLog: 72, DroppedExtensionNoise: 0));

    [Fact]
    public void System_prompt_bakes_in_the_honesty_caveats()
    {
        var p = AiInsights.SystemPrompt;
        Assert.Contains("NOT a real Lighthouse audit", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Web Vitals", p, StringComparison.Ordinal);
        Assert.Contains("third-party", p, StringComparison.OrdinalIgnoreCase);   // site vs third-party distinction
        Assert.Contains("site", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("couldn't determine", p, StringComparison.OrdinalIgnoreCase);
        // The 4 requested categories.
        foreach (var cat in new[] { "performance", "network", "errors", "suggestions" })
            Assert.Contains(cat, p, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void User_message_carries_the_run_context_and_the_summary_with_origin_tags()
    {
        var u = AiInsights.BuildUser(new AiInsights.RunContext("Wegmans search", "www.wegmans.com", "fail"), SampleSignals());
        Assert.Contains("Wegmans search", u, StringComparison.Ordinal);
        Assert.Contains("www.wegmans.com", u, StringComparison.Ordinal);
        Assert.Contains("origin", u, StringComparison.Ordinal);                 // tells the model site vs third-party
        Assert.Contains("Invalid discovery pages storage data", u, StringComparison.Ordinal); // the real site error
        Assert.Contains("images.wegmans.com", u, StringComparison.Ordinal);     // third-party weight
    }

    [Fact]
    public void Parse_maps_categorized_insights_and_clamps_severity()
    {
        const string model = """
            {
              "summary": "A few wins.",
              "performance": [
                { "title": "Slow chunks", "detail": "high server wait", "severity": "CRITICAL",
                  "confidence": "high", "evidence": "big.js 1026ms" }
              ],
              "network": [],
              "errors": [
                { "title": "Search bug", "detail": "fix storage", "severity": "high", "confidence": "medium" }
              ],
              "suggestions": [],
              "caveats": ["Web Vitals omitted — unreliable for this SPA"]
            }
            """;
        var dto = AiInsights.Parse(model)!;
        Assert.True(dto.Configured);
        Assert.Equal("A few wins.", dto.Summary);

        var perf = Assert.Single(dto.Performance);
        Assert.Equal("Slow chunks", perf.Title);
        Assert.Equal("medium", perf.Severity);          // "CRITICAL" is off-taxonomy → clamped to default
        Assert.Equal("high", perf.Confidence);
        Assert.Equal("big.js 1026ms", perf.Evidence);

        Assert.Null(Assert.Single(dto.Errors).Evidence); // evidence omitted → null
        Assert.Single(dto.Caveats);
        Assert.Empty(dto.Network);
    }

    [Fact]
    public void Parse_tolerates_markdown_fences()
    {
        var dto = AiInsights.Parse("```json\n{ \"summary\": \"ok\", \"performance\": [] }\n```");
        Assert.NotNull(dto);
        Assert.Equal("ok", dto!.Summary);
    }

    [Fact]
    public void Parse_returns_null_on_garbage()
    {
        Assert.Null(AiInsights.Parse("the model said no"));
    }
}
