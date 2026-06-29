using System.Text.Json;
using SynthWatch.Api.Dtos;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// The AOAI comparison over a trace-signals DELTA — "why does this run fail when the last-known-good baseline
/// passed?" Categorizes toward the regional-cause taxonomy and makes the HONEST flakiness call. Pure + testable;
/// reuses the ai-insights AOAI path (AoaiClient) + honesty discipline. Feeds the DIFF (not two full traces).
/// </summary>
public static class LocationDiffInsight
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public const string SystemPrompt = """
        A synthetic-monitoring check FAILED on one run but its last-known-good BASELINE run PASSED. You are given
        the DELTA between their trace signals (NOT the two full traces) — what console errors / network requests
        differ between the failing run and the baseline. Explain the most likely cause of THIS failure.

        ★ The delta is between TWO SINGLE RUNS. The baseline is the monitor's last good run (often, but not
        necessarily, a different region). This is NOT a direct region-vs-region comparison — frame it as
        "this failing run vs the last-known-good run".

        Return ONLY a JSON object with EXACTLY this shape:
        {
          "summary": "1-2 plain sentences for an on-call engineer",
          "likelyCause": one of ["regional-waf-cdn","network-allowlist","geo-dns","region-timeout","third-party-blocked","flaky-transient","undetermined"],
          "confidence": one of ["high","medium","low"],
          "isFlaky": boolean,
          "findings": [ { "title": short, "detail": what+why (1-2 sentences), "severity": "high"|"medium"|"low",
                          "confidence": "high"|"medium"|"low", "evidence": the specific delta line it's based on } ],
          "caveats": [ "short string", ... ]
        }

        likelyCause taxonomy:
        - regional-waf-cdn: a WAF/CDN rule (e.g. a CSP/403/blocked response) differs between the runs.
        - network-allowlist: a request reachable in one run but refused in the other (an IP/region not allow-listed).
        - geo-dns: a host resolved/served differently (a geo-DNS or regional-CDN difference).
        - region-timeout: a request/step that timed out or was far slower in the failing run only.
        - third-party-blocked: an embedded THIRD-PARTY script/host present or erroring in one run but not the other.
        - flaky-transient: the delta is thin/one-off (a single timeout or WebSocket blip, no consistent
          difference) — likely transient, not a real regional issue.
        - undetermined: the delta does not support a confident cause.

        ★ HONESTY (mandatory):
        - If the delta is THIN — a single transient error, a one-off timeout, no consistent region-only signal —
          set isFlaky=true and likelyCause="flaky-transient" and SAY it may be transient. Do NOT fabricate a
          regional root cause the signals don't support. An honest "flaky-transient"/"undetermined" beats a
          confident wrong cause.
        - The delta tags each console line origin=site vs third-party — distinguish the SITE's own errors from
          embedded third-party ones (the site can only mitigate third-party, not fix it).
        - Ground every finding in a delta line that is literally present. "couldn't determine" over invention.
        - Best-effort: this is two single runs, not a statistical comparison.
        """;

    /// <summary>The user message: the two labels + the structured delta as JSON.</summary>
    public static string BuildUser(string failingLabel, string baselineLabel, TraceDiffDto diff)
    {
        var ctx = JsonSerializer.Serialize(new { failing = failingLabel, baseline = baselineLabel }, Web);
        var delta = JsonSerializer.Serialize(diff, Web);
        return $"""
            Comparison: {ctx}

            Delta (console: errors only in the failing run / only in the baseline / shared count, each tagged
            origin=site|third-party; network: per-side totals + failed-host + third-party origin deltas):
            {delta}
            """;
    }

    /// <summary>Map the model JSON to a <see cref="DiffInsight"/>. Tolerant of fences; null on a parse failure.</summary>
    public static DiffInsight? Parse(string modelContent)
    {
        try
        {
            using var doc = JsonDocument.Parse(AiInsights.ExtractJson(modelContent));
            var root = doc.RootElement;
            return new DiffInsight(
                Summary: Str(root, "summary") ?? "",
                LikelyCause: NormCause(Str(root, "likelyCause")),
                Confidence: Norm(Str(root, "confidence"), "low", "high", "medium", "low"),
                IsFlaky: root.TryGetProperty("isFlaky", out var f) && f.ValueKind is JsonValueKind.True or JsonValueKind.False
                    && f.GetBoolean(),
                Findings: Findings(root),
                Caveats: Strings(root, "caveats"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly string[] Causes =
        ["regional-waf-cdn", "network-allowlist", "geo-dns", "region-timeout", "third-party-blocked", "flaky-transient", "undetermined"];

    private static string NormCause(string? raw)
    {
        var v = raw?.Trim().ToLowerInvariant();
        return v is not null && Array.Exists(Causes, c => c == v) ? v : "undetermined";
    }

    private static List<AiInsightDto> Findings(JsonElement root)
    {
        if (!root.TryGetProperty("findings", out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
        var list = new List<AiInsightDto>();
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var title = Str(e, "title");
            if (string.IsNullOrWhiteSpace(title)) continue;
            list.Add(new AiInsightDto(
                Title: title,
                Detail: Str(e, "detail") ?? "",
                Severity: Norm(Str(e, "severity"), "medium", "high", "medium", "low"),
                Confidence: Norm(Str(e, "confidence"), "low", "high", "medium", "low"),
                Evidence: string.IsNullOrWhiteSpace(Str(e, "evidence")) ? null : Str(e, "evidence")));
        }
        return list;
    }

    private static List<string> Strings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
        return arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string Norm(string? raw, string fallback, params string[] allowed)
    {
        var v = raw?.Trim().ToLowerInvariant();
        return v is not null && Array.Exists(allowed, a => a == v) ? v : fallback;
    }
}
