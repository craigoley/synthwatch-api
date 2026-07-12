using System.Text.Json.Serialization;

namespace SynthWatch.Api.Dtos;

/// <summary>One error mute (error_mutes, runner migration 0076): the muted fingerprint + when/who/why. Wire
/// shape for GET /api/checks/{id}/error-mutes and the row returned on create. muted_by/note are best-effort
/// (nullable). The fingerprint is the P1/P2 error identity the error-diff read filters on.</summary>
public sealed record ErrorMuteDto(
    [property: JsonPropertyName("fingerprint")] string Fingerprint,
    [property: JsonPropertyName("mutedAt")] System.DateTimeOffset MutedAt,
    [property: JsonPropertyName("mutedBy")] string? MutedBy,
    [property: JsonPropertyName("note")] string? Note);

/// <summary>GET /api/checks/{id}/error-mutes response: every mute for the check (newest first). The management
/// view (list + unmute); the error-diff read applies these as a filter over its own NEW bucket.</summary>
public sealed record ErrorMutesResponse(
    [property: JsonPropertyName("mutes")] IReadOnlyList<ErrorMuteDto> Mutes);

/// <summary>POST /api/checks/{id}/error-mutes body — mute one fingerprint. note is an optional operator memo
/// ("known third-party — tracked in JIRA-123"); it is set at mute time and NOT editable (unmute + re-mute to
/// change it).</summary>
public sealed class MuteErrorRequest
{
    [JsonPropertyName("fingerprint")] public string? Fingerprint { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
}
