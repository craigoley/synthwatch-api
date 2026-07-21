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

    /// <summary>
    /// Per-run "Redact credentials from output" toggle (runner migration 0094). true = credentials scrubbed
    /// from trace/stdout/error/trace_signals (the default); false = the operator opted into RAW output.
    /// ★ AUDIT: this is the record of WHO ran an unredacted preview and WHEN — optional redaction on a
    /// code-execution surface is only defensible with a trail. Never nullable: absent ⇒ redacted.
    /// </summary>
    public bool RedactCredentials { get; set; } = true;
}
