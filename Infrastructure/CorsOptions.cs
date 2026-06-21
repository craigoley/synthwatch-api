namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// CORS configuration. Allowed origins are explicit (never "*"). Supports multiple origins so
/// production AND Vercel preview deployments can both call the API.
/// </summary>
public class CorsOptions
{
    /// <summary>Comma-separated list of allowed origins (preferred). App setting: Cors__AllowedOrigins.</summary>
    public string AllowedOrigins { get; set; } = "";

    /// <summary>Legacy single origin (app setting Cors__AllowedOrigin). Honored as a fallback.</summary>
    public string AllowedOrigin { get; set; } = "";

    /// <summary>Parsed, normalized (trimmed, no trailing slash) set of allowed origins.</summary>
    public IReadOnlySet<string> ResolveAllowed()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in $"{AllowedOrigins},{AllowedOrigin}".Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            set.Add(raw.TrimEnd('/'));
        }
        return set;
    }
}
