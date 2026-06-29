using SynthWatch.Api.Data.Entities;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Channel write-validation: type allowlist, per-type config shape, and the no-transport-secret rule
/// (config holds delivery TARGETS only; the ACS connection string / storage key stays in runner env).
/// </summary>
public static class AlertingValidation
{
    public static readonly string[] ChannelTypes = { "email", "webhook" };

    /// <summary>Returns an error message, or null if the channel write is valid.</summary>
    public static string? ValidateChannel(string? name, string? type, ChannelConfig? config)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "name is required.";
        if (string.IsNullOrWhiteSpace(type) || Array.IndexOf(ChannelTypes, type) < 0)
            return "type must be one of: email, webhook.";

        var cfg = config ?? new ChannelConfig();

        // The transport secret (ACS connection string, storage key) lives in runner env — never here.
        var marker = FindSecretMarker(cfg);
        if (marker is not null)
            return $"config must not contain a connection string / transport secret (found '{marker}'); config holds delivery TARGETS only — the secret stays in runner env.";

        switch (type)
        {
            case "email":
                // 'to' (recipients) only — the sender ('from') is transport env (ALERT_EMAIL_FROM), not
                // a channel field; a stale 'from' key in the body is harmlessly ignored (not modeled).
                if (cfg.To is null || cfg.To.Count == 0 || cfg.To.Any(string.IsNullOrWhiteSpace))
                    return "email channel config requires a non-empty 'to' list.";
                break;
            case "webhook":
                if (string.IsNullOrWhiteSpace(cfg.Url))
                    return "webhook channel config requires 'url'.";
                break;
        }
        return null;
    }

    // Unambiguous connection-string / secret markers (case-insensitive). Catches the ACS connection
    // string (…;accesskey=…) and Azure storage keys; does NOT trip on a webhook bearer token in
    // authHeader (allowed) or ordinary email/URL targets.
    private static readonly string[] SecretMarkers = { "accesskey=", "accountkey=", "connectionstring" };

    private static string? FindSecretMarker(ChannelConfig cfg)
    {
        foreach (var value in ConfigStrings(cfg))
        {
            if (value is null) continue;
            var lower = value.ToLowerInvariant();
            foreach (var marker in SecretMarkers.Where(marker => lower.Contains(marker, StringComparison.Ordinal)))
                return marker;
        }
        return null;
    }

    private static IEnumerable<string?> ConfigStrings(ChannelConfig c)
    {
        yield return c.Url;
        yield return c.AuthHeader;
        if (c.To is not null)
            foreach (var t in c.To) yield return t;
    }
}
