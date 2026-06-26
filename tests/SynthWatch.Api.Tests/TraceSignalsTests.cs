using System.IO.Compression;
using System.Text;
using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>
/// The trace extraction (no DB/host) — ports docs/proposals/prototype/extract_trace.py. The console
/// extension-noise filter is the load-bearing test: if it regresses, slice 2 feeds the model garbage.
/// Fixtures mirror the real trace.network / trace.trace shapes verified against run 844486.
/// </summary>
public class TraceSignalsTests
{
    private const string Target = "www.wegmans.com";

    // ── trace.trace console fixture: 2 info/log chatter, 5 extension-noise, 3 real (2 site + 1 third-party) ──
    private const string ConsoleNdjson = """
        {"type":"console","messageType":"error","text":"component:SiteHeaderSearch:helpers Invalid discovery pages storage data","location":{"url":"https://www.wegmans.com/_next/static/chunks/x.js"}}
        {"type":"console","messageType":"warning","text":"[Meta Pixel] - Duplicate Pixel ID: 376538596548029.","location":{"url":"https://www.wegmans.com/"}}
        {"type":"console","messageType":"info","text":"[LaunchDarkly] client initialized","location":{"url":"https://www.wegmans.com/chunk.js"}}
        {"type":"console","messageType":"log","text":"[SignalR] Initial tab visibility: visible","location":{"url":"https://bot.emplifi.io/x"}}
        {"type":"console","messageType":"error","text":"Failed to load Grammarly-check.js","location":{"url":"chrome-extension://kbfnbcaeplbcioak/Grammarly-check.js"}}
        {"type":"console","messageType":"error","text":"Uncaught Error in recorder.contentScripts.inject","location":{"url":"chrome-extension://aaaa/recorder.js"}}
        {"type":"console","messageType":"warning","text":"Unchecked runtime.lastError: The message port closed before a response was received.","location":{"url":""}}
        {"type":"console","messageType":"error","text":"DEFAULT root logger initialized","location":{"url":""}}
        {"type":"console","messageType":"error","text":"AAA-init: extension boot","location":{"url":""}}
        {"type":"console","messageType":"error","text":"WebSocket connection to 'wss://realtime-c.astutebot.com/eventHub' failed","location":{"url":"https://realtime-c.astutebot.com/lib.js"}}
        {"type":"frame-snapshot"}
        """;

    // ── trace.network fixture: 5 resource-snapshots (+ a non-network line to skip) ──
    private const string NetworkNdjson = """
        {"type":"resource-snapshot","snapshot":{"_resourceType":"document","time":594,"timings":{"wait":451},"request":{"url":"https://www.wegmans.com/","method":"GET"},"response":{"status":200,"_transferSize":43165,"content":{"size":235353,"mimeType":"text/html"},"headers":[{"name":"content-encoding","value":"gzip"}]}}}
        {"type":"resource-snapshot","snapshot":{"_resourceType":"script","time":1026,"timings":{"wait":700},"request":{"url":"https://www.wegmans.com/_next/static/chunks/big.js","method":"GET"},"response":{"status":200,"_transferSize":50000,"content":{"size":120000,"mimeType":"application/javascript"},"headers":[]}}}
        {"type":"resource-snapshot","snapshot":{"_resourceType":"image","time":300,"timings":{"wait":100},"request":{"url":"https://images.wegmans.com/hero.jpg","method":"GET"},"response":{"status":200,"_transferSize":2205000,"content":{"size":2205000,"mimeType":"image/jpeg"},"headers":[]}}}
        {"type":"resource-snapshot","snapshot":{"_resourceType":"fetch","time":200,"timings":{"wait":150},"request":{"url":"https://images.wegmans.com/api/x","method":"GET"},"response":{"status":404,"_transferSize":500,"content":{"size":0,"mimeType":"application/json"},"headers":[]}}}
        {"type":"resource-snapshot","snapshot":{"_resourceType":"script","time":1499,"timings":{"wait":-1},"request":{"url":"blob:https://www.wegmans.com/abc","method":"GET"},"response":{"status":200,"_transferSize":0,"content":{"size":10,"mimeType":"application/javascript"},"headers":[]}}}
        {"type":"context-options"}
        """;

    private static Stream S(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    // ★★ THE load-bearing test: extension noise dropped, real site errors kept, chatter dropped.
    [Fact]
    public void Console_filter_drops_extension_noise_and_keeps_real_site_errors()
    {
        var c = TraceExtractor.ExtractConsole(S(ConsoleNdjson), Target);

        Assert.Equal(2, c.DroppedInfoLog);            // LaunchDarkly info + SignalR log
        Assert.Equal(5, c.DroppedExtensionNoise);     // Grammarly, recorder, message-port, DEFAULT root logger, AAA-init
        Assert.Equal(3, c.Messages.Count);            // 2 site + 1 third-party real errors

        // The real site error is kept and tagged "site".
        var search = Assert.Single(c.Messages, m => m.Text.Contains("Invalid discovery pages storage data"));
        Assert.Equal("error", search.Level);
        Assert.Equal("site", search.Origin);

        // A genuine third-party error is kept but tagged "third-party".
        Assert.Single(c.Messages, m => m.Text.Contains("astutebot") && m.Origin == "third-party");

        // NONE of the extension-noise lines survived.
        foreach (var needle in new[] { "Grammarly", "recorder.contentScripts", "message port closed",
                                       "DEFAULT root logger", "AAA-init" })
            Assert.DoesNotContain(c.Messages, m => m.Text.Contains(needle));
    }

    [Fact]
    public void Console_filter_dedupes_repeated_lines()
    {
        var dup = string.Join('\n', Enumerable.Repeat(
            """{"type":"console","messageType":"error","text":"same boom","location":{"url":"https://www.wegmans.com/a"}}""", 4));
        var c = TraceExtractor.ExtractConsole(S(dup), Target);
        Assert.Single(c.Messages);
    }

    [Fact]
    public void Network_summary_counts_top_n_and_third_party_grouping()
    {
        var n = TraceExtractor.ExtractNetwork(S(NetworkNdjson), Target);

        Assert.Equal(5, n.TotalRequests);                                   // the context-options line is skipped
        Assert.Equal((43165 + 50000 + 2205000 + 500 + 0) / 1024, n.WireKb);
        Assert.Equal(3, n.ThirdPartyCount);                                 // 2× images.wegmans.com + the blob: (no host)

        Assert.Equal(404, Assert.Single(n.Failed).Status);                  // the one 4xx
        Assert.Equal(1499, n.Slowest[0].TimeMs);                            // blob script is slowest
        Assert.Equal(2205000, n.Largest[0].Size);                          // hero image is largest

        // uncompressed = TEXT assets with no content-encoding over the floor → only big.js (the gzip'd doc + the
        // image + the tiny blob are excluded).
        var u = Assert.Single(n.Uncompressed);
        Assert.Contains("big.js", u.Url);

        // third-party grouping is by real origin (the host-less blob: is excluded from the breakdown).
        var tp = Assert.Single(n.TopThirdParties);
        Assert.Equal("images.wegmans.com", tp.Host);
        Assert.Equal(2, tp.Count);
    }

    [Fact]
    public void FromZip_parses_both_streams()
    {
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(archive.CreateEntry("trace.network").Open())) w.Write(NetworkNdjson);
            using (var w = new StreamWriter(archive.CreateEntry("trace.trace").Open())) w.Write(ConsoleNdjson);
        }
        zip.Position = 0;

        var signals = TraceExtractor.FromZip(zip, Target);
        Assert.Equal(Target, signals.TargetHost);
        Assert.Equal(5, signals.Network.TotalRequests);
        Assert.Equal(3, signals.Console.Messages.Count);
    }

    [Fact]
    public void FromZip_is_non_fatal_on_a_corrupt_zip()
    {
        var signals = TraceExtractor.FromZip(new MemoryStream([1, 2, 3, 4]), Target); // not a zip
        Assert.Same(SynthWatch.Api.Dtos.TraceSignalsDto.Empty, signals);
    }

    [Fact]
    public void FromZip_with_missing_entries_yields_empty_sections()
    {
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
            using (var w = new StreamWriter(archive.CreateEntry("unrelated.txt").Open())) w.Write("hi");
        zip.Position = 0;

        var signals = TraceExtractor.FromZip(zip, Target);
        Assert.Equal(0, signals.Network.TotalRequests);
        Assert.Empty(signals.Console.Messages);
    }
}
