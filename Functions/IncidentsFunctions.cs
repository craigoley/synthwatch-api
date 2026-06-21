using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using SynthWatch.Api.Data;
using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;

namespace SynthWatch.Api.Functions;

public class IncidentsFunctions
{
    private readonly SynthWatchDbContext _db;

    public IncidentsFunctions(SynthWatchDbContext db) => _db = db;

    /// <summary>
    /// GET /api/incidents — open + resolved incidents. Optional ?status=open|resolved filter
    /// and ?checkId= filter. Open incidents first, then most recently opened.
    /// </summary>
    [Function("ListIncidents")]
    public async Task<IActionResult> ListIncidents(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "incidents")] HttpRequest req,
        CancellationToken ct)
    {
        var query = _db.Incidents.AsNoTracking();

        var status = req.Query["status"].ToString();
        if (!string.IsNullOrEmpty(status))
        {
            if (status is not ("open" or "resolved"))
                return ApiResults.BadRequest("status must be 'open' or 'resolved'.");
            query = query.Where(i => i.Status == status);
        }

        if (long.TryParse(req.Query["checkId"], out var checkId))
            query = query.Where(i => i.CheckId == checkId);

        // Join to checks for the incident's check name/kind (dashboard parity).
        var incidents = (await query
            .OrderBy(i => i.Status == "open" ? 0 : 1)
            .ThenByDescending(i => i.OpenedAt)
            .Select(i => new { Incident = i, i.Check!.Name, i.Check!.Kind })
            .ToListAsync(ct))
            .Select(x => IncidentDto.From(x.Incident, x.Name, x.Kind))
            .ToList();

        return ApiResults.Ok(incidents);
    }
}
