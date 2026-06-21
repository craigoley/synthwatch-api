using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using SynthWatch.Api.Data;
using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;

namespace SynthWatch.Api.Functions;

public class RunsFunctions
{
    private readonly SynthWatchDbContext _db;

    public RunsFunctions(SynthWatchDbContext db) => _db = db;

    /// <summary>GET /api/runs/{id}/steps — ordered funnel steps for a run.</summary>
    [Function("ListRunSteps")]
    public async Task<IActionResult> ListRunSteps(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "runs/{id:long}/steps")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        if (!await _db.Runs.AnyAsync(r => r.Id == id, ct))
            return ApiResults.NotFound($"Run {id} not found.");

        var steps = (await _db.RunSteps.AsNoTracking()
            .Where(s => s.RunId == id)
            .OrderBy(s => s.StepIndex)
            .ToListAsync(ct))
            .Select(RunStepDto.From)
            .ToList();

        return ApiResults.Ok(steps);
    }
}
