using System.Text.Json.Serialization;

namespace SynthWatch.Api.Dtos;

/// <summary>POST /api/preview body — the uploaded spec + an optional non-prod/public target.</summary>
public sealed record CreatePreviewRequest(
    [property: JsonPropertyName("spec")] string? Spec,
    [property: JsonPropertyName("targetUrl")] string? TargetUrl);

/// <summary>202 — the preview was enqueued + the sandbox job started. Poll GET /api/preview/{token}.</summary>
public sealed record CreatePreviewAcceptedDto(
    [property: JsonPropertyName("token")] string Token);

/// <summary>GET /api/preview/{token} — the lifecycle status, plus the trace JSON once the sandbox writes it.</summary>
public sealed record PreviewStatusDto(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("trace")] string? Trace);
