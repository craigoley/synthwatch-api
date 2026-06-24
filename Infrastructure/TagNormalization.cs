using System.Text.RegularExpressions;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Tag normalization — REPLICATES the runner's tags.ts (#84) in C# across the language boundary so the
/// API write-path produces tags identical to the runner's setCheckTags/tags-as-code sync. Rule (also
/// enforced by the check_tags DB CHECKs): lowercase, trim, collapse internal whitespace to '_'. Applied
/// to BOTH key and value (Datadog-style — prevents Prod/prod drift). A tag whose value is empty after
/// normalization is dropped (a tag must carry a value); key may be '' (a bare value).
/// </summary>
public static partial class TagNormalization
{
    /// <summary>Suggested-canonical tag keys (SUGGESTIONS, not enforced) — mirrors #84's SUGGESTED_TAG_KEYS.</summary>
    public static readonly string[] SuggestedTagKeys = { "env", "service", "team", "criticality" };

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static string NormalizeField(string? s) =>
        Whitespace().Replace((s ?? "").Trim().ToLowerInvariant(), "_");

    /// <summary>Normalize a tag; null when the value is empty after normalization (drop it).</summary>
    public static (string Key, string Value)? NormalizeTag(string? key, string? value)
    {
        var k = NormalizeField(key);
        var v = NormalizeField(value);
        return string.IsNullOrEmpty(v) ? null : (k, v);
    }
}
