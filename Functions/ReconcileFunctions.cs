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
/// Monitors-as-code drift surface (Phase 6b). Serves the runner-owned reconcile_drift table (migration
/// 0031) READ-ONLY: the reconcile job diffs the synthwatch-monitors manifest against live checks and
/// UPSERTs what differs; this API never reconciles and never applies (apply is a later runner capability —
/// reconcile runs in report mode). The dashboard reads this to show "N monitors differ from Git".
/// </summary>
public class ReconcileFunctions
{
    private readonly SynthWatchDbContext _db;

    public ReconcileFunctions(SynthWatchDbContext db) => _db = db;

    /// <summary>
    /// GET /api/reconcile/drift — the latest reconcile snapshot (the full current drift set). Empty items
    /// = live monitors are in sync with Git. Ordered drift_type then source_key for a stable display.
    /// detail jsonb is read as text and re-emitted verbatim (a 'changed' row's per-field before/after diff
    /// passes through unchanged).
    /// </summary>
    [Function("GetReconcileDrift")]
    public async Task<IActionResult> GetReconcileDrift(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "reconcile/drift")] HttpRequest req,
        CancellationToken ct)
    {
        var rows = await _db.ReconcileDrift.FromSql(
            $@"SELECT source_key, drift_type, detail::text AS detail, detected_at
               FROM reconcile_drift
               ORDER BY drift_type, source_key").AsNoTracking().ToListAsync(ct);

        var items = rows.Select(r =>
            new ReconcileDriftItemDto(r.SourceKey, r.DriftType, SafeJson(r.Detail), r.DetectedAt)).ToList();

        // The latest detected_at = when the last reconcile ran; null when there's no drift.
        DateTimeOffset? detectedAt = rows.Count == 0 ? null : rows.Max(r => r.DetectedAt);

        req.HttpContext.Response.Headers.CacheControl = "public, max-age=30";
        req.HttpContext.Response.Headers["Vary"] = "Origin";
        return ApiResults.Ok(new ReconcileDriftDto(items, detectedAt));
    }

    // Parse runner-written jsonb (read as text) defensively — a malformed value degrades to {}, never throws.
    private static JsonElement SafeJson(string? json)
    {
        try { return JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(json) ? "{}" : json); }
        catch (JsonException) { return JsonSerializer.Deserialize<JsonElement>("{}"); }
    }
}
