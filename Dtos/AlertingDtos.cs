using System.Text.Json.Serialization;
using SynthWatch.Api.Data.Entities;

namespace SynthWatch.Api.Dtos;

/// <summary>A delivery channel on the wire: { id, name, type, config, enabled }.</summary>
public record ChannelDto(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("config")] ChannelConfig Config,
    [property: JsonPropertyName("enabled")] bool Enabled)
{
    public static ChannelDto From(Channel c) => new(c.Id, c.Name, c.Type, c.Config, c.Enabled);
}

/// <summary>Body for POST /api/channels and PUT /api/channels/{id} (full replace of mutable fields).</summary>
public class ChannelWriteRequest
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("config")] public ChannelConfig? Config { get; set; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
}

/// <summary>A set of channel ids ({ channelIds: [...] }) for one routing scope.</summary>
public record ChannelIdsDto(
    [property: JsonPropertyName("channelIds")] IReadOnlyList<long> ChannelIds);

/// <summary>
/// GET/PUT /api/routing shape: severity defaults (critical/warning) + per-check overrides, each a
/// { channelIds: [...] }. Assembled from alert_routes rows. Keys absent when they have no routes.
/// </summary>
public class RoutingDto
{
    [JsonPropertyName("severity")] public Dictionary<string, ChannelIdsDto>? Severity { get; set; }
    [JsonPropertyName("perCheck")] public Dictionary<string, ChannelIdsDto>? PerCheck { get; set; }
}
