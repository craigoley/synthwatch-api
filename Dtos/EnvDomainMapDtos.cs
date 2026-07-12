using System.Text.Json.Serialization;

namespace SynthWatch.Api.Dtos;

/// <summary>One domain→environment inference rule (runner env_domain_map, 0073). Wire shape:
/// { pattern, environment, priority }. Pattern = exact host or `*.suffix` wildcard; lowest priority wins.</summary>
public record EnvDomainRuleDto(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("pattern")] string Pattern,
    [property: JsonPropertyName("environment")] string Environment,
    [property: JsonPropertyName("priority")] int Priority);

/// <summary>GET /api/env-domain-map response: the ordered inference rules (priority asc, id asc — first
/// match wins, same order the runner resolves in). Each rule carries its `id` for edit/delete.</summary>
public record EnvDomainMapResponse(
    [property: JsonPropertyName("rules")] IReadOnlyList<EnvDomainRuleDto> Rules);

/// <summary>POST/PUT /api/env-domain-map[/{id}] body — create or replace a domain→env rule. pattern = an
/// exact host or `*.suffix` wildcard (lowercase, no whitespace, no regex — a predictable config). priority
/// optional (default 100); lower wins.</summary>
public class EnvDomainRuleWriteRequest
{
    [JsonPropertyName("pattern")] public string? Pattern { get; set; }
    [JsonPropertyName("environment")] public string? Environment { get; set; }
    [JsonPropertyName("priority")] public int? Priority { get; set; }
}

/// <summary>PUT /api/checks/{id}/environment body — set the per-check env override, or CLEAR it (null). A
/// value must be prod|staging|dev. Writes ONLY checks.environment_override, never the git-authoritative
/// environment.</summary>
public class SetEnvironmentOverrideRequest
{
    [JsonPropertyName("environmentOverride")] public string? EnvironmentOverride { get; set; }
}
