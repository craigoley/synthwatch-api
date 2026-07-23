using SynthWatch.Api.Data.Entities;

namespace SynthWatch.Api.Dtos;

public record RunDto(
    long Id,
    long CheckId,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int? DurationMs,
    int? HttpStatus,
    string? ErrorMessage,
    string? FailedStep,
    // The API proxy path (GET /api/runs/{id}/screenshot), or null when the run has no screenshot.
    // Repurposed from the raw blob URL — that never worked (artifacts account blocks public access,
    // so the dashboard <img> on the raw URL 409'd); the proxy streams it behind the API.
    string? ScreenshotUrl,
    // SSL: structured cert days-remaining for this run (null for non-ssl runs).
    int? CertDaysRemaining,
    // Downloadable Playwright trace: the API proxy path (GET /api/runs/{id}/trace), or null when
    // the run has no trace (passing/non-browser runs). Not the raw blob URL — the proxy streams it.
    string? TraceUrl,
    // Multi-location: the region this run executed from. "default" for single-location/legacy runs
    // (never null). The dashboard aggregates per-location verdicts ("up from eastus2, down from
    // westus"); the API serves the raw per-run value.
    string Location,
    // Attempts to reach the verdict (runner 0048). null for pre-telemetry runs; 1 = clean first try;
    // >1 = settled on retry. pass + retryCount>1 = degrading-but-green. Additive (appended last).
    int? RetryCount,
    // Sandbox (runner migration 0065): true when this run was a PAUSED monitor's on-demand validation
    // (sandbox-run-when-paused). Skipped evaluate() (no incident/alert/SLO) but persisted a normal row;
    // the dashboard badges these so a resumed monitor's history stays honest. Additive (appended last).
    bool Sandbox,
    // True when the run has PERSISTED trace_signals (the compact, redacted network/console summary — #114),
    // INDEPENDENT of TraceUrl. A sensitive monitor's GREEN run stores no downloadable trace (TraceUrl null,
    // by B10 design) but DOES persist trace_signals — so the dashboard can surface "trace data available"
    // (the redacted summary via GET /api/runs/{id}/trace-signals) instead of reading as "no trace".
    bool HasTraceSignals,
    // Confirmation-retry (runner 0077). ConfirmationOfRunId: set when THIS run is a confirmation of an earlier
    // failed run (the id it confirms). SupersededByRunId: set when this run was a TRANSIENT failure whose
    // confirmation PASSED — it stays VISIBLE in history but is excluded from health signal. Both null for a
    // normal run. Additive (appended last); the P2 flaky UI reads them. camelCase → confirmationOfRunId/supersededByRunId.
    long? ConfirmationOfRunId,
    long? SupersededByRunId)
{
    public static RunDto From(Run r) => new(
        r.Id, r.CheckId, r.Status, r.StartedAt, r.FinishedAt, r.DurationMs,
        r.HttpStatus, r.ErrorMessage, r.FailedStep,
        ScreenshotUrl: string.IsNullOrEmpty(r.ScreenshotUrl) ? null : $"/api/runs/{r.Id}/screenshot",
        CertDaysRemaining: r.CertDaysRemaining,
        TraceUrl: string.IsNullOrEmpty(r.TraceUrl) ? null : $"/api/runs/{r.Id}/trace",
        Location: string.IsNullOrEmpty(r.Location) ? "default" : r.Location,
        RetryCount: r.RetryCount,
        Sandbox: r.Sandbox,
        HasTraceSignals: !string.IsNullOrEmpty(r.TraceSignals),
        ConfirmationOfRunId: r.ConfirmationOfRunId,
        SupersededByRunId: r.SupersededByRunId);
}

public record RunStepDto(
    long Id,
    long RunId,
    int StepIndex,
    string Name,
    string Status,
    int DurationMs,
    string? ErrorMessage,
    DateTimeOffset StartedAt)
{
    public static RunStepDto From(RunStep s) => new(
        s.Id, s.RunId, s.StepIndex, s.Name, s.Status, s.DurationMs, s.ErrorMessage, s.StartedAt);
}

public record RunMetricDto(
    long RunId,
    DateTimeOffset CapturedAt,
    int? TtfbMs,
    int? DomContentLoadedMs,
    int? LoadEventMs,
    int? FcpMs,
    int? LcpMs,
    long? TransferBytes,
    int? ResourceCount,
    int? DomNodeCount,
    long? JsHeapBytes,
    int? CpuTimeMs,
    int? LayoutCount,
    int? RecalcStyleCount,
    // Core Web Vitals. cls is unitless (double); inpMs is ms and is often null (no interaction).
    double? Cls,
    int? InpMs)
{
    public static RunMetricDto From(RunMetric m) => new(
        m.RunId, m.CapturedAt, m.TtfbMs, m.DomContentLoadedMs, m.LoadEventMs, m.FcpMs,
        m.LcpMs, m.TransferBytes, m.ResourceCount, m.DomNodeCount, m.JsHeapBytes,
        m.CpuTimeMs, m.LayoutCount, m.RecalcStyleCount, m.Cls, m.InpMs);
}

public record IncidentDto(
    long Id,
    long CheckId,
    string Status,
    string Severity,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ResolvedAt,
    long? OpenedRunId,
    long? ResolvedRunId,
    int ConsecutiveFailures,
    string? Summary,
    // Dashboard-parity: the incident's check name/kind (LEFT JOIN to checks). Null when the check
    // is missing — an incident must surface regardless, never join-dropped by its check.
    string? CheckName,
    string? CheckKind,
    // AI root-cause analysis (incidents.rca); null when RCA is off/failed/pre-existing.
    IncidentRca? Rca,
    // WHY it closed (runner 0095). null = genuine recovery; non-null = the stopped-monitor reconcile
    // (monitor_paused/archived/removed). Lets the dashboard explain a resolved-with-no-green timeline.
    string? ResolutionReason)
{
    public static IncidentDto From(Incident i, string? checkName, string? checkKind) => new(
        i.Id, i.CheckId, i.Status, i.Severity, i.OpenedAt, i.ResolvedAt,
        i.OpenedRunId, i.ResolvedRunId, i.ConsecutiveFailures, i.Summary,
        checkName, checkKind, i.Rca, i.ResolutionReason);
}

/// <summary>One run in an incident's timeline (GET /api/incidents/{id}). Artifact URLs are the API
/// proxy paths (null when absent) — same as RunDto, not the raw blob URLs.</summary>
public record TimelineEntryDto(
    long RunId,
    string Status,
    DateTimeOffset StartedAt,
    int? DurationMs,
    int? HttpStatus,
    string? ErrorMessage,
    string? FailedStep,
    string? ScreenshotUrl,
    string? TraceUrl,
    string Location,
    // Persisted trace_signals present (redacted summary), independent of TraceUrl — see RunDto.HasTraceSignals.
    bool HasTraceSignals)
{
    public static TimelineEntryDto From(Run r) => new(
        r.Id, r.Status, r.StartedAt, r.DurationMs, r.HttpStatus, r.ErrorMessage, r.FailedStep,
        ScreenshotUrl: string.IsNullOrEmpty(r.ScreenshotUrl) ? null : $"/api/runs/{r.Id}/screenshot",
        TraceUrl: string.IsNullOrEmpty(r.TraceUrl) ? null : $"/api/runs/{r.Id}/trace",
        Location: string.IsNullOrEmpty(r.Location) ? "default" : r.Location,
        HasTraceSignals: !string.IsNullOrEmpty(r.TraceSignals));
}

/// <summary>A prior incident on the same check (recurrence history; excludes the current one).</summary>
public record RecurrenceDto(
    long Id,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ResolvedAt,
    string Status,
    string? Summary);

/// <summary>
/// GET /api/incidents/{id} — one incident enriched for the investigation detail page: the incident
/// itself, its per-location current status, the timeline of runs in its window, and recurrence
/// history on the same check.
/// </summary>
public record IncidentDetailDto(
    long Id,
    long CheckId,
    string? CheckName,
    string? CheckKind,
    // The associated check's deployment environment (runner 0059: prod|staging|dev, default prod). Serializes
    // as `environment` — the platform-wide env field the dashboard's envOf()/<EnvBadge> read. Nullable: null
    // when the check is gone (LEFT-join tolerance), same as CheckName/CheckKind.
    string? Environment,
    string Status,
    string Severity,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ResolvedAt,
    // Open -> now; resolved -> resolved_at - opened_at, in seconds. Null while the incident is open.
    double? DurationSeconds,
    int ConsecutiveFailures,
    string? Summary,
    IncidentRca? Rca,
    // WHY it closed (runner 0095). null = genuine recovery; non-null = the stopped-monitor reconcile.
    string? ResolutionReason,
    IReadOnlyList<LocationStatusDto> PerLocation,
    IReadOnlyList<TimelineEntryDto> Timeline,
    IReadOnlyList<RecurrenceDto> Recurrence,
    // ★ Deploys DETECTED near this incident on the same host — possible correlation, NEVER causation. Empty
    // when none (honest-empty; the UI renders absence, never a fabricated row). Detail endpoint only.
    IReadOnlyList<NearbyDeployDto> NearbyDeploys,
    // ★ Timeline truncation contract: TotalRuns = how many runs fall in the incident window [opened_at, to]
    // (the lead streak is NOT counted); Truncated = the timeline carries only the NEWEST cap of them. The
    // two together let the dashboard render "showing newest N of TotalRuns" — and keep honest-empty
    // (TotalRuns=0, Truncated=false) distinguishable from honest-truncated.
    long TotalRuns,
    bool Truncated);

/// <summary>
/// A deploy DETECTED near an incident (same host, inside the proximity window). ★ CORRELATION, NOT CAUSATION:
/// <c>DetectedAt</c> is DETECTION time — the marker is captured passively by browser-check runs, so it carries
/// poll latency and is NOT authoritative deploy time. <c>OffsetMinutes</c> is signed relative to the incident's
/// opened_at (negative = detected BEFORE the incident opened; a small positive offset can still precede the
/// incident because detection lags reality). <c>Sha</c> is empty unless <c>IsSha</c>; otherwise
/// <c>Fingerprint</c> is the human label. (Plain record → camelCase JSON, matching this file's convention.)
/// </summary>
/// <summary>A short-lived, read-only, single-blob SAS URL for a trace zip — the browser fetches the blob
/// DIRECTLY (off the Vercel + API byte path). <see cref="Url"/> carries a bearer credential for its TTL; the
/// response is <c>Cache-Control: no-store</c>. <see cref="ExpiresAt"/> lets the client re-mint before it lapses.</summary>
public record TraceSasDto(string Url, DateTimeOffset ExpiresAt);

public record NearbyDeployDto(
    DateTimeOffset DetectedAt,
    string Source,
    bool IsSha,
    string Sha,
    string Fingerprint,
    int OffsetMinutes);

public record SlaDto(
    long CheckId,
    string CheckName,
    string Kind,
    DateTimeOffset WindowFrom,
    DateTimeOffset WindowTo,
    long CompletedRuns,
    long UpRuns,
    long DownRuns,
    // availabilityPct is nulled when insufficientData is true, so consumers can't render a
    // misleading precise % for a window the data doesn't actually span. Raw up/completed counts
    // are kept regardless. insufficientData is additive (new field) — backward-compatible.
    decimal? AvailabilityPct,
    bool InsufficientData);

/// <summary>Run-weighted fleet rollup for one window: SUM(up)/SUM(completed) across checks.</summary>
public record SlaFleetDto(
    long CompletedRuns,
    long UpRuns,
    long DownRuns,
    decimal? AvailabilityPct,
    bool InsufficientData);

/// <summary>The /api/sla response: the window, the fleet rollup, and per-check items.</summary>
public record SlaResponseDto(
    string Window,
    SlaFleetDto Fleet,
    IReadOnlyList<SlaDto> Items);

/// <summary>One bucket of the availability-over-time series; availabilityPct is null for a no-data
/// bucket (a gap in the line, not 0% which would read as "down").</summary>
public record AvailabilityPointDto(
    DateTimeOffset Ts,
    decimal? AvailabilityPct,
    long UpRuns,
    long DownRuns);

/// <summary>
/// GET /api/checks/{id}/availability-series — uptime % per bucket over the window. Uses the SAME
/// up=pass|warn / down=fail|error taxonomy + maintenance exclusion as sla_availability, so the series
/// integrated over the window matches the SLA panel's headline % (locked by a reconciliation test).
/// </summary>
public record AvailabilitySeriesDto(
    string Window,
    string Bucket,
    IReadOnlyList<AvailabilityPointDto> Points);

/// <summary>A browser flow from the runner-owned flow_manifest (the catalogue of flows).</summary>
public record FlowDto(
    string Name,
    string? Description,
    string? EntryUrlHint,
    DateTimeOffset UpdatedAt)
{
    public static FlowDto From(FlowManifest f) => new(f.Name, f.Description, f.EntryUrlHint, f.UpdatedAt);
}

/// <summary>Generic paged envelope for list endpoints.</summary>
public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total);

/// <summary>
/// Cursor-paginated envelope for append-only list endpoints (runs now, incidents next).
/// <c>NextCursor</c> is the opaque token to pass back as <c>?cursor=</c> for the following page;
/// null when the date-range window is exhausted. There is intentionally NO total — counting an
/// unbounded append-only table is exactly the all-rows scan cursor pagination exists to avoid.
/// </summary>
public record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor, int PageSize);

/// <summary>
/// The runs-list response: the cursor envelope PLUS an in-band freshness signal. <c>LatestRunId</c> is the id of
/// the most-recent run for this check IGNORING the page's date-range/cursor window, so a client can distinguish a
/// windowed/stale page (<c>LatestRunId &gt; Items[0].Id</c> ⇒ newer runs exist outside this window) from
/// genuinely-current data (<c>LatestRunId == Items[0].Id</c>). Resolves the frozen-<c>to</c>-class confusion
/// (#131) in-band — the funnel can prove "nothing newer" vs "the window excluded newer rows" from one response.
/// <c>null</c> ⇒ the check has no runs at all. <c>Items</c>/<c>NextCursor</c>/<c>PageSize</c> are byte-for-byte the
/// prior <see cref="CursorPage{T}"/> shape — <c>LatestRunId</c> is purely additive.
/// </summary>
public record RunsPage(IReadOnlyList<RunDto> Items, string? NextCursor, int PageSize, long? LatestRunId);
