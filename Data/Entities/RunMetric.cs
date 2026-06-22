namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// Tier-1 per-run telemetry; one row per browser run. Maps to <c>run_metrics</c> (15 columns).
/// Every metric is nullable — a capture failure must never fail the check.
/// UNIQUE on run_id enforces one metrics row per run.
/// </summary>
public class RunMetric
{
    public long Id { get; set; }

    public long RunId { get; set; }

    public int? TtfbMs { get; set; }

    public int? DomContentLoadedMs { get; set; }

    public int? LoadEventMs { get; set; }

    public int? FcpMs { get; set; }

    public int? LcpMs { get; set; }

    // bigint — maps natively to long.
    public long? TransferBytes { get; set; }

    public int? ResourceCount { get; set; }

    public int? DomNodeCount { get; set; }

    // bigint — maps natively to long.
    public long? JsHeapBytes { get; set; }

    public int? CpuTimeMs { get; set; }

    public int? LayoutCount { get; set; }

    public int? RecalcStyleCount { get; set; }

    // Core Web Vitals (runner PR #36). CLS is unitless (double); INP is ms (int) and is often
    // null for synthetic loads with no interaction — keep nullable (0 != "not measured").
    public double? Cls { get; set; }
    public int? InpMs { get; set; }

    public DateTimeOffset CapturedAt { get; set; }

    // Navigation (read-mostly).
    public Run? Run { get; set; }
}
