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

    // ★ DURABLE input bound: a pathological trace (hundreds of DISTINCT site errors) must not produce an
    // unbounded summary that blows the downstream AOAI token budget.
    [Fact]
    public void Console_messages_are_hard_capped_for_a_pathological_trace()
    {
        var lines = Enumerable.Range(0, 500).Select(i =>
            "{\"type\":\"console\",\"messageType\":\"error\",\"text\":\"distinct site error number " + i +
            "\",\"location\":{\"url\":\"https://www.wegmans.com/p" + i + "\"}}");
        var c = TraceExtractor.ExtractConsole(S(string.Join('\n', lines)), Target);
        Assert.True(c.Messages.Count <= 80, $"expected ≤80 kept, got {c.Messages.Count}");
    }

    // ★★ MUST-GO-RED (parity with the runner's traceSignals.test.ts): the capture drop-policy ranks by
    // FIRST-PARTY-NESS before severity, so at the cap THIRD-PARTY is dropped FIRST and first-party — INCLUDING
    // first-party WARNINGS — survives. Reverting to the old severity-dominant order (third-party errors above
    // first-party warnings) makes DroppedFirstParty > 0, failing this.
    [Fact]
    public void Console_drop_policy_drops_third_party_before_first_party_at_the_cap()
    {
        var fpErrors = Enumerable.Range(0, 10).Select(i =>
            "{\"type\":\"console\",\"messageType\":\"error\",\"text\":\"first-party API error " + i +
            "\",\"location\":{\"url\":\"https://www.wegmans.com/x" + i + "\"}}");
        var fpWarnings = Enumerable.Range(0, 50).Select(i =>
            "{\"type\":\"console\",\"messageType\":\"warning\",\"text\":\"first-party warning " + i +
            "\",\"location\":{\"url\":\"https://www.wegmans.com/w" + i + "\"}}");
        var tpErrors = Enumerable.Range(0, 50).Select(i =>
            "{\"type\":\"console\",\"messageType\":\"error\",\"text\":\"doubleclick tracker error " + i +
            "\",\"location\":{\"url\":\"https://ad.doubleclick.net/t" + i + "\"}}");
        var c = TraceExtractor.ExtractConsole(
            S(string.Join('\n', fpErrors.Concat(fpWarnings).Concat(tpErrors))), Target);
        Assert.Equal(80, c.Messages.Count);
        Assert.Equal(60, c.Messages.Count(m => m.Origin == "site"));  // every first-party (10 err + 50 warn) survives
        Assert.Equal(30, c.DroppedError);
        Assert.Equal(30, c.DroppedThirdParty);
        Assert.Equal(0, c.DroppedFirstParty);                        // ← reverting the ranking flips this to 30
    }

    // ── Error-diff P1: resource-host classification (console) ────────────────────────────────────────────
    // origin is keyed off the host of the RESOURCE the error is ABOUT (parsed from the first URL in the text),
    // not the frame that logged it — and against the Wegmans first-party allowlist, not the exact target host.

    [Fact] // ★ MUST-GO-RED: a CSP refusal of a third-party resource LOGGED BY the site frame is third-party
    public void Console_csp_third_party_resource_logged_by_site_frame_is_third_party()
    {
        // Reported BY the page (location.url = the site) but ABOUT a third-party resource. The OLD frame-based
        // rule mislabelled this origin:'site'; the resource-host rule reads di.rlcdn.com out of the text.
        const string nd = """{"type":"console","messageType":"error","text":"Refused to load the script 'https://di.rlcdn.com/tag.js' because it violates the Content Security Policy directive","location":{"url":"https://www.wegmans.com/"}}""";
        var c = TraceExtractor.ExtractConsole(S(nd), Target);
        var m = Assert.Single(c.Messages);
        Assert.Equal("third-party", m.Origin);        // NOT 'site' — the resource, not the logging frame
        Assert.Equal("di.rlcdn.com", m.SourceHost);
    }

    [Fact] // ★ MUST-GO-RED: a *.wegmans.cloud resource error is first-party (old exact-host marked it third-party)
    public void Console_wegmans_cloud_resource_is_first_party()
    {
        const string nd = """{"type":"console","messageType":"error","text":"GET https://api.wegmans.cloud/v1/cart 500 (Internal Server Error)","location":{"url":"https://www.wegmans.com/cart"}}""";
        var c = TraceExtractor.ExtractConsole(S(nd), Target);
        var m = Assert.Single(c.Messages);
        Assert.Equal("site", m.Origin);               // .wegmans.cloud is first-party
        Assert.Equal("api.wegmans.cloud", m.SourceHost);
    }

    [Fact]
    public void Console_source_host_falls_back_to_the_logging_frame_when_text_has_no_url()
    {
        const string nd = """{"type":"console","messageType":"error","text":"component:Cart:helpers Invalid state","location":{"url":"https://images.wegmans.com/x.js"}}""";
        var c = TraceExtractor.ExtractConsole(S(nd), Target);
        var m = Assert.Single(c.Messages);
        Assert.Equal("images.wegmans.com", m.SourceHost);   // frame host (no URL in text)
        Assert.Equal("site", m.Origin);                     // first-party sibling subdomain
    }

    [Fact]
    public void Network_summary_counts_top_n_and_third_party_grouping()
    {
        var n = TraceExtractor.ExtractNetwork(S(NetworkNdjson), Target);

        Assert.Equal(5, n.TotalRequests);                                   // the context-options line is skipped
        Assert.Equal((43165 + 50000 + 2205000 + 500 + 0) / 1024, n.WireKb);
        // ★ Error-diff P1: images.wegmans.com is a SIBLING subdomain of the target (www.wegmans.com) →
        // FIRST-party under the Wegmans allowlist. The old exact-target-host rule wrongly counted it
        // third-party; now only the host-less blob: resource (no host → third-party) remains.
        Assert.Equal(1, n.ThirdPartyCount);

        var failed = Assert.Single(n.Failed);
        Assert.Equal(404, failed.Status);                                   // the one 4xx
        Assert.False(failed.ThirdParty);                                    // images.wegmans.com — now first-party
        Assert.Equal(1499, n.Slowest[0].TimeMs);                            // blob script is slowest
        Assert.Equal(2205000, n.Largest[0].Size);                          // hero image is largest
        Assert.False(n.Largest[0].ThirdParty);                             // images.wegmans.com — now first-party

        // uncompressed = TEXT assets with no content-encoding over the floor → only big.js (the gzip'd doc + the
        // image + the tiny blob are excluded).
        var u = Assert.Single(n.Uncompressed);
        Assert.Contains("big.js", u.Url);

        // blob: has no host → excluded from grouping; images.wegmans.com is first-party → not grouped.
        Assert.Empty(n.TopThirdParties);
    }

    // ★ Error-diff P1: grouping still works for a GENUINE third-party host (bot.emplifi.io — not Wegmans),
    // so the allowlist fix narrows what counts as third-party without breaking the third-party rollup.
    [Fact]
    public void Network_third_party_grouping_groups_a_real_third_party_host()
    {
        var nd = string.Join('\n',
            """{"type":"resource-snapshot","snapshot":{"_resourceType":"document","time":10,"timings":{"wait":5},"request":{"url":"https://www.wegmans.com/","method":"GET"},"response":{"status":200,"_transferSize":1000,"content":{"size":500}}}}""",
            """{"type":"resource-snapshot","snapshot":{"_resourceType":"script","time":20,"timings":{"wait":5},"request":{"url":"https://bot.emplifi.io/a.js","method":"GET"},"response":{"status":200,"_transferSize":40000,"content":{"size":40000}}}}""",
            """{"type":"resource-snapshot","snapshot":{"_resourceType":"script","time":30,"timings":{"wait":5},"request":{"url":"https://bot.emplifi.io/b.js","method":"GET"},"response":{"status":200,"_transferSize":8000,"content":{"size":8000}}}}""");
        var n = TraceExtractor.ExtractNetwork(S(nd), Target);
        Assert.Equal(2, n.ThirdPartyCount);                                 // both emplifi scripts
        var tp = Assert.Single(n.TopThirdParties);
        Assert.Equal("bot.emplifi.io", tp.Host);
        Assert.Equal(2, tp.Count);
        Assert.Equal((40000 + 8000) / 1024, tp.Kb);
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
