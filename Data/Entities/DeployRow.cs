namespace SynthWatch.Api.Data.Entities;

/// <summary>Keyless projection of a `deploys` row (migration 0056, runner-owned) for GET /reports/deploys —
/// the chart overlay reads deploy markers per host/window. The runner writes them; this API only reads.</summary>
public class DeployRow
{
    public string TargetHost { get; set; } = "";
    public string? Sha { get; set; }
    public string Fingerprint { get; set; } = "";
    public bool IsSha { get; set; }
    public string Source { get; set; } = "";
    public DateTimeOffset DeployedAt { get; set; }
}

/// <summary>Keyless projection for the incident-detail deploy-proximity annotation: a deploy DETECTED near an
/// incident (same host, inside the window), plus its signed minute offset from the incident's opened_at. Read
/// via raw SQL only. detected_at is DETECTION time (poll latency) — correlation, never causation.</summary>
public class NearbyDeployRow
{
    public DateTimeOffset DetectedAt { get; set; }
    public string Source { get; set; } = "";
    public bool IsSha { get; set; }
    public string? Sha { get; set; }
    public string Fingerprint { get; set; } = "";
    public int OffsetMinutes { get; set; }
}
