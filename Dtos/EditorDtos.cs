using System.Text.Json.Serialization;

namespace SynthWatch.Api.Dtos;

// Phase 12 slice 3 — editor (user) management. Admin-only. Request bodies are PascalCase
// (case-insensitive deserialization, like the other write DTOs); responses carry explicit camelCase.

/// <summary>Body for POST /api/editors.</summary>
public class AddEditorRequest
{
    public string? Email { get; set; }
}

/// <summary>GET /api/editors row — an entry in the admin-managed editor allowlist.</summary>
public record EditorDto(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("addedBy")] string AddedBy,
    [property: JsonPropertyName("addedAt")] DateTimeOffset AddedAt);

/// <summary>GET /api/access-requests row — a pending "request edit access" entry an admin can act on
/// (add the email as an editor). Already-editor/already-admin emails are filtered out server-side.</summary>
public record AccessRequestDto(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("requestedAt")] DateTimeOffset RequestedAt,
    [property: JsonPropertyName("count")] int Count);
