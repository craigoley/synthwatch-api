using System.Text.Json.Serialization;

namespace SynthWatch.Api.Dtos;

/// <summary>A key:value tag. key may be "" (a bare value).</summary>
public record TagDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("value")] string Value);

/// <summary>GET/PUT /api/checks/{id}/tags response: a check's tag set (sorted by key, value).</summary>
public record CheckTagsResponse(
    [property: JsonPropertyName("tags")] IReadOnlyList<TagDto> Tags);

/// <summary>PUT /api/checks/{id}/tags body: the EXACT tag set to assign (normalized + deduped on write).</summary>
public class SetTagsRequest
{
    [JsonPropertyName("tags")] public List<TagDto>? Tags { get; set; }
}

/// <summary>A distinct in-use tag and how many checks carry it (GET /api/tags).</summary>
public record TagUsageDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("count")] long Count);

/// <summary>GET /api/tags response: every distinct key:value in use, with check counts.</summary>
public record TagsInUseResponse(
    [property: JsonPropertyName("tags")] IReadOnlyList<TagUsageDto> Tags);
