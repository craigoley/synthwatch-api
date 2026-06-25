namespace SynthWatch.Api.Infrastructure;

/// <summary>What the gate decides for one request.</summary>
public enum GateOutcome
{
    Allow,    // pass to the handler
    Deny401,  // no / invalid session
    Deny403,  // valid session, insufficient role
}

/// <summary>
/// The authorization decision as a PURE function — no DB, no HttpContext — so the whole security matrix is
/// exhaustively unit-testable. The middleware is thin glue that resolves the principal + calls <see cref="Decide"/>.
///
/// ★ FAIL-CLOSED BY VERB, not an endpoint allowlist: every mutating verb (POST/PUT/PATCH/DELETE) is denied by
/// default unless it's on the tiny <see cref="UnauthWriteAllowlist"/> (the login/access endpoints) or carries a
/// valid editor/admin session. A NEW write endpoint added later is therefore protected automatically — it isn't
/// on the allowlist, so it's denied until someone deliberately exempts it.
/// </summary>
public static class AuthGate
{
    private static readonly HashSet<string> MutatingMethods =
        new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH", "DELETE" };

    /// <summary>The ONLY unauthenticated writes — the login + access-request endpoints (you need them to
    /// obtain a session in the first place). Prefix-agnostic routes (the optional "/api" prefix is stripped).
    /// Keep this list tiny; it is the one thing to scrutinize for openness.</summary>
    public static readonly string[] UnauthWriteAllowlist =
        { "/auth/request-code", "/auth/verify", "/auth/request-access" };

    public static bool IsMutating(string method) => MutatingMethods.Contains(method);

    /// <summary>Lowercased, single leading slash, no trailing slash. "/Api/Checks/" → "/api/checks".</summary>
    public static string NormalizePath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return "/";
        return "/" + rawPath.Trim().ToLowerInvariant().Trim('/');
    }

    /// <summary>The route with the optional "/api" prefix removed, so matching works whether or not the host
    /// includes it in the path. "/api/auth/verify" → "/auth/verify"; "/api/checks" → "/checks".</summary>
    public static string RouteOf(string? rawPath)
    {
        var p = NormalizePath(rawPath);
        if (p == "/api")
            return "/";
        return p.StartsWith("/api/", StringComparison.Ordinal) ? p["/api".Length..] : p;
    }

    /// <summary>Admin-only writes — user management (/api/editors, slice 3). Matched here so they're admin-gated
    /// the moment they exist; handlers should ALSO check IsAdmin (defense in depth: a miss degrades to
    /// "editor could manage users", never "anonymous could").</summary>
    public static bool IsAdminOnlyRoute(string method, string? rawPath)
    {
        if (!IsMutating(method))
            return false;
        var r = RouteOf(rawPath);
        return r == "/editors" || r.StartsWith("/editors/", StringComparison.Ordinal);
    }

    /// <summary>
    /// The decision. <paramref name="role"/> is the LIVE resolved role of a valid session, or null when there
    /// is no valid session (no/invalid/expired/revoked token).
    /// </summary>
    public static GateOutcome Decide(string method, string? path, bool enforcementEnabled, string? role)
    {
        if (!enforcementEnabled)
            return GateOutcome.Allow;          // flag OFF → inert (today's behavior; the slice-3 deploy gate)
        if (!IsMutating(method))
            return GateOutcome.Allow;          // reads are always open (read-only default)
        if (UnauthWriteAllowlist.Contains(RouteOf(path)))
            return GateOutcome.Allow;          // login / access-request — the only unauthenticated writes
        if (role is null)
            return GateOutcome.Deny401;        // no valid session
        if (role is Roles.Editor or Roles.Admin)
        {
            if (IsAdminOnlyRoute(method, path) && role != Roles.Admin)
                return GateOutcome.Deny403;    // editor hitting an admin-only route
            return GateOutcome.Allow;
        }
        return GateOutcome.Deny403;            // valid session but insufficient role (e.g. removed editor)
    }

    /// <summary>True when this request must be recorded in audit_log (an enforced, gated mutation — i.e. not a
    /// read, not inert, not an allowlisted login endpoint). The middleware audits only when it also Allows.</summary>
    public static bool ShouldAudit(string method, string? path, bool enforcementEnabled) =>
        enforcementEnabled && IsMutating(method) && !UnauthWriteAllowlist.Contains(RouteOf(path));
}
