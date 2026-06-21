using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Options;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Adds CORS headers when the request Origin matches one of the configured allowed origins
/// (explicit list, never "*"). Preflight (OPTIONS) is answered by <c>CorsPreflightFunction</c>;
/// this middleware decorates every HTTP response (including errors) with the right headers.
/// </summary>
public sealed class CorsMiddleware : IFunctionsWorkerMiddleware
{
    private readonly IReadOnlySet<string> _allowedOrigins;

    public CorsMiddleware(IOptions<CorsOptions> options)
    {
        _allowedOrigins = options.Value.ResolveAllowed();
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        await next(context);

        var http = context.GetHttpContext();
        if (http is null)
        {
            return;
        }

        var origin = http.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) && _allowedOrigins.Contains(origin.TrimEnd('/')))
        {
            var headers = http.Response.Headers;
            // Echo the matched origin (per-origin response); Vary so caches don't cross origins.
            headers["Access-Control-Allow-Origin"] = origin;
            headers["Vary"] = "Origin";
            headers["Access-Control-Allow-Methods"] = "GET,POST,PATCH,DELETE,OPTIONS";
            headers["Access-Control-Allow-Headers"] = "Content-Type,Authorization";
            headers["Access-Control-Max-Age"] = "600";
        }
    }
}
