namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// One row per check execution. Maps to the live <c>runs</c> table.
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

    // Playwright trace blob URL, captured on browser-run FAILURE (runner PR #39). Null otherwise.
    // Served via the API proxy (GET /api/runs/{id}/trace), not this raw URL.
    public string? TraceUrl { get; set; }

    // SSL checks: measured cert days-remaining at run time. Nullable (null for non-ssl runs).
    public int? CertDaysRemaining { get; set; }

    // Multi-location: the region this run executed from (runner multi-location migration). text with
    // DEFAULT 'default' but NULLABLE in the live schema — an explicit NULL is allowed, so the CLR
    // property must be nullable or EF throws InvalidCastException materializing a null row. Every
    // consumer coalesces null/empty -> "default" (RunDto/TimelineEntryDto + the per-location rollups).
    public string? Location { get; set; }

    // Navigation (read-mostly).
    public Check? Check { get; set; }
    public List<RunStep> Steps { get; set; } = new();
    public RunMetric? Metrics { get; set; }
}
