using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using SynthWatch.Api.Data;
using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;

namespace SynthWatch.Api.Functions;

/// <summary>
/// Spec catalog (Phase 13). Serves the runner-owned spec_catalog snapshot (migration 0036) READ-ONLY,
/// LEFT JOINed to live checks by source_key so each row carries its coverage state (Unmonitored / Active /
/// Paused) alongside the manifest's runnability probe. The API never reconciles, never fetches GitHub, and
/// never writes checks — activation (the write path) is a separate later PR.
/// </summary>
public class SpecsFunctions
{
    private readonly SynthWatchDbContext _db;

    public SpecsFunctions(SynthWatchDbContext db) => _db = db;

    /// <summary>
    /// GET /api/specs — the latest spec catalog. Each row = a manifest spec (from spec_catalog) LEFT JOIN
    /// the live check it activated (by source_key), with per-check health for the monitored ones. Health is
    /// computed via the SAME lateral-join shape ChecksFunctions uses for the grid (latest-run status + 24h
    /// p95 + open-incident count) — scoped to the joined check, NULL when unmonitored. Empty items = the
    /// reconcile job hasn't populated spec_catalog yet.
    /// </summary>
    [Function("GetSpecCatalog")]
    public async Task<IActionResult> GetSpecCatalog(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "specs")] HttpRequest req,
        CancellationToken ct)
    {
        var rows = await _db.SpecCatalog.FromSql(
            $@"SELECT
                 s.source_key, s.name, s.spec_path, s.kind, s.target, s.suggested_interval_seconds,
                 s.tags::text AS tags, s.description, s.enabled_by_default, s.runnable,
                 s.not_runnable_reason, s.probed_at,
                 c.id AS check_id, c.name AS check_name, c.enabled, c.archived_at, c.removed_at,
                 lr.status AS current_status, lr.started_at AS last_run_at,
                 stats.p95_ms,
                 oi.open_incident_count
               FROM spec_catalog s
               LEFT JOIN checks c ON c.source_key = s.source_key
               LEFT JOIN LATERAL (
                 SELECT r.status, r.started_at FROM runs r
                 WHERE r.check_id = c.id
                 ORDER BY r.started_at DESC LIMIT 1
               ) lr ON c.id IS NOT NULL
               LEFT JOIN LATERAL (
                 SELECT percentile_cont(0.95) WITHIN GROUP (ORDER BY r.duration_ms) AS p95_ms
                 FROM runs r
                 WHERE r.check_id = c.id
                   AND r.started_at >= NOW() - INTERVAL '24 hours'
                   AND r.duration_ms IS NOT NULL
               ) stats ON c.id IS NOT NULL
               LEFT JOIN LATERAL (
                 SELECT COUNT(*)::int AS open_incident_count FROM incidents i
                 WHERE i.check_id = c.id AND i.resolved_at IS NULL
               ) oi ON c.id IS NOT NULL
               ORDER BY s.source_key").AsNoTracking().ToListAsync(ct);

        var items = rows.Select(r =>
        {
            var monitored = r.CheckId is not null;
            var health = monitored
                ? new SpecHealthDto(r.CurrentStatus, r.P95Ms, r.OpenIncidentCount ?? 0, r.LastRunAt)
                : null;
            return new SpecCatalogItemDto(
                r.SourceKey, r.Name, r.SpecPath, r.Kind, r.Target, r.SuggestedIntervalSeconds,
                SafeStringList(r.Tags), r.Description, r.EnabledByDefault, r.Runnable, r.NotRunnableReason,
                monitored, r.CheckId, r.CheckName, r.Enabled, r.ArchivedAt, r.RemovedAt, health);
        }).ToList();

        // The latest probe time = when the last reconcile populated the catalog; null when empty.
        DateTimeOffset? probedAt = rows.Count == 0 ? null : rows.Max(r => r.ProbedAt);

        req.HttpContext.Response.Headers.CacheControl = "public, max-age=30";
        req.HttpContext.Response.Headers["Vary"] = "Origin";
        return ApiResults.Ok(new SpecCatalogDto(items, probedAt));
    }

    // Parse the runner-written tags jsonb (read as text) defensively — a malformed value degrades to [].
    private static IReadOnlyList<string> SafeStringList(string? json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(string.IsNullOrWhiteSpace(json) ? "[]" : json) ?? new List<string>(); }
        catch (JsonException) { return Array.Empty<string>(); }
    }
}
