namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// READ-ONLY projection of the runner-owned <c>spec_cache</c> table (migration 0034). The runner (Postgres
/// owner <c>synthadmin</c>) is the ONLY writer — the <c>synthwatch-api</c> role is deliberately denied
/// INSERT/UPDATE/DELETE (migration 0041: <c>compiled_js</c> is executed at runner privilege, so an
/// API-writable cache would be a merge-gate/RCE bypass). This API only SELECTs the version identity
/// (<c>etag</c> = the monitors-repo commit SHA) + <c>fetched_at</c> so the dashboard can show which commit is
/// cached and when it was last refreshed. <c>compiled_js</c> is intentionally NOT mapped (never read here).
/// </summary>
public class SpecCacheRow
{
    /// <summary>The monitors-repo spec path — PRIMARY KEY of spec_cache.</summary>
    public string SpecPath { get; set; } = null!;

    /// <summary>Version identity: the commit SHA the cached compile was fetched at (null before first fetch).</summary>
    public string? Etag { get; set; }

    /// <summary>When the runner last fetched/compiled this spec.</summary>
    public DateTimeOffset FetchedAt { get; set; }
}
