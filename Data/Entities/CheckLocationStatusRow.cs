namespace SynthWatch.Api.Data.Entities;

/// <summary>Keyless projection for the check-detail "By location" rollup — one row per ASSIGNED location
/// (check_locations WHERE check_id = X), LEFT JOIN LATERAL its latest run's status. It drives from the
/// ASSIGNED set, not runs history, so a DROPPED location (runs exist but no check_locations row) is EXCLUDED,
/// and a freshly-ADDED location with no run yet yields <see cref="Status"/> = NULL — the honest "pending"
/// state (never a fabricated pass, never absent). Same LEFT-JOIN-check_locations discipline as
/// <see cref="RegionHealthRow"/>, applied per-check. <see cref="CheckId"/> is selected only by the BATCHED
/// grid variant (ListChecks) so its rows can be grouped per check; the single-check detail query fills it too
/// (it already filters on check_id). Read-only.</summary>
public class CheckLocationStatusRow
{
    public long CheckId { get; set; }
    public string Location { get; set; } = "";
    public string? Status { get; set; }
}
