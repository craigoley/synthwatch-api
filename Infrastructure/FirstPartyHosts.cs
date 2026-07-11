namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// The Wegmans first-party host allowlist (Error-diff P1) — THE shared answer to "is this host first-party
/// (Wegmans) or third-party?", used by <see cref="TraceExtractor"/> to label console origins + network
/// requests. FAITHFUL-PORTED from the runner (synthwatch runner/firstPartyHosts.ts) — keep the two
/// byte-identical; the shared trace-signals golden fixture guards their agreement.
///
/// WHY this replaces the old exact-target-host rule (IsSite): the classifier used to call a host first-party
/// ONLY when it equalled the check's target host or was a subdomain of it. That misread the whole Wegmans
/// estate — a SIBLING subdomain of the target (images.wegmans.com when the check targets www.wegmans.com),
/// anything on *.wegmans.cloud, the Azure APIM gateway, and the wegapi/kitting backends all fell through to
/// "third-party". The allowlist is Wegmans-owned domains (apex + any subdomain) PLUS the Azure APIM gateway
/// (*.azure-api.net) and any host whose name contains `wegapi` / `kitting`. The substring families are a
/// deliberate heuristic (a bare host name, not an ownership proof) — good enough for a first-party/third-party
/// DISPLAY label, not a security boundary.
/// </summary>
public static class FirstPartyHosts
{
    // host == domain OR host is a subdomain of domain (`*.domain`). Both args already lowercased.
    private static bool IsApexOrSub(string host, string domain) =>
        host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

    /// <summary>
    /// Is <paramref name="host"/> a Wegmans first-party host, independent of any particular check's target?
    /// Case-insensitive. Empty host → false (blob:/data:/about: resources have no host → third-party).
    /// </summary>
    public static bool IsWegmansHost(string host)
    {
        if (host.Length == 0) return false;
        var h = host.ToLowerInvariant();
        // Wegmans-owned domains: apex + every subdomain (www/images/preview.commerce/… all first-party).
        if (IsApexOrSub(h, "wegmans.com")) return true;
        if (IsApexOrSub(h, "wegmans.cloud")) return true;
        // Azure API Management gateway (all *.azure-api.net — the estate's APIM lives here).
        if (h.EndsWith(".azure-api.net", StringComparison.Ordinal)) return true;
        // Backend API families by name (wegapi = the storefront API, kitting = kitting/catering-api).
        if (h.Contains("wegapi", StringComparison.Ordinal)) return true;
        if (h.Contains("kitting", StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// First-party for THIS check: a Wegmans allowlist host, OR the check's own target host / a subdomain of
    /// it (keeps a monitor whose target is NOT in the static allowlist treating its own site as first-party).
    /// Empty host → false. Case-insensitive.
    ///
    /// ★ This is the RESOURCE-host classifier: callers pass the host of the RESOURCE the error/request is
    /// about (the request URL's host; or, for a console error, the host parsed out of the error text) — NOT
    /// the frame that logged it. That's what fixes the CSP-violation case (a third-party resource refused by
    /// the site frame was read as origin:'site' when keyed off the frame).
    /// </summary>
    public static bool IsFirstParty(string host, string? target)
    {
        if (host.Length == 0) return false;
        var h = host.ToLowerInvariant();
        if (IsWegmansHost(h)) return true;
        if (!string.IsNullOrEmpty(target))
        {
            var t = target.ToLowerInvariant();
            if (h == t || h.EndsWith("." + t, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
