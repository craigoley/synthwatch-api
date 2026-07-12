using System.Text.RegularExpressions;
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
/// The domain→environment inference map (env PR-2 read; PR-3 CRUD). The runner owns env_domain_map (migration
/// 0073) and resolves <c>checks.environment</c> as <c>manifest.environment ?? inferFromDomain(target_url) ??
/// 'prod'</c> at reconcile-apply; this manages the rules the management page edits. Write grants come from
/// runner migration 0075 (env_domain_map is inert config, like check_tags — NOT RCE-sensitive). GET is
/// session-gated (read); the mutating verbs are gated + audited by the AuthorizationMiddleware.
/// </summary>
public partial class EnvDomainMapFunctions
{
    private readonly SynthWatchDbContext _db;
    private readonly IAuthPrincipal? _auth;

    // _auth optional for test convenience only — DI always injects it; the read gate is inert flag-off.
    public EnvDomainMapFunctions(SynthWatchDbContext db, IAuthPrincipal? auth = null)
    {
        _db = db;
        _auth = auth;
    }

    // Pattern = exact host or `*.suffix` wildcard — lowercase host chars only (a-z 0-9 . - :), optional `*.`
    // prefix. NOT regex: keep the config predictable (the runner matcher only understands exact + *.suffix).
    [GeneratedRegex(@"^(\*\.)?[a-z0-9][a-z0-9.\-:]*$")]
    private static partial Regex PatternShape();

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
            .Select(r => new EnvDomainRuleDto(r.Id, r.Pattern, r.Environment, r.Priority))
            .ToListAsync(ct);

        return ApiResults.Ok(new EnvDomainMapResponse(rules));
    }

    /// <summary>POST /api/env-domain-map — create a rule. Gated + audited by the middleware.</summary>
    [Function("CreateEnvDomainRule")]
    public async Task<IActionResult> CreateEnvDomainRule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "env-domain-map")] HttpRequest req,
        CancellationToken ct)
    {
        var (body, bodyError) = await RequestJson.ReadAsync<EnvDomainRuleWriteRequest>(req, ct);
        if (bodyError is not null) return bodyError;
        if (Validate(body, out var pattern, out var environment, out var priority) is { } invalid) return invalid;

        // UNIQUE(pattern) — a friendly 400 instead of a constraint 500 on a duplicate.
        if (await _db.EnvDomainMap.AnyAsync(r => r.Pattern == pattern, ct))
            return ApiResults.BadRequest($"A rule for pattern '{pattern}' already exists — edit it instead.");

        var row = new EnvDomainMapRow { Pattern = pattern, Environment = environment, Priority = priority };
        _db.EnvDomainMap.Add(row);
        await _db.SaveChangesAsync(ct);
        return ApiResults.Created($"/api/env-domain-map/{row.Id}", new EnvDomainRuleDto(row.Id, row.Pattern, row.Environment, row.Priority));
    }

    /// <summary>PUT /api/env-domain-map/{id} — replace a rule's pattern/environment/priority.</summary>
    [Function("UpdateEnvDomainRule")]
    public async Task<IActionResult> UpdateEnvDomainRule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "env-domain-map/{id:long}")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        var (body, bodyError) = await RequestJson.ReadAsync<EnvDomainRuleWriteRequest>(req, ct);
        if (bodyError is not null) return bodyError;
        if (Validate(body, out var pattern, out var environment, out var priority) is { } invalid) return invalid;

        var row = await _db.EnvDomainMap.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return ApiResults.NotFound($"env-domain rule {id} not found.");
        if (row.Pattern != pattern && await _db.EnvDomainMap.AnyAsync(r => r.Pattern == pattern, ct))
            return ApiResults.BadRequest($"A rule for pattern '{pattern}' already exists.");

        row.Pattern = pattern;
        row.Environment = environment;
        row.Priority = priority;
        await _db.SaveChangesAsync(ct);
        return ApiResults.Ok(new EnvDomainRuleDto(row.Id, row.Pattern, row.Environment, row.Priority));
    }

    /// <summary>DELETE /api/env-domain-map/{id} — remove a rule.</summary>
    [Function("DeleteEnvDomainRule")]
    public async Task<IActionResult> DeleteEnvDomainRule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "env-domain-map/{id:long}")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        var row = await _db.EnvDomainMap.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return ApiResults.NotFound($"env-domain rule {id} not found.");
        _db.EnvDomainMap.Remove(row);
        await _db.SaveChangesAsync(ct);
        return ApiResults.NoContent();
    }

    // Validate + normalize a write body → (pattern, environment, priority), or a 400. Keeps the config a
    // predictable non-footgun: pattern is lowercased + shape-checked (exact host or *.suffix, no regex);
    // environment is the pinned vocab; priority defaults to 100 and must be >= 0 (matches the DB CHECKs).
    private static IActionResult? Validate(EnvDomainRuleWriteRequest? body, out string pattern, out string environment, out int priority)
    {
        pattern = ""; environment = ""; priority = 100;
        if (body is null) return ApiResults.BadRequest("Request body is required.");
        var errors = new Dictionary<string, string>();

        pattern = (body.Pattern ?? "").Trim().ToLowerInvariant();
        if (pattern.Length == 0) errors["pattern"] = "required.";
        else if (!PatternShape().IsMatch(pattern)) errors["pattern"] = "must be an exact host or a `*.suffix` wildcard (lowercase host chars; no whitespace or regex).";

        environment = body.Environment ?? "";
        if (environment is not ("prod" or "staging" or "dev")) errors["environment"] = "must be one of prod|staging|dev.";

        priority = body.Priority ?? 100;
        if (priority < 0) errors["priority"] = "must be >= 0.";

        return errors.Count > 0 ? ApiResults.ValidationError(errors) : null;
    }
}
