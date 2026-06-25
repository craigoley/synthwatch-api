using System.Text.Json.Serialization;

namespace SynthWatch.Api.Dtos;

// Phase 12 slice 1 — auth identity. Request bodies are PascalCase (case-insensitive deserialization,
// like CreateCheckRequest); responses carry explicit camelCase names (the DTO convention).

/// <summary>Body for POST /api/auth/request-code and /api/auth/request-access.</summary>
public class EmailRequest
{
    public string? Email { get; set; }
}

/// <summary>Body for POST /api/auth/verify.</summary>
public class VerifyRequest
{
    public string? Email { get; set; }
    public string? Code { get; set; }
}

/// <summary>POST /api/auth/verify success — the opaque bearer token (shown ONCE) + the resolved role.</summary>
public record VerifyResponseDto(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);

/// <summary>GET /api/auth/me — who the bearer token belongs to + its live role.</summary>
public record MeDto(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("role")] string Role);

/// <summary>A uniform message envelope — used for enumeration-safe responses (request-code, request-access)
/// so the body never reveals whether an email is known/editor/admin.</summary>
public record MessageDto(
    [property: JsonPropertyName("message")] string Message);
