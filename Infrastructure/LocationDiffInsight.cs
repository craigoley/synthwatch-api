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
        A monitored synthetic check FAILED. Identify WHICH LAYER failed, for an on-call engineer: the SITE/action
        under test, the MONITOR's own verification code, or a TRANSIENT blip. Work in THIS ORDER and ground every
        claim ONLY in the evidence provided (sections 1-3 of the user message) — no speculation beyond it.

        STEP 1 — Read THE FAILED ASSERTION (section 1): what did the monitor assert, and how did it fail?
          A JavaScript/TypeScript error in the assertion itself — "Cannot read properties of undefined", a
          TypeError, .toBeNull()/.toBe()/expect(...) on an undefined value, a waitForResponse that resolved to
          null or timed out — is a MONITOR/VERIFICATION BUG, not a site failure. The site didn't break; the CHECK
          did (it tried to read a result it never captured).
        STEP 2 — Read WHAT HAPPENED TO THE ACTION UNDER TEST (section 2: the mutating requests + their status).
          ★ If the action's request returned 2xx, THE ACTION SUCCEEDED. An assertion failure ON TOP of a 2xx
            action is a MONITOR/VERIFICATION BUG — the verification missed a result that was actually there. Do
            NOT call this "transient" and do NOT call it a site failure: the network proves the action worked.
          If the action's request returned 4xx/5xx, or never fired (no matching mutation), that is a SITE/ACTION
            FAILURE — the site genuinely rejected or did not perform the action.
        STEP 3 — ONLY if steps 1-2 are inconclusive, use the BASELINE COLOR (section 3) for a genuine
          site/environment change (a region-only error, a newly-failing host). Request-COUNT deltas alone (e.g.
          "13 fewer third-party requests") are NOT a failure cause — third-party counts vary run to run.
        STEP 4 — Conclude with the VERDICT.

        Return ONLY a JSON object with EXACTLY this shape:
        {
          "verdict": one of ["site-failure","monitor-verification-bug","transient","undetermined"],
          "summary": "1-2 plain sentences for an on-call engineer — lead with the verdict and the decisive fact",
          "likelyCause": one of ["site-failure","monitor-verification-bug","regional-waf-cdn","network-allowlist","geo-dns","region-timeout","third-party-blocked","flaky-transient","undetermined"],
          "confidence": one of ["high","medium","low"],
          "isFlaky": boolean,
          "findings": [ { "title": short, "detail": what+why (1-2 sentences), "severity": "high"|"medium"|"low",
                          "confidence": "high"|"medium"|"low", "evidence": the specific section 1/2/3 line it is based on } ],
          "caveats": [ "short string", ... ]
        }

        ★ GROUNDING RULES (mandatory):
        - verdict="transient" / isFlaky=true is ALLOWED ONLY when the evidence supports it: an intermittent or
          one-off network error, AND no 2xx proving the action worked, AND no spec-code error. A 2xx on the
          action-under-test ARGUES AGAINST transient (the action worked) — prefer monitor-verification-bug.
        - When the action under test SUCCEEDED (a 2xx mutation) but the assertion failed, the LEADING hypothesis
          is a MONITOR/VERIFICATION bug. Defaulting to "flaky/transient" for this case is WRONG.
        - Ground every finding in a literal line from sections 1-3. An honest "undetermined" beats a confident
          wrong cause. The baseline delta is secondary color, NEVER the headline.
        """;

    /// <summary>
    /// The DISTILLED, DECOMPOSED user message — leads with the FAILED ASSERTION (section 1) and the ACTION's
    /// network result (section 2), with the baseline delta demoted to secondary color (section 3). This is the
    /// fix for the "reasoned from request-count diffs, never looked at the actual failure" problem: the decisive
    /// signals come first, and the whole block stays small (research: distill, don't dump). <paramref name="failingNetwork"/>
    /// supplies the mutations + failed/total counts; assertion text + URLs must already be redacted by the caller
    /// for sensitive monitors.
    /// </summary>
    public static string BuildUser(
        string failingLabel, string baselineLabel, string runStatus, string? failedStep, string? assertionError,
        NetworkSummaryDto failingNetwork, TraceDiffDto diff)
    {
        var mutations = failingNetwork.Mutations.Count > 0
            ? string.Join("\n", failingNetwork.Mutations.Select(m => $"- {m.Method} {TrimUrl(m.Url)} → {m.Status}"))
            : "(no mutating request captured in this run's trace)";
        var failed = failingNetwork.Failed.Count > 0
            ? $"{failingNetwork.Failed.Count}: " + string.Join(", ", failingNetwork.Failed.Select(f => $"{f.Status} {HostOnly(f.Url)}"))
            : "none";
        var delta = JsonSerializer.Serialize(diff, Web);

        return $"""
            Comparison: {failingLabel} vs {baselineLabel}

            ## 1. THE FAILED ASSERTION (the primary signal — start here)
            Run status: {runStatus}
            Failing step: {Or(failedStep, "(not recorded)")}
            Assertion error: {Or(assertionError, "(not recorded)")}

            ## 2. WHAT HAPPENED TO THE ACTION UNDER TEST (this run's network result)
            Mutating requests (POST/PUT/PATCH/DELETE) + the status the SITE returned:
            {mutations}
            Failed requests (status >= 400) this run: {failed}
            Total requests this run: {failingNetwork.TotalRequests}

            ## 3. BASELINE COLOR (secondary — only to explain a genuine site/environment change)
            Delta vs the last-known-good baseline (console errors only-in-each + shared count; network footprint
            totals + failed-host + third-party deltas). Count differences alone are NOT a failure cause:
            {delta}

            Decide the verdict per the steps + grounding rules. Lead the summary with the verdict + the decisive fact.
            """;
    }

    private static string Or(string? s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s.Trim();

    /// <summary>Trim a URL to host + path (drop the query string) so mutation lines stay compact + leak-resistant.</summary>
    private static string TrimUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Host}{u.AbsolutePath}" : url;

    private static string HostOnly(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    /// <summary>Map the model JSON to a <see cref="DiffInsight"/>. Tolerant of fences; null on a parse failure.</summary>
    public static DiffInsight? Parse(string modelContent)
    {
        try
        {
            using var doc = JsonDocument.Parse(AiInsights.ExtractJson(modelContent));
            var root = doc.RootElement;
            return new DiffInsight(
                Summary: Str(root, "summary") ?? "",
                Verdict: Norm(Str(root, "verdict"), "undetermined", "site-failure", "monitor-verification-bug", "transient", "undetermined"),
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
        ["site-failure", "monitor-verification-bug", "regional-waf-cdn", "network-allowlist", "geo-dns",
         "region-timeout", "third-party-blocked", "flaky-transient", "undetermined"];

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
