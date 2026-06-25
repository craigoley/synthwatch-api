namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// One append-only audit record (Phase 12 slice 2, migration 0038). Written by the AuthorizationMiddleware
/// for every authorized mutating request: the envelope (actor / action / target / outcome) is automatic;
/// <see cref="BeforeJson"/>/<see cref="AfterJson"/> are an opt-in handler diff, REDACTED before storage.
/// The API can INSERT/SELECT only — UPDATE/DELETE are revoked, so the trail is immutable.
/// </summary>
public class AuditLog
{
    public long Id { get; set; }
    public DateTimeOffset Ts { get; set; }
    public string? ActorEmail { get; set; }
    public string? ActorIp { get; set; }
    public string? Action { get; set; }
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string? HttpMethod { get; set; }
    public string? HttpPath { get; set; }
    public int? StatusCode { get; set; }
    public bool? Success { get; set; }
    // Redacted JSON snapshots (jsonb), stored as a string — null when the handler recorded no diff.
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public string? Note { get; set; }
}
