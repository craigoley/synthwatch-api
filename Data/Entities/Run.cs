namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// One row per check execution. Maps to the live <c>runs</c> table (11 columns).
/// </summary>
public class Run
{
    public long Id { get; set; }

    public long CheckId { get; set; }

    // CHECK: status IN ('pass','warn','fail','error','running'). Default 'fail'.
    // Widened by the runner from the original ('pass','fail'); see Data/RunStatus.cs for the
    // full taxonomy and health classification (up=pass|warn, down=fail|error, running=in-flight).
    public string Status { get; set; } = "fail";

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public int? DurationMs { get; set; }

    public int? HttpStatus { get; set; }

    public string? ErrorMessage { get; set; }

    public string? FailedStep { get; set; }

    public string? ScreenshotUrl { get; set; }

    // SSL checks: measured cert days-remaining at run time. Nullable (null for non-ssl runs).
    public int? CertDaysRemaining { get; set; }

    // Navigation (read-mostly).
    public Check? Check { get; set; }
    public List<RunStep> Steps { get; set; } = new();
    public RunMetric? Metrics { get; set; }
}
