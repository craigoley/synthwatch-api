using System.Text.Json;
using System.Text.Json.Serialization;

namespace SynthWatch.Api.Dtos;

/// <summary>
/// GET /api/reports/narrative — the runner-generated AI narrative for a scope+window (Reporting Layer 3).
/// factPack carries the cited numbers/incidents the prose draws on (auditability — the dashboard renders
/// them alongside the text). stale = the narrative is older than its window period (a regen is due).
/// </summary>
public record NarrativeDto(
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("window")] string Window,
    [property: JsonPropertyName("headline")] string Headline,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("highlights")] IReadOnlyList<string> Highlights,
    [property: JsonPropertyName("generatedAt")] DateTimeOffset GeneratedAt,
    [property: JsonPropertyName("stale")] bool Stale,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("factPack")] JsonElement FactPack);
