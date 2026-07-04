using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// The shared read-side session gate — the #154 forensic-artifact pattern, extracted so every GET that serves
/// credentials or operator config uses ONE implementation (artifact streams, reconcile plan/drift, channel
/// configs, request-header readbacks). Requires a valid EDITOR or ADMIN session: 401 problem+json when there
/// is no valid session; 403 when the session is valid but its LIVE role is neither (a revoked editor whose
/// session hasn't expired). That role floor mirrors the write-gate (<see cref="AuthGate.Decide"/>), so a
/// removed editor loses these reads at the same instant they lose write access.
///
/// ★ Flag-gated on AUTH_ENFORCEMENT_ENABLED — the SAME switch the write-gate uses — so it's deploy-safe
/// (inert when off) and enforces in prod (where enforcement is on; the flag is fail-closed). The middleware's
/// verb-gate can't cover this: a GET is always Allow there, so sensitive reads must self-guard. The caller is
/// resolved from the bearer via the same <see cref="IAuthPrincipal"/> the middleware uses — role derived from
/// DB/env, never trusted from a header.
/// </summary>
public static class SessionReadGate
{
    /// <summary>Deny result (401/403 problem+json) or null when the request may proceed. A null
    /// <paramref name="auth"/> (test convenience — DI always injects) fails closed when enforcement is on.</summary>
    public static async Task<IActionResult?> RequireSessionAsync(IAuthPrincipal? auth, HttpRequest req, CancellationToken ct)
    {
        if (!AuthorizationMiddleware.EnforcementEnabled())
            return null; // flag OFF → inert (deploy-safe; matches the rest of the security model)
        var principal = auth is null ? null : await auth.FromBearerAsync(req.Headers.Authorization, ct);
        if (principal is null)
            return ApiResults.Unauthorized("Authentication required.");        // no valid session → 401
        if (!principal.CanWrite)
            return ApiResults.Forbidden("You do not have permission to perform this action."); // revoked role → 403
        return null;
    }

    /// <summary>True when the caller holds a live editor/admin session — or when enforcement is off (inert).
    /// For FIELD-level gating (e.g. serving a check without its request_headers to anonymous readers) where
    /// the endpoint itself stays open.</summary>
    public static async Task<bool> HasWriteSessionAsync(IAuthPrincipal? auth, HttpRequest req, CancellationToken ct)
    {
        if (!AuthorizationMiddleware.EnforcementEnabled())
            return true;
        var principal = auth is null ? null : await auth.FromBearerAsync(req.Headers.Authorization, ct);
        return principal is { CanWrite: true };
    }
}
