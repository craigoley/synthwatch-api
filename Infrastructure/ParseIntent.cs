using System.Text.Json;
using SynthWatch.Api.Data.Entities;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Chat-to-prefill: turn a free-text request ("set up a ping monitor for meals2go.com") into a structured,
/// NON-BROWSER monitor spec the human reviews + submits. ★ The model only SUGGESTS — every field is run through
/// the SAME CheckValidation.TryBuildNew the real POST /checks uses (validate-don't-trust), and the dashboard
/// PREFILLS the create modal; the human clicks Create. A browser/multistep ask is redirected, never fabricated.
/// </summary>
public static class ParseIntent
{
    public const string SystemPrompt = """
        You extract a SynthWatch MONITOR spec from a user's free-text request, as STRICT JSON ONLY (no prose).

        ★ Only these NON-BROWSER kinds exist — never invent others:
        - "http"  — an HTTP(S) request is up. targetUrl = an absolute http(s) URL.
        - "ssl"   — TLS cert days-to-expiry. targetUrl = an absolute https URL (e.g. https://example.com), NOT a bare host. certExpiryWarnDays optional (>0, default 30).
        - "dns"   — a DNS record resolves. targetUrl = a host. netConfig.recordType ∈ [A,AAAA,CNAME,MX,TXT,NS] (default A); netConfig.expectedValue optional.
        - "tcp"   — a TCP PORT is open. targetUrl = a host (or host:port). netConfig.port is REQUIRED (or put it in targetUrl as host:port).
        - "ping"  — REACHABILITY over TCP (this is NOT ICMP ping): the host responds on a TCP port. targetUrl = a host. netConfig.port optional (default 443).

        ★ "ping"/"reachable"/"is it up (no URL)" → kind "ping" (TCP-reachability). Never describe it as ICMP.
        ★ A BROWSER / multistep / "checkout flow" / "log in and click" request is NOT supported here (those are
          authored as code) → return {"redirect":"browser","reason":"Browser monitors are authored as code in the monitors repo, then set up from the Catalog."} and NOTHING else.

        Output a SINGLE JSON object with EXACTLY these keys (omit a key only by setting it null):
        {
          "redirect": null | "browser" | "unsupported",
          "reason": null | "<short message when redirect>",
          "name": "<a short human monitor name you suggest, e.g. 'meals2go reachability'>",
          "kind": "http" | "ssl" | "dns" | "tcp" | "ping",
          "targetUrl": "<url for http/ssl, or host / host:port for dns/tcp/ping>",
          "intervalSeconds": null | <int>,
          "timeoutMs": null | <int>,
          "certExpiryWarnDays": null | <int>,
          "netConfig": null | { "recordType": null|"A".., "expectedValue": null|"<v>", "port": null|<int> },
          "notes": null | "<one short line on what you inferred / what's missing>"
        }

        Rules: emit netConfig ONLY for dns/tcp/ping (never for http/ssl). NEVER emit flowName, sourceKey, or
        specPath. If a host is missing, fill what you can and leave targetUrl null — the human completes it.
        Respond with the JSON object ONLY.
        """;

    public static string BuildUser(string text) => $"Request:\n{text}";

    /// <summary>The model's raw suggestion — flat, mapping straight onto CreateCheckRequest fields (+ redirect).</summary>
    public sealed class Suggestion
    {
        public string? Redirect { get; set; }
        public string? Reason { get; set; }
        public string? Notes { get; set; }
        public string? Name { get; set; }
        public string? Kind { get; set; }
        public string? TargetUrl { get; set; }
        public int? IntervalSeconds { get; set; }
        public int? TimeoutMs { get; set; }
        public int? CertExpiryWarnDays { get; set; }
        public NetConfig? NetConfig { get; set; }
    }

    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);

    /// <summary>Parse the model JSON (tolerant of code fences); null on a parse failure.</summary>
    public static Suggestion? Parse(string modelContent)
    {
        try { return JsonSerializer.Deserialize<Suggestion>(AiInsights.ExtractJson(modelContent), Opts); }
        catch (JsonException) { return null; }
    }
}
