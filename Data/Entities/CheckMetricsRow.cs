namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// Keyless projection of the per-check dashboard-parity metrics, computed by the lateral-join
/// SQL ported verbatim from the dashboard's old route handler. <see cref="Spark"/> is the
/// json_agg result carried as JSON text and deserialized in the handler.
/// </summary>
public class CheckMetricsRow
{
    public long CheckId { get; set; }

    // percentile_cont returns double precision; null when there are no 24h runs.
    public double? P50Ms { get; set; }
    public double? P95Ms { get; set; }

    public int Runs24h { get; set; }

    public int OpenIncidentCount { get; set; }

    public string? MaxOpenSeverity { get; set; }

    /// <summary>JSON array text of SparkPoint objects ({t,d,s}); defaults to "[]".</summary>
    public string Spark { get; set; } = "[]";
}
