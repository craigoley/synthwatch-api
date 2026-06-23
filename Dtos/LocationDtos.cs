using System.Text.Json.Serialization;

namespace SynthWatch.Api.Dtos;

/// <summary>One registry location for the selector's options. Wire shape: { name, enabled }.</summary>
public record LocationDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("enabled")] bool Enabled);

/// <summary>GET /api/locations response: the deployed-regions registry.</summary>
public record LocationsResponse(
    [property: JsonPropertyName("locations")] IReadOnlyList<LocationDto> Locations);

/// <summary>GET/PUT /api/checks/{id}/locations response: a check's assigned location set (sorted).</summary>
public record CheckLocationsResponse(
    [property: JsonPropertyName("locations")] IReadOnlyList<string> Locations);

/// <summary>PUT /api/checks/{id}/locations body: the EXACT location set to assign.</summary>
public class SetLocationsRequest
{
    [JsonPropertyName("locations")]
    public List<string>? Locations { get; set; }
}
