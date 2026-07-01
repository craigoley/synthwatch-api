using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using SynthWatch.Api.Data;
using SynthWatch.Api.Infrastructure;

namespace SynthWatch.Api.Functions;

/// <summary>
/// GET /api/status — the internal/stakeholder status page (§A3). A curated, PROPERTY-level rollup driven by
/// data that already exists (current check status + SLA + incidents); no new capture. Groups by the `area:`
/// tag, so only curated properties appear and internal/untagged checks are excluded (no leaked internals).
/// Read-only, stays open per the GET default. Returns ONLY property names + states + uptime + incident titles.
/// </summary>
public class StatusFunctions
{
    private readonly SynthWatchDbContext _db;

    public StatusFunctions(SynthWatchDbContext db) => _db = db;

    private const string Window = "30d";

    [Function("GetStatus")]
    public async Task<IActionResult> GetStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "status")] HttpRequest req,
        CancellationToken ct)
    {
        // (1) Current signal per area-tagged ENABLED check: its latest run's status (across locations) +
        //     severity + open-incident severity. LATERAL picks the single most-recent run.
        var checks = await _db.StatusChecks.FromSql(
            $@"SELECT t.value AS property, c.severity AS severity,
                      COALESCE(l.status, 'unknown') AS status,
                      EXISTS (SELECT 1 FROM incidents i WHERE i.check_id = c.id AND i.status = 'open') AS has_open_incident,
                      (SELECT max(i2.severity) FROM incidents i2 WHERE i2.check_id = c.id AND i2.status = 'open') AS open_severity
               FROM checks c
               JOIN check_tags t ON t.check_id = c.id AND t.key = 'area'
               LEFT JOIN LATERAL (
                   SELECT r.status FROM runs r WHERE r.check_id = c.id ORDER BY r.started_at DESC LIMIT 1
               ) l ON true
               WHERE c.enabled").AsNoTracking().ToListAsync(ct);

        // (2) SLA counts per area-tagged enabled check over the 30d view → summed per property for uptime.
        var sla = await _db.StatusSla.FromSql(
            $@"SELECT t.value AS property, s.completed_runs, s.up_runs, s.down_runs
               FROM sla_availability_30d s
               JOIN check_tags t ON t.check_id = s.check_id AND t.key = 'area'
               JOIN checks c ON c.id = s.check_id AND c.enabled").AsNoTracking().ToListAsync(ct);

        // (3) Recent incidents on area-tagged checks — property-level fields only (no id/url exposed).
        var incidents = await _db.StatusIncidents.FromSql(
            $@"SELECT t.value AS property, c.name AS check_name, i.summary, i.opened_at, i.resolved_at, i.status, i.severity
               FROM incidents i
               JOIN checks c ON c.id = i.check_id
               JOIN check_tags t ON t.check_id = c.id AND t.key = 'area'
               ORDER BY i.opened_at DESC
               LIMIT 15").AsNoTracking().ToListAsync(ct);

        var dto = StatusPageProjection.Build(Window, checks, sla, incidents);
        // Current status moves run-to-run → a short cache like /checks.
        req.HttpContext.Response.Headers.CacheControl = "public, max-age=15";
        req.HttpContext.Response.Headers["Vary"] = "Origin";
        return ApiResults.Ok(dto);
    }
}
