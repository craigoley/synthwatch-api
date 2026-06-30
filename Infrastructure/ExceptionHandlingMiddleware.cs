using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Catches unhandled exceptions (notably DB exceptions) and returns a generic 500 so raw
/// database/internal errors never leak to clients. The real exception is logged server-side.
/// </summary>
public sealed class ExceptionHandlingMiddleware : IFunctionsWorkerMiddleware
{
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(ILogger<ExceptionHandlingMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            ExceptionLog.Unhandled(_logger, context.FunctionDefinition.Name, ex);

            var http = context.GetHttpContext();
            if (http is null)
            {
                throw; // Non-HTTP trigger — let the host handle it.
            }

            if (http.Response.HasStarted)
            {
                throw;
            }

            http.Response.Clear();
            http.Response.StatusCode = StatusCodes.Status500InternalServerError;
            // RFC 9457 problem+json. ★ instance = the invocation id, so the opaque 500 a user/dashboard sees
            // carries a correlation id they can quote (it was previously only in the server log). Backward-
            // compatible: ProblemResults keeps error/message, which the dashboard's error path reads.
            await http.Response.WriteAsJsonAsync(
                ProblemResults.Body(StatusCodes.Status500InternalServerError, "Internal Server Error",
                    "An unexpected error occurred.", context.InvocationId, "internal_error"),
                options: null, contentType: ProblemResults.ContentType);
        }
    }
}

/// <summary>High-performance (CA1848) log delegates for unhandled exceptions.</summary>
internal static partial class ExceptionLog
{
    [LoggerMessage(EventId = 3000, Level = LogLevel.Error,
        Message = "Unhandled exception in {Function}")]
    public static partial void Unhandled(ILogger logger, string function, Exception ex);
}
