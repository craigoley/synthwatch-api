namespace SynthWatch.Api.Dtos;

/// <summary>
/// GET /api/checks/{id}/spec-cache — the cached runtime-spec version identity (read-only). Lets the dashboard
/// show which monitors-repo commit is cached and when it was last fetched, so a merge's propagation is
/// observable. <c>GitManaged</c> is false for baked-in (non-spec_path) checks, which have no runtime-spec cache.
/// </summary>
public record SpecCacheDto(
    bool GitManaged,
    string? SpecPath,
    // CachedSha = etag = the monitors-repo commit SHA the cached compile was fetched at (null: never fetched yet).
    string? CachedSha,
    DateTimeOffset? FetchedAt);
