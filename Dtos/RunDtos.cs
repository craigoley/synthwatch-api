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
    string? ScreenshotUrl,
    // SSL: structured cert days-remaining for this run (null for non-ssl runs).
    int? CertDaysRemaining,
    // Downloadable Playwright trace: the API proxy path (GET /api/runs/{id}/trace), or null when
    // the run has no trace (passing/non-browser runs). Not the raw blob URL — the proxy streams it.
    string? TraceUrl)
{
    public static RunDto From(Run r) => new(
        r.Id, r.CheckId, r.Status, r.StartedAt, r.FinishedAt, r.DurationMs,
        r.HttpStatus, r.ErrorMessage, r.FailedStep, r.ScreenshotUrl, r.CertDaysRemaining,
        TraceUrl: string.IsNullOrEmpty(r.TraceUrl) ? null : $"/api/runs/{r.Id}/trace");
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
    // Dashboard-parity: the incident's check name/kind (join to checks).
    string CheckName,
    string CheckKind)
{
    public static IncidentDto From(Incident i, string checkName, string checkKind) => new(
        i.Id, i.CheckId, i.Status, i.Severity, i.OpenedAt, i.ResolvedAt,
        i.OpenedRunId, i.ResolvedRunId, i.ConsecutiveFailures, i.Summary,
        checkName, checkKind);
}

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
