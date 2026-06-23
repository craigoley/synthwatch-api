namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// A check's per-location cadence cursor (runner migration #73 / #68). The set of rows for a check IS
/// its location assignment — it runs from exactly those locations. last_run_at is the cadence cursor
/// (NULL = never run there = due-now). PK (check_id, location); ON DELETE CASCADE when the check is
/// deleted. The API seeds these at create (#62) and edits the set via PUT /api/checks/{id}/locations.
/// </summary>
public class CheckLocation
{
    public long CheckId { get; set; }
    public string Location { get; set; } = null!;
    public DateTimeOffset? LastRunAt { get; set; }
}
