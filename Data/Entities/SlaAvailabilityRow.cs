namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// Keyless projection of the <c>sla_availability(p_from, p_to)</c> function and its
/// 24h / 7d / 30d views. The runner owns the SLA logic; this API only reads it.
/// </summary>
public class SlaAvailabilityRow
{
    public long CheckId { get; set; }

    public string CheckName { get; set; } = null!;

    public string Kind { get; set; } = null!;

    public DateTimeOffset WindowFrom { get; set; }

    public DateTimeOffset WindowTo { get; set; }

    public long CompletedRuns { get; set; }

    public long UpRuns { get; set; }

    public long DownRuns { get; set; }

    // numeric — may be NULL when there are no completed runs in the window.
    public decimal? AvailabilityPct { get; set; }
}
