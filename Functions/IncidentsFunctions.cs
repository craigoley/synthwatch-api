using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using SynthWatch.Api.Data;
using SynthWatch.Api.Data.Entities;
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

        // LEFT JOIN to checks for the incident's check name/kind (dashboard parity). NOT an inner
        // join: an incident whose check is missing/deleted must still surface — an incident must
        // never silently vanish because of its check (checkName/checkKind are null in that case).
        // Accessing the required nav i.Check in a projection generates an INNER JOIN; the explicit
        // GroupJoin + DefaultIfEmpty forces the LEFT JOIN.
        var rows = await (
            from i in query
            join c in _db.Checks.AsNoTracking() on i.CheckId equals c.Id into cj
            from c in cj.DefaultIfEmpty()
            orderby (i.Status == "open" ? 0 : 1), i.OpenedAt descending
            select new { Incident = i, Name = c != null ? c.Name : null, Kind = c != null ? c.Kind : null })
            .ToListAsync(ct);
        var incidents = rows
            .Select(x => IncidentDto.From(x.Incident, x.Name, x.Kind))
            .ToList();

        return ApiResults.Ok(incidents);
    }

    /// <summary>
    /// GET /api/incidents/{id} — one incident enriched for the investigation detail page. 404 if no
    /// such incident. checkName/checkKind are null if the check is missing (defensive LEFT-join).
    ///
    /// Timeline window: runs for the incident's check with started_at in
    /// [opened_at, COALESCE(resolved_at, now())] (ASC), PLUS a lead of up to
    /// min(consecutiveFailures, 10) runs immediately before opened_at (the failure streak that
    /// opened the incident) — merged ASC. An OPEN incident's window runs through now().
    /// </summary>
    [Function("GetIncident")]
    public async Task<IActionResult> GetIncident(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "incidents/{id:long}")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        var inc = await _db.Incidents.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
        if (inc is null)
            return ApiResults.NotFound($"Incident {id} not found.");

        // LEFT-join equivalent: the check may be gone (don't drop the incident).
        var check = await _db.Checks.AsNoTracking()
            .Where(c => c.Id == inc.CheckId)
            .Select(c => new { c.Name, c.Kind })
            .FirstOrDefaultAsync(ct);

        var to = inc.ResolvedAt ?? DateTimeOffset.UtcNow;

        // Core window: runs during the incident.
        var core = await _db.Runs.AsNoTracking()
            .Where(r => r.CheckId == inc.CheckId && r.StartedAt >= inc.OpenedAt && r.StartedAt <= to)
            .ToListAsync(ct);

        // Lead: the failure streak just before opened_at (cap 10) — cheap context for the page.
        var leadCount = Math.Clamp(inc.ConsecutiveFailures, 0, 10);
        var lead = leadCount == 0
            ? new List<Run>()
            : await _db.Runs.AsNoTracking()
                .Where(r => r.CheckId == inc.CheckId && r.StartedAt < inc.OpenedAt)
                .OrderByDescending(r => r.StartedAt)
                .Take(leadCount)
                .ToListAsync(ct);

        var timeline = lead.Concat(core)
            .OrderBy(r => r.StartedAt)
            .Select(TimelineEntryDto.From)
            .ToList();

        // Per-location current status of the check (latest run per location).
        var perLocation = (await _db.Runs.AsNoTracking()
            .Where(r => r.CheckId == inc.CheckId)
            .GroupBy(r => r.Location)
            .Select(g => g.OrderByDescending(r => r.StartedAt).First())
            .ToListAsync(ct))
            .OrderBy(r => r.Location, StringComparer.Ordinal)
            .Select(r => new LocationStatusDto(r.Location, r.Status))
            .ToList();

        // Recurrence: recent incidents on the same check, newest first, excluding this one.
        var recurrence = (await _db.Incidents.AsNoTracking()
            .Where(x => x.CheckId == inc.CheckId && x.Id != inc.Id)
            .OrderByDescending(x => x.OpenedAt)
            .Take(10)
            .ToListAsync(ct))
            .Select(x => new RecurrenceDto(x.Id, x.OpenedAt, x.ResolvedAt, x.Status, x.Summary))
            .ToList();

        var detail = new IncidentDetailDto(
            inc.Id, inc.CheckId, check?.Name, check?.Kind, inc.Status, inc.Severity,
            inc.OpenedAt, inc.ResolvedAt,
            DurationSeconds: inc.ResolvedAt is null ? null : (inc.ResolvedAt.Value - inc.OpenedAt).TotalSeconds,
            inc.ConsecutiveFailures, inc.Summary, inc.Rca, perLocation, timeline, recurrence);

        req.HttpContext.Response.Headers.CacheControl = "public, max-age=10";
        req.HttpContext.Response.Headers["Vary"] = "Origin";
        return ApiResults.Ok(detail);
    }
}
