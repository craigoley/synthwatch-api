namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// Structural funnel telemetry; one row per recorded step. Maps to <c>run_steps</c> (8 columns).
/// </summary>
public class RunStep
{
    public long Id { get; set; }

    public long RunId { get; set; }

    public int StepIndex { get; set; }

    public string Name { get; set; } = null!;

    // CHECK: status IN ('pass','fail','error'). Widened by the runner from ('pass','fail').
    // (Steps have no 'warn'/'running' — see Data/RunStatus.cs.)
    public string Status { get; set; } = null!;

    public int DurationMs { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    // Navigation (read-mostly).
    public Run? Run { get; set; }
}
