using System.Text.Json.Serialization;

namespace SynthWatch.Api.Dtos;

/// <summary>One domain→environment inference rule (runner env_domain_map, 0073). Wire shape:
/// { pattern, environment, priority }. Pattern = exact host or `*.suffix` wildcard; lowest priority wins.</summary>
public record EnvDomainRuleDto(
    [property: JsonPropertyName("pattern")] string Pattern,
    [property: JsonPropertyName("environment")] string Environment,
    [property: JsonPropertyName("priority")] int Priority);

/// <summary>GET /api/env-domain-map response: the ordered inference rules (priority asc, id asc — first
/// match wins, same order the runner resolves in).</summary>
public record EnvDomainMapResponse(
    [property: JsonPropertyName("rules")] IReadOnlyList<EnvDomainRuleDto> Rules);
