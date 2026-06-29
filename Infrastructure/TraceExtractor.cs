using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using SynthWatch.Api.Dtos;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Server-side extraction of a Playwright trace zip into a compact, filtered <see cref="TraceSignalsDto"/>.
/// A faithful C# port of docs/proposals/prototype/extract_trace.py (proven against real trace 844486) — same
/// signals, same thresholds, same console filter. Pure + non-fatal: a missing entry / unparseable line yields
/// an empty section, never an exception that 500s the request.
///
/// The trace zip (Playwright 1.61.0) holds two newline-delimited-JSON streams we read:
///   trace.network — one {type:"resource-snapshot"} per request (HAR-shaped: status, sizes, compression,
///                   dns/connect/ssl/wait/receive timing, _resourceType).
///   trace.trace   — actions + {type:"console"} events (messageType, text, location.url).
/// </summary>
public static partial class TraceExtractor
{
    // ★ Browser-EXTENSION console noise — a trace captured/opened with extensions is NOT the monitored site.
    // Matched against the message text AND its source url. THE load-bearing correctness filter; ported verbatim.
    [GeneratedRegex(@"grammarly|recorder\.contentScripts|contentscript|message port closed|DEFAULT root logger|AAA-init|chrome-extension://|moz-extension://",
        RegexOptions.IgnoreCase)]
    private static partial Regex ExtensionNoise();

    // Assets where missing compression is a real concern (a big image isn't "uncompressed", just large).
    private static readonly HashSet<string> TextTypes = new(StringComparer.Ordinal)
        { "script", "stylesheet", "document", "fetch", "xhr" };

    private const int TopN = 5;
    private const int FailedCap = 8;
    private const int ThirdPartyCap = 6;
    private const int UncompressedMinBytes = 30_000;
    // ★ Hard cap on console messages so a pathological trace (hundreds of distinct site errors) can't blow the
    // downstream AOAI token budget. The network lists are already top-N bounded; this was the one unbounded list.
    private const int MaxConsoleMessages = 40;

    /// <summary>Open the trace zip + extract both sections. Non-fatal: a bad zip / missing entries → empty.</summary>
    public static TraceSignalsDto FromZip(Stream zip, string? targetHost)
    {
        try
        {
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
            var network = ReadEntry(archive, "trace.network", s => ExtractNetwork(s, targetHost))
                          ?? NetworkSummaryDto.Empty;
            var console = ReadEntry(archive, "trace.trace", s => ExtractConsole(s, targetHost))
                          ?? ConsoleSummaryDto.Empty;
            return new TraceSignalsDto(targetHost, network, console);
        }
        catch (InvalidDataException)
        {
            return TraceSignalsDto.Empty; // not a valid zip — non-fatal
        }
    }

    private static T? ReadEntry<T>(ZipArchive archive, string name, Func<Stream, T> read) where T : class
    {
        var entry = archive.GetEntry(name);
        if (entry is null) return null;
        using var s = entry.Open();
        return read(s);
    }

    // ── network ─────────────────────────────────────────────────────────────────────────────────────────

    private readonly record struct Req(
        string Url, int Status, string Rtype, int Time, int Wait, long Size, long Wire, string Enc, bool Third);

    public static NetworkSummaryDto ExtractNetwork(Stream networkNdjson, string? targetHost)
    {
        var reqs = new List<Req>();
        foreach (var line in Lines(networkNdjson))
        {
            if (!TryParse(line, out var doc)) continue;
            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t) || t.GetString() != "resource-snapshot") continue;
                if (!root.TryGetProperty("snapshot", out var s)) continue;

                var req = s.TryGetProperty("request", out var rq) ? rq : default;
                var resp = s.TryGetProperty("response", out var rs) ? rs : default;
                var content = resp.ValueKind == JsonValueKind.Object && resp.TryGetProperty("content", out var c) ? c : default;

                var url = Str(req, "url");
                reqs.Add(new Req(
                    Url: url,
                    Status: Int(resp, "status"),
                    Rtype: Str(s, "_resourceType"),
                    Time: (int)Math.Round(Dbl(s, "time")),
                    Wait: (int)Math.Round(s.TryGetProperty("timings", out var tm) ? Dbl(tm, "wait") : 0),
                    Size: Long(content, "size"),
                    Wire: Long(resp, "_transferSize"),
                    Enc: Header(resp, "content-encoding"),
                    Third: !IsSite(HostOf(url), targetHost)));
            }
        }

        TraceRequestDto Slim(Req r) => new(r.Url, r.Status, r.Rtype, r.Time, r.Wait, r.Size, r.Wire, r.Enc, r.Third);

        var thirdParties = reqs.Where(r => r.Third && HostOf(r.Url).Length > 0)
            .GroupBy(r => HostOf(r.Url))
            .Select(g => new ThirdPartyDto(g.Key, g.Count(), g.Sum(r => r.Wire) / 1024))
            .OrderByDescending(t => t.Kb).Take(ThirdPartyCap).ToList();

        return new NetworkSummaryDto(
            TotalRequests: reqs.Count,
            WireKb: reqs.Sum(r => r.Wire) / 1024,
            ThirdPartyCount: reqs.Count(r => r.Third),
            Failed: reqs.Where(r => r.Status >= 400).Take(FailedCap).Select(Slim).ToList(),
            Slowest: reqs.OrderByDescending(r => r.Time).Take(TopN).Select(Slim).ToList(),
            Largest: reqs.OrderByDescending(r => r.Size).Take(TopN).Select(Slim).ToList(),
            // uncompressed: TEXT assets only, no content-encoding, over the size floor.
            Uncompressed: reqs.Where(r => TextTypes.Contains(r.Rtype) && r.Enc.Length == 0 && r.Size > UncompressedMinBytes)
                              .OrderByDescending(r => r.Size).Take(TopN).Select(Slim).ToList(),
            TopThirdParties: thirdParties);
    }

    // ── console (the filter) ────────────────────────────────────────────────────────────────────────────

    public static ConsoleSummaryDto ExtractConsole(Stream traceNdjson, string? targetHost)
    {
        var kept = new List<ConsoleMessageDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int droppedLevel = 0, droppedExt = 0;

        foreach (var line in Lines(traceNdjson))
        {
            if (!TryParse(line, out var doc)) continue;
            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t) || t.GetString() != "console") continue;

                var level = root.TryGetProperty("messageType", out var mt) ? mt.GetString() ?? "log" : "log";
                var text = (root.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "").Trim();
                var loc = root.TryGetProperty("location", out var l) && l.ValueKind == JsonValueKind.Object
                    ? Str(l, "url") : "";

                if (level != "error" && level != "warning") { droppedLevel++; continue; }     // info/log chatter
                if (ExtensionNoise().IsMatch(text) || ExtensionNoise().IsMatch(loc)) { droppedExt++; continue; }

                var key = level + "|" + (text.Length > 80 ? text[..80] : text);
                if (!seen.Add(key)) continue;                                                  // dedupe repeats

                kept.Add(new ConsoleMessageDto(
                    Level: level,
                    Origin: IsSite(HostOf(loc), targetHost) ? "site" : "third-party",
                    Text: text.Length > 200 ? text[..200] : text));
            }
        }
        // Bound the list, keeping the most relevant: the site's own errors first, then warnings/third-party.
        // OrderBy is stable, so first-seen order is preserved within each priority tier.
        var bounded = kept
            .OrderByDescending(m => m.Level == "error")
            .ThenByDescending(m => m.Origin == "site")
            .Take(MaxConsoleMessages)
            .ToList();
        return new ConsoleSummaryDto(bounded, droppedLevel, droppedExt);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<string> Lines(Stream s)
    {
        using var reader = new StreamReader(s);
        string? line;
        while ((line = reader.ReadLine()) is not null)
            if (line.Length > 0) yield return line;
    }

    private static bool TryParse(string line, out JsonDocument doc)
    {
        try { doc = JsonDocument.Parse(line); return true; }
        catch (JsonException) { doc = null!; return false; }
    }

    // Host only for real http(s) urls — blob:/data:/about: have no host (→ third-party), matching the prototype.
    private static string HostOf(string url)
    {
        if (!url.StartsWith("http://", StringComparison.Ordinal) && !url.StartsWith("https://", StringComparison.Ordinal))
            return "";
        return Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "";
    }

    private static bool IsSite(string host, string? target) =>
        !string.IsNullOrEmpty(target) && host.Length > 0 &&
        (host.Equals(target, StringComparison.OrdinalIgnoreCase) ||
         host.EndsWith("." + target, StringComparison.OrdinalIgnoreCase));

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : 0;

    private static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64() : 0;

    private static double Dbl(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : 0;

    private static string Header(JsonElement resp, string name)
    {
        if (resp.ValueKind != JsonValueKind.Object || !resp.TryGetProperty("headers", out var hs) ||
            hs.ValueKind != JsonValueKind.Array) return "";
        foreach (var h in hs.EnumerateArray().Where(h => Str(h, "name").Equals(name, StringComparison.OrdinalIgnoreCase)))
            return Str(h, "value");
        return "";
    }
}
