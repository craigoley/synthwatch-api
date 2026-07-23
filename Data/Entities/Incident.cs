namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// Open/resolved incident lifecycle per check. Maps to <c>incidents</c>.
/// Partial unique index allows at most one open incident per check.
/// </summary>
public class Incident
{
    public long Id { get; set; }

    public long CheckId { get; set; }

    // CHECK: status IN ('open','resolved')
    public string Status { get; set; } = null!;

    // CHECK: severity IN ('critical','warning')
    public string Severity { get; set; } = null!;

    public DateTimeOffset OpenedAt { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }

    public long? OpenedRunId { get; set; }

    public long? ResolvedRunId { get; set; }

    public int ConsecutiveFailures { get; set; }

    public string? Summary { get; set; }

    /// <summary>WHY the incident closed (runner 0095). NULL = a genuine cross-location recovery run; non-null
    /// = closed by the runner's stopped-monitor reconcile (monitor_paused/archived/removed). Read-only for the
    /// API — the runner writes it. /reports/mttr excludes non-null; the dashboard uses it to explain a
    /// resolved-with-no-green-recovery timeline.</summary>
    public string? ResolutionReason { get; set; }

    // AI root-cause analysis (migration 0015; jsonb). Null when RCA is off / failed / pre-existing.
    public IncidentRca? Rca { get; set; }

    // Navigation (read-mostly).
    public Check? Check { get; set; }
}
