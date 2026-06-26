namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// An on-demand "Run now" request (runner migration 0042). The API only INSERTs a 'pending' row (one per
/// POST /api/checks/{id}/run — at most one pending per check, enforced by a partial unique index) and READs
/// to coalesce; the RUNNER owns the lifecycle: it drains pending rows, force-runs the check through its
/// normal run path, and moves status 'pending' -> 'done' (+ completed_at). The API never mutates after insert.
/// </summary>
public class RunRequest
{
    public long Id { get; set; }
    public long CheckId { get; set; }
    public string Status { get; set; } = "pending";
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
