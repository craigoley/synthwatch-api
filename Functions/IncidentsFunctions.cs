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

    // Incidents are SPARSE compared to runs (one per failure episode, not ~hundreds/day), so the
    // default look-back is wider — 30d gives a useful recent window of resolved incidents without a
    // 7d window that would usually read empty. Same cursor + page-size contract as runs.
    private static readonly TimeSpan IncidentWindow = TimeSpan.FromDays(30);

    /// <summary>
    /// GET /api/incidents — cursor-paginated incidents over a bounded date-range window. Keyset cursor
    /// on (opened_at DESC, id DESC): stable for an append-only-over-time table where OFFSET re-scans and
    /// double-counts as new incidents open. The id tie-break keeps incidents sharing an opened_at distinct
    /// across a page boundary. Params: <c>?status=open|resolved</c>, <c>?checkId=</c>, <c>?from=&amp;to=</c>
    /// (ISO-8601; DEFAULT the last 30d so the query NEVER loads all-time), <c>?cursor=</c>, <c>?pageSize=</c>
    /// (default 50, max 200). Returns the page + a nextCursor (null when the window is exhausted).
    ///
    /// ★ <c>status=open</c> is EXEMPT from the date window: open incidents are count-bounded (the partial
    /// unique index <c>one_open_incident_per_check</c> allows at most one open per check), and a long-running
    /// open incident must never be hidden by a recent window. Resolved/all incidents grow without bound over
    /// time, so they get the window — that is the unbounded set the cursor design exists to bound.
    /// </summary>
    [Function("ListIncidents")]
    public async Task<IActionResult> ListIncidents(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "incidents")] HttpRequest req,
        CancellationToken ct)
    {
        var status = req.Query["status"].ToString();
        if (!string.IsNullOrEmpty(status) && status is not ("open" or "resolved"))
            return ApiResults.BadRequest("status must be 'open' or 'resolved'.");

        var range = CursorPaging.Parse(req, DateTimeOffset.UtcNow, IncidentWindow);
        if (!range.IsValid)
            return ApiResults.BadRequest(range.Error!);

        var query = _db.Incidents.AsNoTracking();

        if (!string.IsNullOrEmpty(status))
            query = query.Where(i => i.Status == status);

        if (long.TryParse(req.Query["checkId"], out var checkId))
            query = query.Where(i => i.CheckId == checkId);

        // Bound to the date-range window EXCEPT for status=open (count-bounded; see the remarks above —
        // windowing it would hide a still-open incident that opened before the window).
        if (status != "open")
            query = query.Where(i => i.OpenedAt >= range.From && i.OpenedAt < range.To);

        // Keyset: continue strictly after the cursor's (opened_at, id) under the DESC ordering.
        if (range.Cursor is { } cur)
            query = query.Where(i => i.OpenedAt < cur.Ts || (i.OpenedAt == cur.Ts && i.Id < cur.Id));

        // LEFT JOIN to checks for the incident's check name/kind (dashboard parity). NOT an inner join:
        // an incident whose check is missing/deleted must still surface — never join-dropped by its check
        // (checkName/checkKind are null then). The explicit GroupJoin + DefaultIfEmpty forces the LEFT JOIN.
        // Over-fetch one row to know whether a further page exists, without a COUNT over the whole table.
        var rows = await (
            from i in query
            join c in _db.Checks.AsNoTracking() on i.CheckId equals c.Id into cj
            from c in cj.DefaultIfEmpty()
            orderby i.OpenedAt descending, i.Id descending
            select new { Incident = i, Name = c != null ? c.Name : null, Kind = c != null ? c.Kind : null })
            .Take(range.PageSize + 1)
            .ToListAsync(ct);

        var hasMore = rows.Count > range.PageSize;
        if (hasMore)
            rows.RemoveAt(rows.Count - 1);

        var incidents = rows
            .Select(x => IncidentDto.From(x.Incident, x.Name, x.Kind))
            .ToList();
        var nextCursor = hasMore && rows.Count > 0
            ? new CursorPosition(rows[^1].Incident.OpenedAt, rows[^1].Incident.Id).Encode()
            : null;

        return ApiResults.Ok(new CursorPage<IncidentDto>(incidents, nextCursor, range.PageSize));
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
            .Select(c => new { c.Name, c.Kind, c.TargetUrl })
            .FirstOrDefaultAsync(ct);

        var to = inc.ResolvedAt ?? DateTimeOffset.UtcNow;

        // Core window: runs during the incident — NEWEST-first with a hard cap. An OPEN incident's window is
        // otherwise unbounded (a 10-day 60s-cadence incident ≈ 14,400 rows; a real capture measured 2,309
        // rows / 765KB of JSON — the page the on-call engineer needs most is the one that must not be huge).
        // The count is separate so the response can say "showing newest 500 of N".
        var coreWindow = _db.Runs.AsNoTracking()
            .Where(r => r.CheckId == inc.CheckId && r.StartedAt >= inc.OpenedAt && r.StartedAt <= to);
        var totalRuns = await coreWindow.LongCountAsync(ct);
        var core = await coreWindow
            .OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id)
            .Take(TimelineRunCap)
            .ToListAsync(ct);
        var truncated = totalRuns > core.Count;

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

        // Per-location status DURING the incident window [opened_at, to] — the latest IN-WINDOW run per
        // location, derived from `core` (the same window the timeline uses). For a RESOLVED incident
        // this is the per-location state as of the incident (not now); for an OPEN incident `to` = now()
        // so it tracks the live state. (Previously this used the latest run per location across ALL time,
        // so a resolved incident's panel showed the present, not the incident.) When the timeline is
        // truncated, `core` is the newest cap — the LATEST in-window run per location is inside that newest
        // slice for any location still running, so the panel stays correct. Coalesce a null/empty
        // location to "default" (the runs.location DEFAULT) — matching RunDto/TimelineEntryDto — so a
        // null never becomes its own bogus group. Reuses `core`, so no extra query.
        var perLocation = core
            .GroupBy(r => string.IsNullOrEmpty(r.Location) ? "default" : r.Location)
            .Select(g => new LocationStatusDto(g.Key, g.OrderByDescending(r => r.StartedAt).First().Status))
            .OrderBy(d => d.Location, StringComparer.Ordinal)
            .ToList();

        // Recurrence: recent incidents on the same check, newest first, excluding this one.
        var recurrence = (await _db.Incidents.AsNoTracking()
            .Where(x => x.CheckId == inc.CheckId && x.Id != inc.Id)
            .OrderByDescending(x => x.OpenedAt)
            .Take(10)
            .ToListAsync(ct))
            .Select(x => new RecurrenceDto(x.Id, x.OpenedAt, x.ResolvedAt, x.Status, x.Summary))
            .ToList();

        var nearbyDeploys = await NearbyDeploysAsync(check?.TargetUrl, inc.OpenedAt, ct);

        var detail = new IncidentDetailDto(
            inc.Id, inc.CheckId, check?.Name, check?.Kind, inc.Status, inc.Severity,
            inc.OpenedAt, inc.ResolvedAt,
            DurationSeconds: inc.ResolvedAt is null ? null : (inc.ResolvedAt.Value - inc.OpenedAt).TotalSeconds,
            inc.ConsecutiveFailures, inc.Summary, inc.Rca, perLocation, timeline, recurrence, nearbyDeploys,
            totalRuns, truncated);

        req.HttpContext.Response.Headers.CacheControl = "public, max-age=10";
        req.HttpContext.Response.Headers["Vary"] = "Origin";
        return ApiResults.Ok(detail);
    }

    // Deploy-proximity window (ASYMMETRIC on purpose): a deploy DETECTED up to 15 min AFTER an incident opened
    // can still have happened BEFORE it — detection lags reality (the marker is captured passively by
    // browser-check runs, so detected_at carries poll latency, not authoritative deploy time). So we look from
    // 60 min BEFORE opened_at to 15 min AFTER.
    // ★ Hard cap on timeline entries served per incident (newest-first). Named so the dashboard copy
    // ("showing newest 500 of N") and the API agree in one place. See IncidentDetailDto.TotalRuns/Truncated.
    private const int TimelineRunCap = 500;

    private const int DeployProximityLeadMinutes = 60;
    private const int DeployProximityLagMinutes = 15;

    /// <summary>
    /// Deploys DETECTED near this incident on the SAME host — a possible correlation, NEVER causation
    /// (detected_at is detection time with poll latency). Host-matched with query-side normalization (lowercase
    /// + strip a leading "www." on BOTH sides), so an apex-host check (e.g. a wegmans.com SSL check) matches a
    /// www.wegmans.com deploy — the runner's stored hostOf() keeps "www.", and its dedup key is runner-owned +
    /// out of scope, so we close the gap read-side only. Empty (never null) when there are no matches — the
    /// honest-empty state the UI renders as absence. Tolerant of the deploys table not being migrated in this
    /// env (→ empty, never a 500). Detail endpoint only — NOT ListIncidents.
    /// </summary>
    private async Task<IReadOnlyList<NearbyDeployDto>> NearbyDeploysAsync(
        string? targetUrl, DateTimeOffset openedAt, CancellationToken ct)
    {
        var host = NormalizeHost(targetUrl);
        if (host.Length == 0)
            return new List<NearbyDeployDto>();

        var windowStart = openedAt.AddMinutes(-DeployProximityLeadMinutes);
        var windowEnd = openedAt.AddMinutes(DeployProximityLagMinutes);

        List<NearbyDeployRow> rows;
        try
        {
            // offset_minutes is SIGNED (detected_at - opened_at): negative = detected before the incident opened.
            // regexp_replace strips a leading "www." from the stored host so apex ↔ www match.
            rows = await _db.NearbyDeploys.FromSql(
                $@"SELECT d.detected_at, d.source, d.is_sha, d.sha, d.fingerprint,
                          round(EXTRACT(EPOCH FROM (d.detected_at - {openedAt})) / 60.0)::int AS offset_minutes
                   FROM deploys d
                   WHERE regexp_replace(lower(d.target_host), '^www\.', '') = {host}
                     AND d.detected_at >= {windowStart} AND d.detected_at <= {windowEnd}
                   ORDER BY d.detected_at").AsNoTracking().ToListAsync(ct);
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42P01")
        {
            return new List<NearbyDeployDto>(); // deploys not migrated in this env — serve empty, never a 500.
        }

        return rows
            .Select(d => new NearbyDeployDto(
                d.DetectedAt, d.Source, d.IsSha,
                Sha: d.IsSha ? (d.Sha ?? "") : "", // empty unless it's a real SHA (mirrors DeployMarkerDto)
                d.Fingerprint, d.OffsetMinutes))
            .ToList();
    }

    /// <summary>Host for proximity matching: the URL's host (or the raw value when it isn't an absolute URL),
    /// lowercased with a single leading "www." stripped — the SAME normalization the SQL applies to the stored
    /// target_host, so apex and www variants match.</summary>
    private static string NormalizeHost(string? targetUrl)
    {
        if (string.IsNullOrWhiteSpace(targetUrl))
            return "";
        var host = Uri.TryCreate(targetUrl, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host)
            ? u.Host
            : targetUrl;
        host = host.Trim().ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host["www.".Length..] : host;
    }
}
