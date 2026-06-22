using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using SynthWatch.Api.Data;
using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;

namespace SynthWatch.Api.Functions;

public class FlowsFunctions
{
    private readonly SynthWatchDbContext _db;

    public FlowsFunctions(SynthWatchDbContext db) => _db = db;

    /// <summary>
    /// GET /api/flows — the runner-owned flow manifest (name + description + entryUrlHint +
    /// updatedAt), the single source of truth for "what flows exist". Replaces the previous
    /// distinct-flow_name string[] (a stopgap) with the richer object[] shape.
    /// </summary>
    [Function("ListFlows")]
    public async Task<IActionResult> ListFlows(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "flows")] HttpRequest req,
        CancellationToken ct)
    {
        var flows = (await _db.FlowManifests.AsNoTracking()
            .OrderBy(f => f.Name)
            .ToListAsync(ct))
            .Select(FlowDto.From)
            .ToList();

        req.HttpContext.Response.Headers.CacheControl = "public, max-age=30";
        req.HttpContext.Response.Headers["Vary"] = "Origin";
        return ApiResults.Ok(flows);
    }
}
