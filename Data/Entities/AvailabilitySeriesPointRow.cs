namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// Keyless projection of one bucket of the availability-over-time series (an inline bucketed query
/// in ChecksFunctions that mirrors sla_availability's up=pass|warn / down=fail|error taxonomy +
/// maintenance-window exclusion, so the series reconciles with the SLA panel's headline %).
/// AvailabilityPct is NULL for a bucket with no completed runs (a gap, not 0%).
/// </summary>
public class AvailabilitySeriesPointRow
{
    public DateTimeOffset Ts { get; set; }
    public long UpRuns { get; set; }
    public long DownRuns { get; set; }
    public decimal? AvailabilityPct { get; set; }
}
