namespace SynthWatch.Api.Infrastructure;

/// <summary>A handler-recorded before/after diff for the audit row. <see cref="Before"/>/<see cref="After"/>
/// are raw entity snapshots; the middleware serializes + REDACTS them before persisting.</summary>
public sealed record AuditDiff(string TargetType, string? TargetId, object? Before, object? After, string? Note);

/// <summary>
/// Request-scoped audit channel (tier 2 of the two-tier design). A handler OPTIONALLY records the rich
/// before/after diff via <see cref="Record"/>; the AuthorizationMiddleware reads it and merges it into the
/// single audit row it already writes (the envelope is automatic — every authorized mutation is audited
/// whether or not the handler records a diff, so auditing can't be bypassed by a forgetful handler).
/// </summary>
public interface IAuditScope
{
    void Record(string targetType, string? targetId, object? before, object? after, string? note = null);
    AuditDiff? Diff { get; }
}

public sealed class AuditScope : IAuditScope
{
    public AuditDiff? Diff { get; private set; }

    public void Record(string targetType, string? targetId, object? before, object? after, string? note = null) =>
        Diff = new AuditDiff(targetType, targetId, before, after, note);
}
