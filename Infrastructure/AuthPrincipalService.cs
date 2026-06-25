using Microsoft.EntityFrameworkCore;
using SynthWatch.Api.Data;

namespace SynthWatch.Api.Infrastructure;

/// <summary>The authenticated caller: the session email + its LIVE role. Role is re-resolved on every
/// request (never trusted from the client), so a removed editor loses write access immediately.</summary>
public sealed record Principal(string Email, string Role)
{
    public bool IsAdmin => Role == Roles.Admin;
    public bool CanWrite => Role is Roles.Admin or Roles.Editor;
}

/// <summary>Resolves the request's bearer token to a <see cref="Principal"/>. ONE implementation shared by
/// the AuthorizationMiddleware (the gate) and AuthFunctions (/me) so they can never disagree about what a
/// valid session + role is. The client supplies only the opaque token; identity + role are derived here.</summary>
public interface IAuthPrincipal
{
    /// <summary>Bearer token → live, non-revoked, non-expired session → {email, live role}, or null
    /// (no/invalid/expired/revoked token). Role may be "anonymous" for a valid session whose email is no
    /// longer an editor/admin.</summary>
    Task<Principal?> FromBearerAsync(string? authorizationHeader, CancellationToken ct);

    /// <summary>email → admin (ADMIN_EMAILS app setting) | editor (editors table) | anonymous.</summary>
    Task<string> ResolveRoleAsync(string email, CancellationToken ct);
}

public sealed class AuthPrincipalService : IAuthPrincipal
{
    private readonly SynthWatchDbContext _db;

    public AuthPrincipalService(SynthWatchDbContext db) => _db = db;

    public async Task<Principal?> FromBearerAsync(string? authorizationHeader, CancellationToken ct)
    {
        var token = AuthTokens.BearerFrom(authorizationHeader);
        if (token is null)
            return null;

        var hash = AuthTokens.Sha256Hex(token);
        var now = DateTimeOffset.UtcNow;
        var session = await _db.Sessions
            .FirstOrDefaultAsync(s => s.TokenHash == hash && s.RevokedAt == null && s.ExpiresAt > now, ct);
        if (session is null)
            return null;

        var role = await ResolveRoleAsync(session.Email, ct);
        return new Principal(session.Email, role);
    }

    public async Task<string> ResolveRoleAsync(string email, CancellationToken ct)
    {
        if (AdminEmails().Contains(email))
            return Roles.Admin;
        if (await _db.Editors.AnyAsync(e => e.Email == email, ct))
            return Roles.Editor;
        return Roles.Anonymous;
    }

    /// <summary>The admin allowlist from the ADMIN_EMAILS app setting (comma-separated, normalized).
    /// ★ This is the API's security source of truth for admin — NOT the dashboard's Vercel env.</summary>
    public static HashSet<string> AdminEmails() =>
        (Environment.GetEnvironmentVariable("ADMIN_EMAILS") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(AuthTokens.NormalizeEmail)
            .ToHashSet(StringComparer.Ordinal);
}
