using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Options;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Adds CORS headers scoped to the single configured dashboard origin. Preflight (OPTIONS)
/// requests are answered by <c>CorsPreflightFunction</c>; this middleware decorates every
/// HTTP response (including error responses) with the appropriate headers.
/// </summary>
public sealed class CorsMiddleware : IFunctionsWorkerMiddleware
{
    private readonly string _allowedOrigin;

    public CorsMiddleware(IOptions<CorsOptions> options)
    {
        _allowedOrigin = options.Value.AllowedOrigin?.TrimEnd('/') ?? "";
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
        if (!string.IsNullOrEmpty(_allowedOrigin) &&
            string.Equals(origin.TrimEnd('/'), _allowedOrigin, StringComparison.OrdinalIgnoreCase))
        {
            var headers = http.Response.Headers;
            headers["Access-Control-Allow-Origin"] = origin;
            headers["Vary"] = "Origin";
            headers["Access-Control-Allow-Methods"] = "GET,POST,PATCH,DELETE,OPTIONS";
            headers["Access-Control-Allow-Headers"] = "Content-Type,Authorization";
            headers["Access-Control-Max-Age"] = "600";
        }
    }
}
