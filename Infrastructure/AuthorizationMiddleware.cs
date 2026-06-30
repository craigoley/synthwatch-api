using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using SynthWatch.Api.Data;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Phase 12 slice 2 — THE GATE. Rejects unauthorized writes and records the audit trail. Registered AFTER
/// the exception-shielding middleware, so a session-lookup error bubbles to it → a shielded 500 = DENIED
/// (fail-closed; an auth error is never an open door).
///
/// ★ FLAG-GATED: inert unless AUTH_ENFORCEMENT_ENABLED is true (default OFF). Off → every request passes as
/// today (deploy-safe: this can ship before the dashboard sends tokens, slice 3). On → mutating verbs require
/// a valid editor/admin session (except the login/access allowlist), and every authorized mutation is audited.
/// </summary>
public sealed class AuthorizationMiddleware : IFunctionsWorkerMiddleware
{
    private readonly ILogger<AuthorizationMiddleware> _logger;

    public AuthorizationMiddleware(ILogger<AuthorizationMiddleware> logger) => _logger = logger;

    /// <summary>The master switch. DEFAULT OFF — only "true"/"1" turns enforcement on.</summary>
    public static bool EnforcementEnabled()
    {
        var v = Environment.GetEnvironmentVariable("AUTH_ENFORCEMENT_ENABLED");
        return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var http = context.GetHttpContext();
        if (http is null)
        {
            await next(context); // non-HTTP trigger
            return;
        }

        var method = http.Request.Method;
        var path = http.Request.Path.Value;

        // Only an enforced, mutating, non-allowlisted request is gated + audited. Everything else — flag off,
        // any GET, or a login/access endpoint — passes untouched (today's behavior; no DB hit).
        if (!AuthGate.ShouldAudit(method, path, EnforcementEnabled()))
        {
            await next(context);
            return;
        }

        // Resolve the caller server-side from the bearer token (the client supplies ONLY the opaque token;
        // the role is derived from the DB/env, never trusted from a header). A lookup exception is NOT caught
        // here → it bubbles to the outer exception middleware → 500 = denied (fail-closed).
        var principal = await context.InstanceServices
            .GetRequiredService<IAuthPrincipal>()
            .FromBearerAsync(http.Request.Headers.Authorization, context.CancellationToken);

        var outcome = AuthGate.Decide(method, path, enforcementEnabled: true, role: principal?.Role);
        if (outcome != GateOutcome.Allow)
        {
            // ★ Durable record of the denial (the probing/brute-force record that previously went unwritten).
            // Best-effort + never-throws → the 401/403 RESPONSE is unchanged whether or not the audit write lands.
            await TryAuditDenialAsync(context, http, principal, method, path, outcome);
            await DenyAsync(http, outcome, context.InvocationId); // short-circuit; do NOT call next
            return;
        }

        http.Items["principal"] = principal; // handlers + the audit write read this

        Exception? failure = null;
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            failure = ex;
            throw; // rethrow so the outer exception middleware still shields it
        }
        finally
        {
            await TryAuditAsync(context, http, principal!, method, path, failure);
        }
    }

    private static async Task DenyAsync(HttpContext http, GateOutcome outcome, string instanceId)
    {
        var (code, error, title, detail) = outcome == GateOutcome.Deny401
            ? (StatusCodes.Status401Unauthorized, "unauthorized", "Unauthorized", "Authentication required.")
            : (StatusCodes.Status403Forbidden, "forbidden", "Forbidden", "You do not have permission to perform this action.");
        http.Response.StatusCode = code;
        // RFC 9457 problem+json (+ legacy error/message the dashboard reads); instance = the correlation id.
        await http.Response.WriteAsJsonAsync(ProblemResults.Body(code, title, detail, instanceId, error),
            options: null, contentType: ProblemResults.ContentType);
    }

    /// <summary>Best-effort durable record of a DENIED request (401/403): who (null email for a 401), verb, route,
    /// status. Same isolated-context + NEVER-THROWS guarantee as the authorized-mutation audit — an audit-write
    /// failure must never turn a denial into a 500.</summary>
    private async Task TryAuditDenialAsync(FunctionContext context, HttpContext http, Principal? principal,
        string method, string? path, GateOutcome outcome)
    {
        var status = outcome == GateOutcome.Deny401 ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden;
        var row = AuditWriter.BuildDenialRow(principal?.Email, ClientIp(http), method, path, status);
        var ds = context.InstanceServices.GetRequiredService<NpgsqlDataSource>();
        await AuditWriter.TryPersistAsync(ds, row, ex => AuthzLog.AuditFailed(_logger, ex), context.CancellationToken);
    }

    /// <summary>Write the audit envelope (+ redacted diff). Uses a FRESH DbContext so the insert is isolated
    /// from the handler's context state (clean on the success, 4xx, and 500 paths). An audit-write failure is
    /// logged but never breaks the response — the write already happened.</summary>
    private async Task TryAuditAsync(FunctionContext context, HttpContext http, Principal principal, string method, string? path, Exception? failure)
    {
        try
        {
            var status = failure is not null ? StatusCodes.Status500InternalServerError : http.Response.StatusCode;
            var success = failure is null && status < 400;
            var diff = context.InstanceServices.GetRequiredService<IAuditScope>().Diff;
            var row = AuditWriter.BuildRow(principal, ClientIp(http), method, path, status, success, diff);

            var ds = context.InstanceServices.GetRequiredService<NpgsqlDataSource>();
            await AuditWriter.TryPersistAsync(ds, row, ex => AuthzLog.AuditFailed(_logger, ex), context.CancellationToken);
        }
        catch (Exception ex)
        {
            AuthzLog.AuditFailed(_logger, ex); // covers a row-BUILD failure (the write itself never throws)
        }
    }

    private static string? ClientIp(HttpContext http)
    {
        var fwd = http.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(fwd))
            return fwd.Split(',')[0].Trim();
        return http.Connection.RemoteIpAddress?.ToString();
    }
}

internal static partial class AuthzLog
{
    [LoggerMessage(EventId = 5200, Level = LogLevel.Error, Message = "Audit write failed (the mutation itself succeeded)")]
    public static partial void AuditFailed(ILogger logger, Exception ex);
}
