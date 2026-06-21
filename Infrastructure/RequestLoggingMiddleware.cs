using System.Diagnostics;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Structured per-request logging (method / path / status / duration) to App Insights.
/// Outermost middleware so it times the whole pipeline and observes the final status code.
/// Uses LoggerMessage source-gen delegates (CA1848) for low-overhead structured logging.
/// </summary>
public sealed class RequestLoggingMiddleware : IFunctionsWorkerMiddleware
{
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(ILogger<RequestLoggingMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var http = context.GetHttpContext();
        if (http is null)
        {
            await next(context); // non-HTTP trigger — nothing to time/log here.
            return;
        }

        var sw = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            var elapsedMs = (long)Stopwatch.GetElapsedTime(sw).TotalMilliseconds;
            RequestLog.Completed(
                _logger,
                http.Request.Method,
                http.Request.Path.Value ?? "/",
                http.Response.StatusCode,
                elapsedMs);
        }
    }
}

/// <summary>High-performance (CA1848) log delegates for request logging.</summary>
internal static partial class RequestLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information,
        Message = "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs}ms")]
    public static partial void Completed(
        ILogger logger, string method, string path, int statusCode, long elapsedMs);
}
