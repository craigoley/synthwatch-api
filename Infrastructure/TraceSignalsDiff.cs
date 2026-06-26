using System.Text.RegularExpressions;
using SynthWatch.Api.Dtos;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Pure diff over two runs' <see cref="TraceSignalsDto"/> (same schema whether on-demand-extracted or
/// persisted by #114) — the data layer for the location comparison ("why does this location fail when the
/// other passes"). The DELTA (not two full traces) is what the AI layer explains.
///
/// ★ CANONICALIZATION is load-bearing: real console messages carry per-run noise (random WebSocket ids,
/// <c>?auid=…</c> query strings, ISO timestamps). Without stripping it, the SAME error reads as different on
/// every run and the diff is all-noise. Proven on the real eastus2/centralus run pair (the doubleclick-CSP +
/// astutebot-WebSocket errors are identical once canonicalized — NOT a location difference).
/// </summary>
public static partial class TraceSignalsDiff
{
    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}t[\d:.]+z?", RegexOptions.IgnoreCase)]
    private static partial Regex IsoTimestamp();
    [GeneratedRegex(@"\?[^\s'""\)]*")]
    private static partial Regex QueryString();
    [GeneratedRegex(@"[a-z0-9_-]{12,}", RegexOptions.IgnoreCase)]
    private static partial Regex LongToken();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static TraceDiffDto Diff(TraceSignalsDto a, TraceSignalsDto b, string labelA, string labelB) =>
        new(labelA, labelB, DiffConsoleOf(a.Console, b.Console), DiffNetworkOf(a.Network, b.Network));

    // ── console: group by canonical signature; report only-in-A / only-in-B / shared count ──
    private static DiffConsole DiffConsoleOf(ConsoleSummaryDto a, ConsoleSummaryDto b)
    {
        var aBy = GroupBySignature(a.Messages);
        var bBy = GroupBySignature(b.Messages);

        var onlyA = aBy.Where(kv => !bBy.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
        var onlyB = bBy.Where(kv => !aBy.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
        var shared = aBy.Count(kv => bBy.ContainsKey(kv.Key));
        return new DiffConsole(onlyA, onlyB, shared);
    }

    // First occurrence of each signature wins as the representative line.
    private static Dictionary<string, DiffConsoleLine> GroupBySignature(IReadOnlyList<ConsoleMessageDto> msgs)
    {
        var map = new Dictionary<string, DiffConsoleLine>(StringComparer.Ordinal);
        foreach (var m in msgs)
        {
            var sig = m.Level + "|" + Canonicalize(m.Text);
            if (!map.ContainsKey(sig))
                map[sig] = new DiffConsoleLine(m.Level, m.Origin, m.Text);
        }
        return map;
    }

    /// <summary>Strip per-run noise so the SAME error matches across runs: lowercase, drop ISO timestamps +
    /// query strings, collapse long id/hash tokens, normalise whitespace.</summary>
    public static string Canonicalize(string text)
    {
        var s = text.ToLowerInvariant();
        s = IsoTimestamp().Replace(s, "");
        s = QueryString().Replace(s, "");
        s = LongToken().Replace(s, "*");
        return Whitespace().Replace(s, " ").Trim();
    }

    // ── network: aggregate deltas + failed-host / third-party set deltas ──
    private static DiffNetwork DiffNetworkOf(NetworkSummaryDto a, NetworkSummaryDto b)
    {
        var failedA = FailedHosts(a);
        var failedB = FailedHosts(b);
        var tpA = a.TopThirdParties.Select(t => t.Host).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tpB = b.TopThirdParties.Select(t => t.Host).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new DiffNetwork(
            TotalRequestsA: a.TotalRequests, TotalRequestsB: b.TotalRequests,
            WireKbA: a.WireKb, WireKbB: b.WireKb,
            ThirdPartyCountA: a.ThirdPartyCount, ThirdPartyCountB: b.ThirdPartyCount,
            FailedCountA: a.Failed.Count, FailedCountB: b.Failed.Count,
            FailedHostsOnlyInA: failedA.Except(failedB, StringComparer.OrdinalIgnoreCase).ToList(),
            FailedHostsOnlyInB: failedB.Except(failedA, StringComparer.OrdinalIgnoreCase).ToList(),
            ThirdPartyOnlyInA: a.TopThirdParties.Where(t => !tpB.Contains(t.Host)).ToList(),
            ThirdPartyOnlyInB: b.TopThirdParties.Where(t => !tpA.Contains(t.Host)).ToList());
    }

    private static HashSet<string> FailedHosts(NetworkSummaryDto n) =>
        n.Failed.Select(r => HostOf(r.Url)).Where(h => h.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "";
}
