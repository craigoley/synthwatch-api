namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// One routing edge (runner migration 0023 / #81): a channel receives alerts for EITHER a severity
/// default (<see cref="Severity"/> set, 'critical'|'warning') OR a per-check override
/// (<see cref="CheckId"/> set) — a DB CHECK enforces exactly one. channel_id and check_id are both
/// ON DELETE CASCADE, so deleting a channel/check removes its routes at the DB. The API serves the
/// contract's { severity, perCheck } shape assembled from these rows.
/// </summary>
public class AlertRoute
{
    public long Id { get; set; }
    public string? Severity { get; set; }
    public long? CheckId { get; set; }
    public long ChannelId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
