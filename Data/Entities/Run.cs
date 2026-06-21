namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// One row per check execution. Maps to the live <c>runs</c> table (10 columns).
/// </summary>
public class Run
{
    public long Id { get; set; }

    public long CheckId { get; set; }

    // CHECK: status IN ('pass','fail') — and nothing else. Default 'fail'.
    public string Status { get; set; } = "fail";

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public int? DurationMs { get; set; }

    public int? HttpStatus { get; set; }

    public string? ErrorMessage { get; set; }

    public string? FailedStep { get; set; }

    public string? ScreenshotUrl { get; set; }

    // Navigation (read-mostly).
    public Check? Check { get; set; }
    public List<RunStep> Steps { get; set; } = new();
    public RunMetric? Metrics { get; set; }
}
