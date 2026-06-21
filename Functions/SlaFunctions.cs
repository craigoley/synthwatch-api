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
    /// GET /api/sla?window=24h|7d|30d — per-check availability from the runner-owned SLA views.
    /// We read the existing views rather than reimplementing the availability calculation.
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

        var rows = (await _db.SlaAvailability
            .FromSqlRaw(sql)
            .AsNoTracking()
            .ToListAsync(ct))
            .Select(SlaDto.From)
            .ToList();

        return ApiResults.Ok(new
        {
            window = string.IsNullOrEmpty(window) ? "24h" : window,
            items = rows
        });
    }
}
