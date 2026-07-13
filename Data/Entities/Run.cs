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

    // Compact filtered trace summary (network + filtered console), persisted by the runner AT CAPTURE TIME
    // (#114, runs.trace_signals jsonb) — same TraceSignalsDto schema as the API's TraceExtractor. NULL for a
    // run with no trace (a pass without a baseline refresh, non-browser). Read by the baseline-diff endpoint
    // (prefer this; fall back to on-demand extraction from TraceUrl when null but a trace exists).
    public string? TraceSignals { get; set; }

    // SSL checks: measured cert days-remaining at run time. Nullable (null for non-ssl runs).
    public int? CertDaysRemaining { get; set; }

    // Attempts taken to reach this run's verdict (runner migration 0048; runs.retry_count). 1 = first try;
    // >1 = settled after fast-retry; = retries+1 when retries were exhausted. NULL for pre-telemetry runs.
    // status=pass AND retry_count>1 is the "degrading-but-green" signal (passes only on retry) the dashboard
    // surfaces. Additive/nullable — older runs simply have no value.
    public int? RetryCount { get; set; }

    // Multi-location: the region this run executed from (runner multi-location migration). text with
    // DEFAULT 'default' but NULLABLE in the live schema — an explicit NULL is allowed, so the CLR
    // property must be nullable or EF throws InvalidCastException materializing a null row. Every
    // consumer coalesces null/empty -> "default" (RunDto/TimelineEntryDto + the per-location rollups).
    public string? Location { get; set; }

    // Sandbox (runner migration 0065): true when this row was written by a PAUSED monitor's on-demand
    // validation run (sandbox-run-when-paused). Such runs skip evaluate() (no incident/alert/SLO) but
    // persist a normal row — this flag keeps them distinguishable in history + the SLO lookback after
    // the monitor is resumed. DEFAULT false (NOT NULL) → every real run is false.
    public bool Sandbox { get; set; }

    // Confirmation-retry (runner migration 0077). ConfirmationOfRunId: set on a CONFIRMATION run, points at the
    // original it confirms. SupersededByRunId: set on the ORIGINAL when its confirmation PASSED (⇒ it was a
    // transient); the read-side health filters exclude runs where this IS NOT NULL. Both nullable (most runs
    // have neither). Runner-written only — the API never writes runs.
    public long? ConfirmationOfRunId { get; set; }
    public long? SupersededByRunId { get; set; }

    // B3-2 stage 2 (runner migration 0079): the classification of a SUPERSEDED transient — 'monitor-side' /
    // 'service-side' / 'indeterminate' (NULL on every non-superseded run). Runner-written; the API reads it to
    // grade the spurious-red trust dimension and (B3-3) to burn ONLY monitor-side transients.
    public string? TransientClass { get; set; }

    // Navigation (read-mostly).
    public Check? Check { get; set; }
    public List<RunStep> Steps { get; set; } = new();
    public RunMetric? Metrics { get; set; }
}
