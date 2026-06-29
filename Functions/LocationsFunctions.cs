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
/// The location selector's API half (4-MLACT). Serves the deployed-regions registry and reads/edits a
/// check's location ASSIGNMENT — the set of check_locations cursors it has (the runner's source of
/// truth). PUT replicates the runner's setCheckLocations() (locations.ts) in C# across the language
/// boundary, plus the contract's validation (assign only to ENABLED registry locations; never empty).
/// </summary>
public class LocationsFunctions
{
    private readonly SynthWatchDbContext _db;

    public LocationsFunctions(SynthWatchDbContext db) => _db = db;

    /// <summary>GET /api/locations — the deployed-regions registry (the selector's options).</summary>
    [Function("GetLocations")]
    public async Task<IActionResult> GetLocations(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "locations")] HttpRequest req,
        CancellationToken ct)
    {
        var locations = await _db.Locations.AsNoTracking()
            .OrderBy(l => l.Name)
            .Select(l => new LocationDto(l.Name, l.Enabled))
            .ToListAsync(ct);
        return ApiResults.Ok(new LocationsResponse(locations));
    }

    /// <summary>GET /api/checks/{id}/locations — a check's current assigned location set.</summary>
    [Function("GetCheckLocations")]
    public async Task<IActionResult> GetCheckLocations(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "checks/{id:long}/locations")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        if (!await _db.Checks.AnyAsync(c => c.Id == id, ct))
            return ApiResults.NotFound($"Check {id} not found.");

        return ApiResults.Ok(new CheckLocationsResponse(await CurrentLocationsAsync(id, ct)));
    }

    /// <summary>
    /// PUT /api/checks/{id}/locations — set the check's assignment to EXACTLY the body's set. Adds
    /// cursors for newly-assigned locations (NULL last_run_at = due-now, matching create-seeding #62)
    /// and removes cursors for de-assigned ones, in one transaction (replicating the runner's
    /// setCheckLocations). Every location must be an ENABLED registry location; the set must be
    /// non-empty (a check must run from >= 1 location). Returns the new set.
    /// </summary>
    [Function("SetCheckLocations")]
    public async Task<IActionResult> SetCheckLocations(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "checks/{id:long}/locations")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        SetLocationsRequest? body;
        try
        {
            body = await req.ReadFromJsonAsync<SetLocationsRequest>(ct);
        }
        catch (JsonException)
        {
            return ApiResults.BadRequest("Request body is not valid JSON.");
        }
        if (body is null)
            return ApiResults.BadRequest("Request body is required.");

        var check = await _db.Checks.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new { c.Sensitive, c.RedactPatterns })
            .FirstOrDefaultAsync(ct);
        if (check is null)
            return ApiResults.NotFound($"Check {id} not found.");

        // ★ B10 ENABLE GATE (the canonical "redaction REQUIRED before enable" on the API path): assigning a
        // location inserts a check_locations cursor = enabling the check there. A sensitive check with no
        // declared redact_patterns must NOT be enabled — its trace could leak session tokens / cart / PII.
        // Mirrors the runner reconcile gate, so the bypass is closed on every enable path.
        if (CheckValidation.SensitiveNeedsRedaction(check.Sensitive, check.RedactPatterns))
            return ApiResults.BadRequest(
                "Cannot enable a sensitive check without redaction (B10): declare redact_patterns in the monitor's manifest before assigning locations.");

        // Normalize: trim, drop blanks, de-dupe (order-independent set).
        var requested = (body.Locations ?? new List<string>())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // A check must run from at least one location — an empty set would never run.
        if (requested.Count == 0)
            return ApiResults.BadRequest("locations must contain at least one location (a check must run from >= 1 location).");

        // Every requested location must be an ENABLED registry location.
        var enabled = await _db.Locations.AsNoTracking()
            .Where(l => l.Enabled).Select(l => l.Name).ToListAsync(ct);
        var enabledSet = enabled.ToHashSet(StringComparer.Ordinal);
        var unknown = requested.Where(l => !enabledSet.Contains(l)).ToList();
        if (unknown.Count > 0)
            return ApiResults.BadRequest($"unknown or disabled location(s): {string.Join(", ", unknown)}. Allowed: {string.Join(", ", enabled.OrderBy(x => x, StringComparer.Ordinal))}.");

        // Apply the set in one transaction: add newly-assigned (idempotent, NULL cursor = due-now),
        // drop de-assigned. Mirrors the runner's setCheckLocations() exactly.
        var requestedArr = requested.ToArray();
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO check_locations (check_id, location)
               SELECT {id}, unnest({requestedArr}::text[])
               ON CONFLICT (check_id, location) DO NOTHING", ct);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"DELETE FROM check_locations
               WHERE check_id = {id} AND NOT (location = ANY({requestedArr}::text[]))", ct);
        await tx.CommitAsync(ct);

        return ApiResults.Ok(new CheckLocationsResponse(await CurrentLocationsAsync(id, ct)));
    }

    private async Task<List<string>> CurrentLocationsAsync(long checkId, CancellationToken ct) =>
        await _db.CheckLocations.AsNoTracking()
            .Where(cl => cl.CheckId == checkId)
            .OrderBy(cl => cl.Location)
            .Select(cl => cl.Location)
            .ToListAsync(ct);
}
