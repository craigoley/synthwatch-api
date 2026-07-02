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
/// Tag CRUD (Phase 9a). The runner owns check_tags (#84); this exposes a check's tag set, the distinct
/// tags in use (for the dashboard filter/autocomplete), and the suggested keys. PUT replicates the
/// runner's setCheckTags() (tags.ts) in C# — normalize + dedupe (one value per key, last wins) +
/// upsert + delete-diff — so dashboard-set tags are identical to tags-as-code-set tags.
/// </summary>
public class TagsFunctions
{
    private readonly SynthWatchDbContext _db;

    public TagsFunctions(SynthWatchDbContext db) => _db = db;

    /// <summary>GET /api/checks/{id}/tags — a check's tag set (sorted by key, value).</summary>
    [Function("GetCheckTags")]
    public async Task<IActionResult> GetCheckTags(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "checks/{id:long}/tags")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        if (!await _db.Checks.AnyAsync(c => c.Id == id, ct))
            return ApiResults.NotFound($"Check {id} not found.");
        return ApiResults.Ok(new CheckTagsResponse(await CurrentTagsAsync(id, ct)));
    }

    /// <summary>
    /// PUT /api/checks/{id}/tags — set the check's tag set to EXACTLY the body's set. Normalizes
    /// (lowercase/trim/whitespace→_) + dedupes by key (last value wins) + drops empty-value tags,
    /// then upserts the desired tags and deletes any key no longer present, in one transaction —
    /// matching the runner's setCheckTags(). An explicit { "tags": [] } clears all tags; a MISSING
    /// `tags` key (null) is rejected (a contract drift must not silently wipe a check's tags).
    /// </summary>
    [Function("SetCheckTags")]
    public async Task<IActionResult> SetCheckTags(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "checks/{id:long}/tags")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        var (body, bodyError) = await RequestJson.ReadAsync<SetTagsRequest>(req, ct);
        if (bodyError is not null) return bodyError;
        if (body is null) return ApiResults.BadRequest("Request body is required.");
        // Guard (per the routing-PUT hardening): a missing/unrecognized `tags` key deserializes to null;
        // reject rather than wiping. An explicit empty array clears.
        if (body.Tags is null)
            return ApiResults.BadRequest("expected { tags: [...] }. To clear all tags, send { \"tags\": [] }.");

        if (!await _db.Checks.AnyAsync(c => c.Id == id, ct))
            return ApiResults.NotFound($"Check {id} not found.");

        // Normalize + dedupe by key (last value wins), dropping empty-value tags — exactly as #84.
        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var n in body.Tags.Select(t => TagNormalization.NormalizeTag(t.Key, t.Value)))
        {
            if (n is not null) byKey[n.Value.Key] = n.Value.Value;
        }
        var keys = byKey.Keys.ToArray();
        var values = byKey.Values.ToArray();

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        if (keys.Length > 0)
        {
            // Upsert (one value per key — last wins via ON CONFLICT DO UPDATE), mirroring setCheckTags.
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $@"INSERT INTO check_tags (check_id, key, value)
                   SELECT {id}, k, v FROM unnest({keys}::text[], {values}::text[]) AS t(k, v)
                   ON CONFLICT (check_id, key) DO UPDATE SET value = EXCLUDED.value", ct);
        }
        // Delete-diff: drop keys no longer present (empty keys[] => clears all).
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"DELETE FROM check_tags WHERE check_id = {id} AND key <> ALL({keys}::text[])", ct);
        await tx.CommitAsync(ct);

        return ApiResults.Ok(new CheckTagsResponse(await CurrentTagsAsync(id, ct)));
    }

    /// <summary>GET /api/tags — every distinct key:value in use + how many checks carry each.</summary>
    [Function("GetTagsInUse")]
    public async Task<IActionResult> GetTagsInUse(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "tags")] HttpRequest req,
        CancellationToken ct)
    {
        var grouped = await _db.CheckTags.AsNoTracking()
            .GroupBy(t => new { t.Key, t.Value })
            .Select(g => new { g.Key.Key, g.Key.Value, Count = (long)g.Count() })
            .ToListAsync(ct);
        var tags = grouped
            .OrderBy(t => t.Key, StringComparer.Ordinal).ThenBy(t => t.Value, StringComparer.Ordinal)
            .Select(t => new TagUsageDto(t.Key, t.Value, t.Count))
            .ToList();
        return ApiResults.Ok(new TagsInUseResponse(tags));
    }

    /// <summary>GET /api/tags/suggested — the suggested-canonical tag keys (mirrors #84).</summary>
    [Function("GetSuggestedTagKeys")]
    public static IActionResult GetSuggestedTagKeys(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "tags/suggested")] HttpRequest req) =>
        ApiResults.Ok(TagNormalization.SuggestedTagKeys);

    private async Task<List<TagDto>> CurrentTagsAsync(long checkId, CancellationToken ct) =>
        await _db.CheckTags.AsNoTracking()
            .Where(t => t.CheckId == checkId)
            .OrderBy(t => t.Key).ThenBy(t => t.Value)
            .Select(t => new TagDto(t.Key, t.Value))
            .ToListAsync(ct);
}
