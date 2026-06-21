namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// CORS configuration. The allowed origin is the Vercel dashboard URL, supplied via the
/// <c>Cors:AllowedOrigin</c> app setting. We never reflect arbitrary origins and never use "*".
/// </summary>
public class CorsOptions
{
    public string AllowedOrigin { get; set; } = "";
}
