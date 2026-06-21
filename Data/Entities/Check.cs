namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// Catalogue of monitored targets. Maps to the live <c>checks</c> table (20 columns).
/// The runner owns this schema; this API maps to it read-mostly and never migrates it.
/// </summary>
public class Check
{
    // bigint GENERATED ALWAYS AS IDENTITY — never set on insert.
    public long Id { get; set; }

    public string Name { get; set; } = null!;

    // CHECK: kind IN ('http','browser')
    public string Kind { get; set; } = null!;

    public string TargetUrl { get; set; } = null!;

    public string? FlowName { get; set; }

    public string Method { get; set; } = "GET";

    public int ExpectedStatus { get; set; } = 200;

    public string? BodyMustContain { get; set; }

    public int IntervalSeconds { get; set; } = 300;

    public DateTimeOffset? LastRunAt { get; set; }

    public int TimeoutMs { get; set; } = 30000;

    public int FailureThreshold { get; set; } = 3;

    // CHECK: severity IN ('critical','warning')
    public string Severity { get; set; } = "critical";

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public bool LighthouseEnabled { get; set; }

    public int? LighthouseIntervalSeconds { get; set; }

    public string LighthouseFormFactor { get; set; } = "desktop";

    public int? PerfBudgetLcpMs { get; set; }

    // bigint — maps natively to long, no string trap.
    public long? PerfBudgetTransferBytes { get; set; }

    // Navigation (read-mostly).
    public List<Run> Runs { get; set; } = new();
    public List<Incident> Incidents { get; set; } = new();
}
