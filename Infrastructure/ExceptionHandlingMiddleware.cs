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
            _logger.LogError(ex, "Unhandled exception in {Function}", context.FunctionDefinition.Name);

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
            await http.Response.WriteAsJsonAsync(new
            {
                error = "internal_error",
                message = "An unexpected error occurred."
            });
        }
    }
}
