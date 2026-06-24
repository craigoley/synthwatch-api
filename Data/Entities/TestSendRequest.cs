namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// A channel test-send request (runner migration 0026). The API only INSERTs a 'pending' row (one per
/// POST /api/channels/{id}/test) and READs its status; the RUNNER owns the lifecycle: it drains pending
/// rows, sends a [TEST] alert through the REAL dispatch path, and moves status pending -> sending ->
/// delivered|failed (writing detail + completed_at). The API never mutates a row after insert.
/// </summary>
public class TestSendRequest
{
    public long Id { get; set; }
    public long ChannelId { get; set; }
    public string Status { get; set; } = "pending";
    public string? Detail { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
