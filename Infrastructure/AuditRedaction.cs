using System.Text.Json;
using System.Text.Json.Nodes;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Redacts secret-bearing values out of an audit before/after snapshot before it is persisted — so the
/// immutable trail NEVER stores a plaintext secret. Masks by KEY (channel config.url / authHeader; any
/// connectionString / password / token / accessKey), masks recipient emails (j***@d***), and pattern-masks
/// any string that looks like a secret (accesskey=…, Bearer …) regardless of key. Secrets become a
/// stable sha256 FINGERPRINT (comparable across rows, irreversible), never the value.
/// </summary>
public static class AuditRedaction
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // Exact key names (case-insensitive) whose value is a secret to fingerprint.
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "authheader", "url", "connectionstring", "password", "secret", "token", "accesskey", "apikey",
    };

    // Key names whose value is recipient email(s) to mask.
    private static readonly HashSet<string> EmailKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "to", "recipient", "recipients", "email",
    };

    /// <summary>Serialize + redact an object to a jsonb string (null when the object is null). Used by the
    /// audit-write path for before/after snapshots.</summary>
    public static string? RedactToJson(object? value)
    {
        if (value is null)
            return null;
        var node = JsonSerializer.SerializeToNode(value, value.GetType(), Web);
        Walk(node);
        return node?.ToJsonString();
    }

    /// <summary>Redact a JsonNode tree in place and return it (for unit tests).</summary>
    public static JsonNode? Redact(JsonNode? node)
    {
        Walk(node);
        return node;
    }

    private static void Walk(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var child = obj[key];
                    if (SecretKeys.Contains(key))
                        obj[key] = MaskSecret(child);
                    else if (EmailKeys.Contains(key))
                        obj[key] = MaskEmails(child);
                    else if (TryGetString(child, out var s) && LooksSecret(s))
                        obj[key] = Fingerprint(s);
                    else
                        Walk(child);
                }
                break;

            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    if (TryGetString(arr[i], out var s) && LooksSecret(s))
                        arr[i] = Fingerprint(s);
                    else
                        Walk(arr[i]);
                }
                break;
        }
    }

    private static JsonValue? MaskSecret(JsonNode? child)
    {
        if (child is null)
            return null;
        if (TryGetString(child, out var s))
            return s.Length == 0 ? JsonValue.Create(string.Empty) : Fingerprint(s);
        // Non-string secret (object/array under a secret key) → fingerprint its serialized form.
        return Fingerprint(child.ToJsonString());
    }

    private static JsonNode MaskEmails(JsonNode? child)
    {
        if (child is JsonArray arr)
        {
            var masked = new JsonArray();
            foreach (var e in arr)
                masked.Add(JsonValue.Create(TryGetString(e, out var s) ? MaskEmail(s) : "***"));
            return masked;
        }
        return JsonValue.Create(TryGetString(child, out var single) ? MaskEmail(single) : "***");
    }

    private static bool TryGetString(JsonNode? n, out string value)
    {
        if (n is JsonValue v && v.TryGetValue(out string? s) && s is not null)
        {
            value = s;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static JsonValue Fingerprint(string s) => JsonValue.Create($"redacted:sha256:{AuthTokens.Sha256Hex(s)[..12]}");

    private static string MaskEmail(string e)
    {
        var at = e.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at == e.Length - 1)
            return "***";
        return $"{e[0]}***@{e[at + 1]}***";
    }

    private static bool LooksSecret(string s) =>
        s.Contains("accesskey=", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("accountkey=", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) ||
        (s.Contains("endpoint=", StringComparison.OrdinalIgnoreCase) && s.Contains(';', StringComparison.Ordinal));
}
