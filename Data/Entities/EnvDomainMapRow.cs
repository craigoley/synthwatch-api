namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// One ordered domain→environment inference rule (runner migration 0073). The runner's reconcile-apply
/// resolves <c>checks.environment</c> as <c>manifest.environment ?? inferFromDomain(target_url, map) ?? 'prod'</c>;
/// this is the map. READ-ONLY here (the runner owns the schema + the write path is env PR-3) — the API only
/// SELECTs it for <c>GET /api/env-domain-map</c>. <see cref="Pattern"/> = an exact host or a <c>*.suffix</c>
/// wildcard; lowest <see cref="Priority"/> wins (ties by <see cref="Id"/>).
/// </summary>
public class EnvDomainMapRow
{
    public long Id { get; set; }
    public string Pattern { get; set; } = null!;
    public string Environment { get; set; } = null!;
    public int Priority { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
