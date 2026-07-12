using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using SynthWatch.Api.Data;
using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;

namespace SynthWatch.Api.Functions;

/// <summary>
/// The domain→environment inference map (env PR-2). READ-ONLY here: the runner owns env_domain_map (migration
/// 0073) and resolves <c>checks.environment</c> as <c>manifest.environment ?? inferFromDomain(target_url) ??
/// 'prod'</c> at reconcile-apply. This serves the ordered rules so the dashboard can show how a host maps to
/// an environment. CRUD (the management page) is env PR-3.
/// </summary>
public class EnvDomainMapFunctions
{
    private readonly SynthWatchDbContext _db;
    private readonly IAuthPrincipal? _auth;

    // _auth optional for test convenience only — DI always injects it; the read gate is inert flag-off.
    public EnvDomainMapFunctions(SynthWatchDbContext db, IAuthPrincipal? auth = null)
    {
        _db = db;
        _auth = auth;
    }

    /// <summary>GET /api/env-domain-map — the ordered inference rules (priority asc, id asc — the order the
    /// runner matches in; first match wins). Session-gated (fleet config, same posture as reconcile reads).</summary>
    [Function("GetEnvDomainMap")]
    public async Task<IActionResult> GetEnvDomainMap(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "env-domain-map")] HttpRequest req,
        CancellationToken ct)
    {
        if (await SessionReadGate.RequireSessionAsync(_auth, req, ct) is { } denied)
            return denied;

        var rules = await _db.EnvDomainMap.AsNoTracking()
            .OrderBy(r => r.Priority).ThenBy(r => r.Id)
            .Select(r => new EnvDomainRuleDto(r.Pattern, r.Environment, r.Priority))
            .ToListAsync(ct);

        return ApiResults.Ok(new EnvDomainMapResponse(rules));
    }
}
