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
