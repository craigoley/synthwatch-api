using System.Text.Json.Serialization;

namespace SynthWatch.Api.Dtos;

/// <summary>POST /api/preview body — the uploaded spec + an optional non-prod/public target.</summary>
public sealed record CreatePreviewRequest(
    [property: JsonPropertyName("spec")] string? Spec,
    [property: JsonPropertyName("targetUrl")] string? TargetUrl,
    [property: JsonPropertyName("credentials")] CreatePreviewCredentials? Credentials = null,
    /// <summary>
    /// "Redact credentials from output" — per-run, editor/admin only. ★ DEFAULT TRUE, including when the
    /// field is ABSENT: a client that has not been updated must not silently disable redaction. Only an
    /// explicit `false` opts into raw output, and that choice is recorded in sandbox_preview + audit_log.
    /// </summary>
    [property: JsonPropertyName("redactCredentials")] bool? RedactCredentials = null);

/// <summary>
/// OPTIONAL per-run credentials the user typed in the Tests UI, so a preview can drive a login-gated flow.
///
/// ★ NEVER PERSISTED. These are sealed into the ephemeral {token}.payload blob (deleted on read by the
/// sandbox) and are absent from sandbox_preview (which stores spec_sha256 only), from audit_log (which
/// records {specSha256, targetUrl} only), from the ARM start body, and from every log and exception path.
///
/// ★ <c>VercelBypassToken</c> is USER-PASTED, never server-injected from the platform's own
/// VERCEL_BYPASS_TOKEN — see <see cref="SynthWatch.Api.Infrastructure.SandboxPayload.Credentials"/> for why.
/// </summary>
public sealed record CreatePreviewCredentials(
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("password")] string? Password,
    [property: JsonPropertyName("vercelBypassToken")] string? VercelBypassToken);

/// <summary>202 — the preview was enqueued + the sandbox job started. Poll GET /api/preview/{token}.</summary>
public sealed record CreatePreviewAcceptedDto(
    [property: JsonPropertyName("token")] string Token);

/// <summary>GET /api/preview/{token} — the lifecycle status, plus the trace JSON once the sandbox writes it.</summary>
public sealed record PreviewStatusDto(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("trace")] string? Trace);

/// <summary>GET /api/preview/quota — the caller's live bounds so the UI can show "N of M" instead of a mystery
/// 429. running/hourly are the SAME counts the POST enforces (recent 'running' rows; the caller's rows this
/// hour); the maxima are the server's caps.</summary>
public sealed record PreviewQuotaDto(
    [property: JsonPropertyName("running")] int Running,
    [property: JsonPropertyName("maxConcurrent")] int MaxConcurrent,
    [property: JsonPropertyName("hourly")] int Hourly,
    [property: JsonPropertyName("maxPerHour")] int MaxPerHour);
