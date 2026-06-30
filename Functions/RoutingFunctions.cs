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
/// Alert routing (runner-owned alert_routes #81 + tag_routes #85). THREE all-additive dimensions —
/// severity defaults, per-check, and tag-rules — which the runner UNIONs at dispatch ("hit any criterion
/// → alerted"); this API only CRUDs the rule sets. GET assembles { severity, perCheck, tagRules }. PUT is
/// PER-DIMENSION: a dimension ABSENT from the body is left untouched; PRESENT (even empty) replaces it
/// exactly. A payload with NONE of the three recognized keys -> 400 (defense-in-depth: contract drift
/// must never silently wipe routes, the #66 lesson — extended here to all three dimensions).
/// </summary>
public class RoutingFunctions
{
    private static readonly string[] Severities = { "critical", "warning" };

    private readonly SynthWatchDbContext _db;

    public RoutingFunctions(SynthWatchDbContext db) => _db = db;

    /// <summary>GET /api/routing — { severity, perCheck, tagRules } assembled from alert_routes + tag_routes.</summary>
    [Function("GetRouting")]
    public async Task<IActionResult> GetRouting(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "routing")] HttpRequest req,
        CancellationToken ct)
    {
        var routes = await _db.AlertRoutes.AsNoTracking().ToListAsync(ct);
        var tagRoutes = await _db.TagRoutes.AsNoTracking().ToListAsync(ct);
        return ApiResults.Ok(Assemble(routes, tagRoutes));
    }

    /// <summary>
    /// PUT /api/routing — set routing PER DIMENSION. Each of severity / perCheck / tagRules: ABSENT
    /// (null) leaves that dimension untouched; PRESENT (even empty {} / []) replaces it exactly (so an
    /// explicit empty clears just that dimension). Validates severities (critical|warning), that every
    /// referenced channelId + perCheck checkId exists, and normalizes tag-rule key/value (lowercase /
    /// whitespace→_, matching #85's CHECKs) + dedupes by (key,value,channel). All three absent -> 400.
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

        // GUARD (#66, extended to 3 dimensions): unrecognized keys deserialize to null, so all-three-null
        // means the payload was empty ({}) or used the wrong keys. Reject rather than touch anything. An
        // intentional clear sends the dimension explicitly empty ({"severity":{}} / {"tagRules":[]}); a
        // dimension simply OMITTED is left untouched (partial update — omitting tagRules never wipes them).
        if (body.Severity is null && body.PerCheck is null && body.TagRules is null)
            return ApiResults.BadRequest(
                "routing payload empty or unrecognized — expected { severity, perCheck, tagRules }. " +
                "Omit a dimension to leave it unchanged; send it explicitly empty (e.g. { \"tagRules\": [] }) to clear it.");

        // Build + validate each PRESENT dimension.
        // ★ F-05: a dimension ENTRY whose `channelIds` is ABSENT means the inner write shape didn't bind (a
        // client/contract drift) — NOT an intentional clear. The old `set?.ChannelIds ?? new List<long>()`
        // coalesced that to empty, so a present dimension silently resolved to ZERO rows and the per-dimension
        // replace below DELETED the routes and inserted nothing → all routes WIPED while returning 200, with
        // the gap invisible until an alert didn't fire. An intentional clear is unambiguous: clear ONE entry's
        // channels with `{ "critical": { "channelIds": [] } }` (present-but-empty), or clear the WHOLE
        // dimension with `{ "severity": {} }`. A missing channelIds key now FAILS LOUDLY (400) before any delete.
        var severityRows = new List<AlertRoute>();
        if (body.Severity is not null)
            foreach (var (severity, set) in body.Severity)
            {
                if (Array.IndexOf(Severities, severity) < 0)
                    return ApiResults.BadRequest($"unknown severity '{severity}'. Allowed: critical, warning.");
                if (set?.ChannelIds is null)
                    return ApiResults.BadRequest(
                        $"routing.severity['{severity}'] is missing channelIds — expected {{ \"channelIds\": [...] }}. " +
                        "Refusing a malformed routing write (it would otherwise wipe routes). Send channelIds:[] to clear.");
                foreach (var cid in set.ChannelIds.Distinct())
                    severityRows.Add(new AlertRoute { Severity = severity, ChannelId = cid });
            }

        var perCheckRows = new List<AlertRoute>();
        if (body.PerCheck is not null)
            foreach (var (checkKey, set) in body.PerCheck)
            {
                if (!long.TryParse(checkKey, out var checkId))
                    return ApiResults.BadRequest($"perCheck key '{checkKey}' is not a valid check id.");
                if (set?.ChannelIds is null)
                    return ApiResults.BadRequest(
                        $"routing.perCheck['{checkKey}'] is missing channelIds — expected {{ \"channelIds\": [...] }}. " +
                        "Refusing a malformed routing write (it would otherwise wipe routes). Send channelIds:[] to clear.");
                foreach (var cid in set.ChannelIds.Distinct())
                    perCheckRows.Add(new AlertRoute { CheckId = checkId, ChannelId = cid });
            }

        // Tag-rules: normalize key/value (matching #85's CHECKs) + dedupe by (key,value,channel) (the
        // UNIQUE). A tag-rule must target a concrete, non-empty value (key may be '' for a bare-value tag).
        var tagRuleRows = new List<TagRoute>();
        if (body.TagRules is not null)
        {
            var seen = new HashSet<(string, string, long)>();
            foreach (var r in body.TagRules)
            {
                var key = TagNormalization.NormalizeField(r.TagKey);
                var value = TagNormalization.NormalizeField(r.TagValue);
                if (string.IsNullOrEmpty(value))
                    return ApiResults.BadRequest("each tag-rule requires a non-empty tagValue.");
                if (seen.Add((key, value, r.ChannelId)))
                    tagRuleRows.Add(new TagRoute { TagKey = key, TagValue = value, ChannelId = r.ChannelId });
            }
        }

        // Referential validation across every present dimension (clear 400s, not raw FK 500s).
        var channelIds = severityRows.Select(r => r.ChannelId)
            .Concat(perCheckRows.Select(r => r.ChannelId))
            .Concat(tagRuleRows.Select(r => r.ChannelId)).Distinct().ToList();
        if (channelIds.Count > 0)
        {
            var known = (await _db.Channels.AsNoTracking()
                .Where(c => channelIds.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct)).ToHashSet();
            var unknown = channelIds.Where(c => !known.Contains(c)).ToList();
            if (unknown.Count > 0)
                return ApiResults.BadRequest($"unknown channelId(s): {string.Join(", ", unknown)}.");
        }
        var checkIds = perCheckRows.Select(r => r.CheckId!.Value).Distinct().ToList();
        if (checkIds.Count > 0)
        {
            var known = (await _db.Checks.AsNoTracking()
                .Where(c => checkIds.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct)).ToHashSet();
            var unknown = checkIds.Where(c => !known.Contains(c)).ToList();
            if (unknown.Count > 0)
                return ApiResults.BadRequest($"unknown checkId(s) in perCheck: {string.Join(", ", unknown)}.");
        }

        // Per-dimension replace, atomically — only PRESENT dimensions are touched.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        if (body.Severity is not null)
        {
            await _db.AlertRoutes.Where(r => r.CheckId == null).ExecuteDeleteAsync(ct);
            _db.AlertRoutes.AddRange(severityRows);
        }
        if (body.PerCheck is not null)
        {
            await _db.AlertRoutes.Where(r => r.CheckId != null).ExecuteDeleteAsync(ct);
            _db.AlertRoutes.AddRange(perCheckRows);
        }
        if (body.TagRules is not null)
        {
            await _db.TagRoutes.ExecuteDeleteAsync(ct);
            _db.TagRoutes.AddRange(tagRuleRows);
        }
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var routesOut = await _db.AlertRoutes.AsNoTracking().ToListAsync(ct);
        var tagRoutesOut = await _db.TagRoutes.AsNoTracking().ToListAsync(ct);
        return ApiResults.Ok(Assemble(routesOut, tagRoutesOut));
    }

    private static RoutingDto Assemble(List<AlertRoute> routes, List<TagRoute> tagRoutes)
    {
        var severity = routes.Where(r => r.Severity is not null)
            .GroupBy(r => r.Severity!)
            .ToDictionary(g => g.Key, g => new ChannelIdsDto(g.Select(r => r.ChannelId).OrderBy(x => x).ToList()));
        var perCheck = routes.Where(r => r.CheckId is not null)
            .GroupBy(r => r.CheckId!.Value)
            .ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => new ChannelIdsDto(g.Select(r => r.ChannelId).OrderBy(x => x).ToList()));
        var tagRules = tagRoutes
            .OrderBy(t => t.TagKey, StringComparer.Ordinal).ThenBy(t => t.TagValue, StringComparer.Ordinal).ThenBy(t => t.ChannelId)
            .Select(t => new TagRuleDto(t.TagKey, t.TagValue, t.ChannelId)).ToList();
        return new RoutingDto
        {
            Severity = severity.Count > 0 ? severity : null,
            PerCheck = perCheck.Count > 0 ? perCheck : null,
            TagRules = tagRules.Count > 0 ? tagRules : null,
        };
    }
}
