using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using SynthWatch.Api.Data;
using SynthWatch.Api.Infrastructure;

namespace SynthWatch.Api.Functions;

public class FlowsFunctions
{
    private readonly SynthWatchDbContext _db;

    public FlowsFunctions(SynthWatchDbContext db) => _db = db;

    /// <summary>GET /api/flows — distinct non-null flow_name values.</summary>
    [Function("ListFlows")]
    public async Task<IActionResult> ListFlows(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "flows")] HttpRequest req,
        CancellationToken ct)
    {
        var flows = await _db.Checks.AsNoTracking()
            .Where(c => c.FlowName != null)
            .Select(c => c.FlowName!)
            .Distinct()
            .OrderBy(f => f)
            .ToListAsync(ct);

        return ApiResults.Ok(flows);
    }
}
