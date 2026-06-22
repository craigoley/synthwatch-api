using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using SynthWatch.Api.Data;
using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;

namespace SynthWatch.Api.Functions;

public class SlaFunctions
{
    private readonly SynthWatchDbContext _db;

    public SlaFunctions(SynthWatchDbContext db) => _db = db;

    /// <summary>
    /// GET /api/sla?window=24h|7d|30d — per-check availability from the runner-owned SLA views,
    /// plus a run-weighted fleet rollup. Per-check and fleet carry an <c>insufficientData</c> flag
    /// (with a null percentage) when the window lacks enough data to report a trustworthy number.
    /// </summary>
    [Function("GetSla")]
    public async Task<IActionResult> GetSla(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sla")] HttpRequest req,
        CancellationToken ct)
    {
        var window = req.Query["window"].ToString();

        // Map the window to a fixed view name — a closed allowlist, so no SQL injection surface.
        string? sql = window switch
        {
            "" or "24h" => "SELECT * FROM sla_availability_24h",
            "7d" => "SELECT * FROM sla_availability_7d",
            "30d" => "SELECT * FROM sla_availability_30d",
            _ => null
        };

        if (sql is null)
            return ApiResults.BadRequest("window must be one of: 24h, 7d, 30d.");

        var rows = await _db.SlaAvailability.FromSqlRaw(sql).AsNoTracking().ToListAsync(ct);

        // Each check's created_at decides how much of the window it could have covered.
        var createdById = await _db.Checks.AsNoTracking()
            .ToDictionaryAsync(c => c.Id, c => c.CreatedAt, ct);

        // Pure projection (items + run-weighted fleet + insufficient-data); see SlaProjection.
        var projection = SlaProjection.Build(rows, createdById);

        // SLA aggregates change slowly (windowed availability); cache a bit longer than /checks.
        // Vary on Origin: publicly cacheable + per-origin platform CORS header.
        req.HttpContext.Response.Headers.CacheControl = "public, max-age=30";
        req.HttpContext.Response.Headers["Vary"] = "Origin";
        return ApiResults.Ok(new SlaResponseDto(
            Window: string.IsNullOrEmpty(window) ? "24h" : window,
            Fleet: projection.Fleet,
            Items: projection.Items));
    }
}
