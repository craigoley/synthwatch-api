using SynthWatch.Api.Data.Entities;

namespace SynthWatch.Api.Infrastructure;

/// <summary>Builds the <see cref="AuditLog"/> row for one authorized mutation — the automatic envelope
/// merged with the handler's optional (redacted) diff. Pure + testable; the middleware just persists it.</summary>
public static class AuditWriter
{
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
