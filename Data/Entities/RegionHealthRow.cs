namespace SynthWatch.Api.Data.Entities;

/// <summary>Keyless projection for GET /api/reports/region-health — one row per ENABLED region (locations
/// WHERE enabled), with its freshness = MAX(check_locations.last_run_at). The runner advances
/// check_locations.last_run_at at CLAIM time on every run (pass OR fail, before the run executes — see the
/// runner's claim()/forceClaim()), so this is a pure LIVENESS signal: a dark region stops claiming and its
/// max goes stale. <see cref="LastRunAt"/> is NULL when the region has zero claim data (no check_locations
/// rows, or only never-claimed cursors) — the honest "never reported" state, never a fabricated fresh row.
/// Rolled into fresh/stale/never_reported by RegionHealthProjection. Read-only.</summary>
public class RegionHealthRow
{
    public string Location { get; set; } = "";
    public DateTimeOffset? LastRunAt { get; set; }
}
