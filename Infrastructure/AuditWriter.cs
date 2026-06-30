using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SynthWatch.Api.Data;
using SynthWatch.Api.Data.Entities;

namespace SynthWatch.Api.Infrastructure;

/// <summary>Builds the <see cref="AuditLog"/> row for one authorized mutation — the automatic envelope
/// merged with the handler's optional (redacted) diff. Pure + testable; the middleware just persists it.</summary>
public static class AuditWriter
{
    /// <summary>
    /// The audit row for a DENIED request (401/403) — the durable record of a probe against the security
    /// boundary that previously went unwritten. <paramref name="actorEmail"/> is null for a 401 (no valid
    /// session) and set for a 403 (valid session, insufficient role). No diff/secret — just who/what/when.
    /// </summary>
    public static AuditLog BuildDenialRow(string? actorEmail, string? ip, string method, string? rawPath, int statusCode)
    {
        var route = AuthGate.RouteOf(rawPath);
        return new AuditLog
        {
            ActorEmail = actorEmail,
            ActorIp = ip,
            Action = "auth.denied",
            TargetType = FirstSegment(route),
            TargetId = FirstNumericSegment(route),
            HttpMethod = method.ToUpperInvariant(),
            HttpPath = AuthGate.NormalizePath(rawPath),
            StatusCode = statusCode,
            Success = false,
            Note = statusCode == StatusCodes.Status401Unauthorized ? "denied: unauthenticated" : "denied: insufficient role",
        };
    }

    /// <summary>
    /// Persist an audit row on a FRESH, isolated <see cref="SynthWatchDbContext"/> (independent of the request's
    /// context state, so it survives the success, 4xx, AND 500 paths). ★ NEVER THROWS — a failed audit write is
    /// swallowed (and surfaced via <paramref name="onFailure"/>) so it can never turn a response into a 500.
    /// Shared by the authorized-mutation audit and the denial audit. Returns true iff the row was written.
    /// </summary>
    public static async Task<bool> TryPersistAsync(NpgsqlDataSource dataSource, AuditLog row,
        Action<Exception>? onFailure = null, CancellationToken ct = default)
    {
        try
        {
            var options = new DbContextOptionsBuilder<SynthWatchDbContext>().UseNpgsql(dataSource).Options;
            await using var auditDb = new SynthWatchDbContext(options);
            auditDb.AuditLogs.Add(row);
            await auditDb.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            onFailure?.Invoke(ex);
            return false;
        }
    }

    public static AuditLog BuildRow(
        Principal principal, string? ip, string method, string? rawPath, int statusCode, bool success, AuditDiff? diff)
    {
        var route = AuthGate.RouteOf(rawPath);
        return new AuditLog
        {
            ActorEmail = principal.Email,
            ActorIp = ip,
            Action = ActionFor(method),
            // The diff names the precise target; otherwise derive a coarse one from the route.
            TargetType = diff?.TargetType ?? FirstSegment(route),
            TargetId = diff?.TargetId ?? FirstNumericSegment(route),
            HttpMethod = method.ToUpperInvariant(),
            HttpPath = AuthGate.NormalizePath(rawPath),
            StatusCode = statusCode,
            Success = success,
            // ★ Redaction happens HERE (centralized), so a handler can never persist a plaintext secret.
            BeforeJson = diff is null ? null : AuditRedaction.RedactToJson(diff.Before),
            AfterJson = diff is null ? null : AuditRedaction.RedactToJson(diff.After),
            Note = diff?.Note,
        };
    }

    private static string ActionFor(string method) => method.ToUpperInvariant() switch
    {
        "POST" => "create",
        "PUT" or "PATCH" => "update",
        "DELETE" => "delete",
        var other => other.ToLowerInvariant(),
    };

    private static string? FirstSegment(string route)
    {
        var parts = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : null;
    }

    private static string? FirstNumericSegment(string route)
    {
        foreach (var p in route.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => long.TryParse(p, out _)))
            return p;
        return null;
    }
}
