using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using SynthWatch.Api.Data;
using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;

namespace SynthWatch.Api.Functions;

/// <summary>
/// Alert routing (runner-owned `alert_routes`, migration 0023 / #81). GET assembles the contract's
/// { severity, perCheck } shape from the rows; PUT replaces the entire routing config (validating that
/// every channelId/checkId exists and severities are critical|warning), in one transaction.
/// </summary>
public class RoutingFunctions
{
    private static readonly string[] Severities = { "critical", "warning" };

    private readonly SynthWatchDbContext _db;

    public RoutingFunctions(SynthWatchDbContext db) => _db = db;

    /// <summary>GET /api/routing — the routing config assembled from alert_routes.</summary>
    [Function("GetRouting")]
    public async Task<IActionResult> GetRouting(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "routing")] HttpRequest req,
        CancellationToken ct)
    {
        var routes = await _db.AlertRoutes.AsNoTracking().ToListAsync(ct);
        return ApiResults.Ok(Assemble(routes));
    }

    /// <summary>
    /// PUT /api/routing — set the entire routing config to the body. Validates severities (critical|
    /// warning), and that every referenced channelId and checkId exists; then replaces all alert_routes
    /// rows with the body's set in one transaction. Returns the new config.
    /// </summary>
    [Function("SetRouting")]
    public async Task<IActionResult> SetRouting(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "routing")] HttpRequest req,
        CancellationToken ct)
    {
        RoutingDto? body;
        try
        {
            body = await req.ReadFromJsonAsync<RoutingDto>(ct);
        }
        catch (JsonException)
        {
            return ApiResults.BadRequest("Request body is not valid JSON.");
        }
        if (body is null) return ApiResults.BadRequest("Request body is required.");

        // GUARD (defense-in-depth against contract drift): deserialization maps unrecognized keys to
        // null, so BOTH null means the payload was empty ({}) or used the wrong keys (e.g. a stale
        // {defaults, overrides} dashboard). Reject rather than wiping every route with a silent 200 —
        // this PUT is replace-all, so an empty desired set would ExecuteDelete all rows. An INTENTIONAL
        // clear must send the recognized keys explicitly: {"severity":{},"perCheck":{}} (present-but-empty
        // -> non-null -> allowed below, clears all). A well-formed partial ({"severity":{...}}) is fine.
        if (body.Severity is null && body.PerCheck is null)
            return ApiResults.BadRequest(
                "routing payload empty or unrecognized — expected { severity, perCheck }. " +
                "To clear all routing, send an explicit { \"severity\": {}, \"perCheck\": {} }.");

        // Build the desired routes from the body + validate as we go.
        var desired = new List<AlertRoute>();

        foreach (var (severity, set) in body.Severity ?? new())
        {
            if (Array.IndexOf(Severities, severity) < 0)
                return ApiResults.BadRequest($"unknown severity '{severity}'. Allowed: critical, warning.");
            foreach (var cid in (set?.ChannelIds ?? new List<long>()).Distinct())
                desired.Add(new AlertRoute { Severity = severity, ChannelId = cid });
        }

        foreach (var (checkKey, set) in body.PerCheck ?? new())
        {
            if (!long.TryParse(checkKey, out var checkId))
                return ApiResults.BadRequest($"perCheck key '{checkKey}' is not a valid check id.");
            foreach (var cid in (set?.ChannelIds ?? new List<long>()).Distinct())
                desired.Add(new AlertRoute { CheckId = checkId, ChannelId = cid });
        }

        // Referential validation (clear 400s rather than raw FK-violation 500s).
        var channelIds = desired.Select(r => r.ChannelId).Distinct().ToList();
        if (channelIds.Count > 0)
        {
            var known = (await _db.Channels.AsNoTracking()
                .Where(c => channelIds.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct)).ToHashSet();
            var unknownChannels = channelIds.Where(c => !known.Contains(c)).ToList();
            if (unknownChannels.Count > 0)
                return ApiResults.BadRequest($"unknown channelId(s): {string.Join(", ", unknownChannels)}.");
        }
        var checkIds = desired.Where(r => r.CheckId is not null).Select(r => r.CheckId!.Value).Distinct().ToList();
        if (checkIds.Count > 0)
        {
            var known = (await _db.Checks.AsNoTracking()
                .Where(c => checkIds.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct)).ToHashSet();
            var unknownChecks = checkIds.Where(c => !known.Contains(c)).ToList();
            if (unknownChecks.Count > 0)
                return ApiResults.BadRequest($"unknown checkId(s) in perCheck: {string.Join(", ", unknownChecks)}.");
        }

        // Replace the whole routing config atomically.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _db.AlertRoutes.ExecuteDeleteAsync(ct);
        if (desired.Count > 0)
        {
            _db.AlertRoutes.AddRange(desired);
            await _db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);

        var routes = await _db.AlertRoutes.AsNoTracking().ToListAsync(ct);
        return ApiResults.Ok(Assemble(routes));
    }

    private static RoutingDto Assemble(List<AlertRoute> routes)
    {
        var severity = routes.Where(r => r.Severity is not null)
            .GroupBy(r => r.Severity!)
            .ToDictionary(g => g.Key, g => new ChannelIdsDto(g.Select(r => r.ChannelId).OrderBy(x => x).ToList()));
        var perCheck = routes.Where(r => r.CheckId is not null)
            .GroupBy(r => r.CheckId!.Value)
            .ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => new ChannelIdsDto(g.Select(r => r.ChannelId).OrderBy(x => x).ToList()));
        return new RoutingDto
        {
            Severity = severity.Count > 0 ? severity : null,
            PerCheck = perCheck.Count > 0 ? perCheck : null,
        };
    }
}
