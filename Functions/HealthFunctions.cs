using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SynthWatch.Api.Data;

namespace SynthWatch.Api.Functions;

/// <summary>
/// Liveness + readiness probe. Anonymous (it's a health probe) and cheap: a single
/// <c>SELECT 1</c> over the managed-identity connection proves the app is up AND can reach the
/// DB. Lets SynthWatch monitor its own API and lets deploys be verified.
/// </summary>
public class HealthFunctions
{
    private readonly SynthWatchDbContext _db;
    private readonly ILogger<HealthFunctions> _logger;

    public HealthFunctions(SynthWatchDbContext db, ILogger<HealthFunctions> logger)
    {
        _db = db;
        _logger = logger;
    }

    [Function("Health")]
    public async Task<IActionResult> Health(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequest req,
        CancellationToken ct)
    {
        try
        {
            // Cheapest possible readiness check; opens/uses the MI-authenticated connection.
            await _db.Database.ExecuteSqlRawAsync("SELECT 1", ct);
            return new OkObjectResult(new { status = "healthy", db = "up" });
        }
        catch (Exception ex)
        {
            // Log the full exception server-side; return a concise, non-sensitive detail.
            HealthLog.DbUnreachable(_logger, ex);
            return new ObjectResult(new
            {
                status = "unhealthy",
                db = "down",
                detail = ex.GetType().Name
            })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable
            };
        }
    }
}

/// <summary>High-performance (CA1848) log delegates for the health probe.</summary>
internal static partial class HealthLog
{
    [LoggerMessage(EventId = 2000, Level = LogLevel.Error,
        Message = "Health check failed: database unreachable")]
    public static partial void DbUnreachable(ILogger logger, Exception ex);
}
