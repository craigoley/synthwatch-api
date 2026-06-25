namespace SynthWatch.Api.Data.Entities;

// Auth identity tables (Phase 12 slice 1, runner migration 0037). Logically API-owned: the API is the
// only reader/writer. Slice 1 mints/verifies sessions + records access requests; nothing is enforced
// until slice 2 adds the authz gate.

/// <summary>An emailed 6-digit login code. Only <c>sha256(code)</c> is stored (never the raw code);
/// one-time (<c>ConsumedAt</c>), expiring, and attempt-capped (<c>AttemptCount</c>).</summary>
public class OtpCode
{
    public long Id { get; set; }
    public string Email { get; set; } = null!;
    public string CodeHash { get; set; } = null!;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public int AttemptCount { get; set; }
    public string? RequestIp { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>An opaque bearer session. <c>TokenHash</c> = <c>sha256(raw token)</c>; the raw <c>swt_…</c>
/// token is returned to the client once at verify and never persisted. <c>RevokedAt</c> = logout/revoke.</summary>
public class Session
{
    public long Id { get; set; }
    public string TokenHash { get; set; } = null!;
    public string Email { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? IssuedIp { get; set; }
}

/// <summary>The admin-managed editor allowlist. Admins are NOT here — they come from the ADMIN_EMAILS
/// app setting (env-based so they can't be locked out by a DB edit). Email is the PK (normalized lowercase).</summary>
public class Editor
{
    public string Email { get; set; } = null!;
    public string AddedBy { get; set; } = null!;
    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>An enumeration-safe "request edit access" record. The endpoint always returns the same
/// response, so this row — not the response — is how an admin sees who asked (+ the rate-limit ledger).</summary>
public class AccessRequest
{
    public long Id { get; set; }
    public string Email { get; set; } = null!;
    public DateTimeOffset RequestedAt { get; set; }
    public string? RequestIp { get; set; }
}
