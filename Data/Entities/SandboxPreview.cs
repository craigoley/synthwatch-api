namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// The lifecycle + audit record for a spec preview-run (POST /api/preview), runner migration 0093. ★ Written by
/// the API, NOT the sandbox job — the sandbox ACA job is DB-less by design. The API INSERTs 'running', starts the
/// job, then UPDATEs on the blob poll. Stores the spec SHA-256 (never the body — the body rides an ephemeral env
/// override; the trace lives in the TTL'd sandbox-artifacts blob).
/// </summary>
public class SandboxPreview
{
    public long Id { get; set; }
    public string Token { get; set; } = "";
    public string ActorEmail { get; set; } = "";
    public string? ActorIp { get; set; }
    public string SpecSha256 { get; set; } = "";
    public string TargetUrl { get; set; } = "";
    public string Status { get; set; } = "running"; // running | done | failed | timeout
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int? ExitCode { get; set; }
    public string? Error { get; set; }
}
