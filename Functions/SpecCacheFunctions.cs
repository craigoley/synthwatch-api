using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using SynthWatch.Api.Data;
using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;

namespace SynthWatch.Api.Functions;

/// <summary>
/// GET /api/checks/{id}/spec-cache — READ-ONLY view of a check's cached runtime spec (migration 0034
/// spec_cache). Surfaces which monitors-repo commit is cached (<c>etag</c> = commit SHA) and when it was last
/// fetched, so a merge's propagation is observable ("cached at &lt;sha&gt;, fetched HH:MM") — the doubt-killer
/// after a merge.
///
/// ★ It NEVER writes spec_cache. The API role is denied INSERT/UPDATE/DELETE by migration 0041 (compiled_js is
/// executed at runner privilege → an API-writable cache is a merge-gate/RCE bypass). A manual force-refresh
/// EVICTION is therefore a runner capability (the owner synthadmin), not this endpoint — see the runner-PR
/// spec. This is the safe, read-only half: make staleness observable.
/// </summary>
public class SpecCacheFunctions
{
    private readonly SynthWatchDbContext _db;

    public SpecCacheFunctions(SynthWatchDbContext db) => _db = db;

    [Function("GetCheckSpecCache")]
    public async Task<IActionResult> GetCheckSpecCache(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "checks/{id:long}/spec-cache")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        var check = await _db.Checks.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (check is null) return ApiResults.NotFound($"Check {id} not found.");

        // Baked-in (non-Git) checks have no spec_path → no runtime-spec cache to report.
        if (string.IsNullOrEmpty(check.SpecPath))
            return ApiResults.Ok(new SpecCacheDto(GitManaged: false, SpecPath: null, CachedSha: null, FetchedAt: null));

        // SELECT-only read of the runner-owned cache row for this spec_path.
        var row = await _db.SpecCache.AsNoTracking().FirstOrDefaultAsync(s => s.SpecPath == check.SpecPath, ct);
        return ApiResults.Ok(new SpecCacheDto(
            GitManaged: true,
            SpecPath: check.SpecPath,
            CachedSha: row?.Etag,     // null when the spec has never been fetched yet (no cache row)
            FetchedAt: row?.FetchedAt));
    }
}
