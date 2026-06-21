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
    string? ScreenshotUrl)
{
    public static RunDto From(Run r) => new(
        r.Id, r.CheckId, r.Status, r.StartedAt, r.FinishedAt, r.DurationMs,
        r.HttpStatus, r.ErrorMessage, r.FailedStep, r.ScreenshotUrl);
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
    int? RecalcStyleCount)
{
    public static RunMetricDto From(RunMetric m) => new(
        m.RunId, m.CapturedAt, m.TtfbMs, m.DomContentLoadedMs, m.LoadEventMs, m.FcpMs,
        m.LcpMs, m.TransferBytes, m.ResourceCount, m.DomNodeCount, m.JsHeapBytes,
        m.CpuTimeMs, m.LayoutCount, m.RecalcStyleCount);
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
    string? Summary)
{
    public static IncidentDto From(Incident i) => new(
        i.Id, i.CheckId, i.Status, i.Severity, i.OpenedAt, i.ResolvedAt,
        i.OpenedRunId, i.ResolvedRunId, i.ConsecutiveFailures, i.Summary);
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
    decimal? AvailabilityPct)
{
    public static SlaDto From(SlaAvailabilityRow r) => new(
        r.CheckId, r.CheckName, r.Kind, r.WindowFrom, r.WindowTo,
        r.CompletedRuns, r.UpRuns, r.DownRuns, r.AvailabilityPct);
}

/// <summary>Generic paged envelope for list endpoints.</summary>
public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total);
