using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>The pure trace-signals diff (data layer of the location comparison). The CANONICALIZATION test is
/// load-bearing: real console messages carry per-run ids/query/timestamps, so without it every run "differs".</summary>
public class TraceSignalsDiffTests
{
    private static TraceSignalsDto Signals(
        string? host = "www.wegmans.com",
        ConsoleMessageDto[]? console = null,
        ThirdPartyDto[]? thirdParties = null,
        TraceRequestDto[]? failed = null,
        int totalReqs = 0, long wireKb = 0, int thirdPartyCount = 0) =>
        new(host,
            new NetworkSummaryDto(totalReqs, wireKb, thirdPartyCount,
                Failed: failed ?? [], Slowest: [], Largest: [], Uncompressed: [],
                TopThirdParties: thirdParties ?? [], Mutations: []),
            new ConsoleSummaryDto(console ?? [], 0, 0));

    private static ConsoleMessageDto Err(string text, string origin = "site") => new("error", origin, text);

    [Fact]
    public void Canonicalize_strips_per_run_ids_query_and_timestamps()
    {
        // The SAME doubleclick-CSP + astutebot-WebSocket errors, different per-run ids — must canon-match.
        var east = TraceSignalsDiff.Canonicalize(
            "Fetch API cannot load https://ad.doubleclick.net/ccm/s/collect?auid=226460730.1782492068&gtm=45be66o1");
        var central = TraceSignalsDiff.Canonicalize(
            "Fetch API cannot load https://ad.doubleclick.net/ccm/s/collect?auid=1259117174.1782492629&gtm=45be66o1");
        Assert.Equal(east, central);

        var ts1 = TraceSignalsDiff.Canonicalize("[2026-06-26T16:50:35.801Z] Error: Failed to start the transport");
        var ts2 = TraceSignalsDiff.Canonicalize("[2026-06-26T16:50:35.200Z] Error: Failed to start the transport");
        Assert.Equal(ts1, ts2);
    }

    [Fact]
    public void Console_diff_treats_id_noise_as_shared_not_different()
    {
        var a = Signals(console: [Err("WebSocket connection to 'wss://x.io/eventHub?id=via11NZSmO34B' failed")]);
        var b = Signals(console: [Err("WebSocket connection to 'wss://x.io/eventHub?id=7pRbpde8hj8i' failed")]);
        var d = TraceSignalsDiff.Diff(a, b, "eastus2", "centralus");
        Assert.Empty(d.Console.OnlyInA);   // same error, different id → NOT a difference
        Assert.Empty(d.Console.OnlyInB);
        Assert.Equal(1, d.Console.Shared);
    }

    [Fact]
    public void Console_diff_surfaces_a_genuine_region_only_error()
    {
        var a = Signals(console: [Err("shared error 1"), Err("REGION-ONLY: connect ECONNREFUSED to api-east")]);
        var b = Signals(console: [Err("shared error 1")]);
        var d = TraceSignalsDiff.Diff(a, b, "eastus2", "centralus");
        var only = Assert.Single(d.Console.OnlyInA);
        Assert.Contains("REGION-ONLY", only.Text, System.StringComparison.Ordinal);
        Assert.Equal("eastus2", d.LabelA);
        Assert.Equal(1, d.Console.Shared);
        Assert.Empty(d.Console.OnlyInB);
    }

    [Fact]
    public void Network_diff_reports_totals_and_third_party_and_failed_host_deltas()
    {
        var a = Signals(
            totalReqs: 512, wireKb: 16864, thirdPartyCount: 273,
            thirdParties: [new ThirdPartyDto("images.wegmans.com", 70, 6663), new ThirdPartyDto("east-only-cdn.com", 5, 100)],
            failed: [new TraceRequestDto("https://east-fail.com/x", 503, "fetch", 10, 5, 0, 0, "", true)]);
        var b = Signals(
            totalReqs: 502, wireKb: 16908, thirdPartyCount: 266,
            thirdParties: [new ThirdPartyDto("images.wegmans.com", 66, 6600)],
            failed: []);

        var d = TraceSignalsDiff.Diff(a, b, "eastus2", "centralus").Network;
        Assert.Equal(512, d.TotalRequestsA);
        Assert.Equal(502, d.TotalRequestsB);
        Assert.Equal(1, d.FailedCountA);
        Assert.Equal(0, d.FailedCountB);
        Assert.Contains("east-fail.com", d.FailedHostsOnlyInA);
        Assert.Equal("east-only-cdn.com", Assert.Single(d.ThirdPartyOnlyInA).Host);  // a 3p origin present only in east
        Assert.Empty(d.ThirdPartyOnlyInB);
    }
}
