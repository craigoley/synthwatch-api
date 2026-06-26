using System.Text.Json;
using System.Text.Json.Serialization;
using SynthWatch.Api.Dtos;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// The prompt + response mapping for trace AI insights (slice 2) — pure + testable. Asks gpt-5-mini for
/// actionable, categorized insights grounded ONLY in the compact trace summary (slice 1), with HONESTY baked
/// into the system prompt: site-vs-third-party error distinction, SPA Web-Vitals are best-effort (not
/// authoritative), it is NOT a real Lighthouse audit, and "couldn't determine" beats fabrication.
/// </summary>
public static class AiInsights
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Context about the run, so the model can frame insights (and not re-derive them). TraceSource
    /// tells the model WHICH trace it's analyzing — "this run" (a failure, possibly truncated at the break) vs
    /// the monitor's latest success baseline (a complete journey) — so it frames the insights honestly.</summary>
    public readonly record struct RunContext(string CheckName, string? TargetHost, string Status, string TraceSource);

    public const string SystemPrompt = """
        You are a senior web-performance and reliability analyst. A synthetic-monitoring tool captured a
        Playwright trace of a real page load and extracted a COMPACT SUMMARY of it (a network waterfall and a
        FILTERED console log). You are given that summary as JSON — you did NOT see the page itself, only this
        summary. Produce actionable insights about how the MONITORED SITE could be improved.

        Return ONLY a JSON object with EXACTLY this shape:
        {
          "summary": "1-2 plain sentences for an engineer",
          "performance":  [ insight, ... ],   // slow requests, high server wait, large payloads
          "network":      [ insight, ... ],   // third-party weight, uncompressed/cacheable assets, request count
          "errors":       [ insight, ... ],   // the SITE'S real console errors (and how to fix them)
          "suggestions":  [ insight, ... ],   // Lighthouse-STYLE wins (render-blocking, compression, caching)
          "caveats":      [ "short string", ... ]
        }
        Each `insight` is: { "title": short headline, "detail": what + why (1-2 sentences),
          "severity": "high"|"medium"|"low", "confidence": "high"|"medium"|"low",
          "evidence": the SPECIFIC signal from the summary it's based on (a url / timing / console line) }.
        Use [] for a category with nothing to say. Aim for the few highest-value insights, not an exhaustive list.

        HONESTY — these are mandatory and matter more than sounding impressive:
        - The summary's console messages are tagged "origin":"site" vs "third-party". Treat SITE errors as the
          site's own bugs to fix; for third-party ones, say they come from an embedded third-party script and
          the site can only mitigate (defer/remove), not fix. Network requests are likewise tagged third-party.
        - This is NOT a real Lighthouse audit — you did not run Lighthouse and have no Lighthouse score. Frame
          suggestions as "based on the trace's network/console data". NEVER invent a Lighthouse/perf score.
        - Web Vitals (LCP/CLS/INP) are NOT in this summary and are unreliable for SPA soft-navigations — do not
          assert them. Lean on the network waterfall, payload sizes, and console (the solid signals). If a point
          would depend on Web Vitals, add a caveat instead of stating it.
        - Ground every insight in evidence that is literally present in the summary. If the data is thin, say so
          (a caveat or low confidence) — "couldn't determine" beats a confident fabrication.
        """;

    /// <summary>The user message: run context + the compact trace summary as JSON.</summary>
    public static string BuildUser(RunContext run, TraceSignalsDto signals)
    {
        var ctx = JsonSerializer.Serialize(
            new { check = run.CheckName, targetHost = run.TargetHost, status = run.Status, traceSource = run.TraceSource }, Web);
        var summary = JsonSerializer.Serialize(signals, Web);
        return $"""
            Run context: {ctx}
            (traceSource says which trace this is: "this run" = the run itself; otherwise it's the monitor's
            latest successful run — a complete journey. Frame insights for whichever you were given.)

            Trace summary (network + filtered console; console messages carry origin=site|third-party):
            {summary}
            """;
    }

    /// <summary>Map the model's JSON to the categorized DTO. Tolerant of fences/prose; null on a parse failure
    /// (the caller returns a clean "unavailable").</summary>
    public static AiInsightsDto? Parse(string modelContent)
    {
        try
        {
            using var doc = JsonDocument.Parse(ExtractJson(modelContent));
            var root = doc.RootElement;
            return new AiInsightsDto(
                Configured: true,
                Summary: Str(root, "summary"),
                Performance: Category(root, "performance"),
                Network: Category(root, "network"),
                Errors: Category(root, "errors"),
                Suggestions: Category(root, "suggestions"),
                Caveats: Strings(root, "caveats"),
                Note: null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── helpers ──

    /// <summary>Outermost {...}, tolerant of ```json fences / leading-trailing prose (mirrors runner extractJson).</summary>
    internal static string ExtractJson(string content)
    {
        var s = content.Trim();
        var fence = s.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            var start = s.IndexOf('\n', fence);
            var end = s.IndexOf("```", fence + 3, StringComparison.Ordinal);
            if (start >= 0 && end > start) s = s[(start + 1)..end].Trim();
        }
        int first = s.IndexOf('{'), last = s.LastIndexOf('}');
        return first >= 0 && last > first ? s[first..(last + 1)] : s;
    }

    private static List<AiInsightDto> Category(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
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
        if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    // Clamp a free-text severity/confidence to the allowed set, else a default.
    private static string Norm(string? raw, string fallback, params string[] allowed)
    {
        var v = raw?.Trim().ToLowerInvariant();
        return v is not null && Array.Exists(allowed, a => a == v) ? v : fallback;
    }
}
