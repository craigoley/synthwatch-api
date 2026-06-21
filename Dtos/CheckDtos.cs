using SynthWatch.Api.Data.Entities;

namespace SynthWatch.Api.Dtos;

/// <summary>A check plus its derived current status (from the latest run).</summary>
public record CheckSummaryDto(
    long Id,
    string Name,
    string Kind,
    string TargetUrl,
    string? FlowName,
    string Method,
    int ExpectedStatus,
    int IntervalSeconds,
    int TimeoutMs,
    int FailureThreshold,
    string Severity,
    bool Enabled,
    bool LighthouseEnabled,
    DateTimeOffset? LastRunAt,
    DateTimeOffset CreatedAt,
    // Derived:
    string CurrentStatus,
    long? LastRunId,
    int? LastDurationMs,
    int? LastHttpStatus,
    bool HasOpenIncident)
{
    public static CheckSummaryDto From(Check c, Run? latest, bool hasOpenIncident) => new(
        c.Id, c.Name, c.Kind, c.TargetUrl, c.FlowName, c.Method, c.ExpectedStatus,
        c.IntervalSeconds, c.TimeoutMs, c.FailureThreshold, c.Severity, c.Enabled,
        c.LighthouseEnabled, c.LastRunAt, c.CreatedAt,
        CurrentStatus: !c.Enabled ? "paused" : latest?.Status ?? "unknown",
        LastRunId: latest?.Id,
        LastDurationMs: latest?.DurationMs,
        LastHttpStatus: latest?.HttpStatus,
        HasOpenIncident: hasOpenIncident);
}

/// <summary>Full check view including its recent runs.</summary>
public record CheckDetailDto(
    long Id,
    string Name,
    string Kind,
    string TargetUrl,
    string? FlowName,
    string Method,
    int ExpectedStatus,
    string? BodyMustContain,
    int IntervalSeconds,
    DateTimeOffset? LastRunAt,
    int TimeoutMs,
    int FailureThreshold,
    string Severity,
    bool Enabled,
    DateTimeOffset CreatedAt,
    bool LighthouseEnabled,
    int? LighthouseIntervalSeconds,
    string LighthouseFormFactor,
    int? PerfBudgetLcpMs,
    long? PerfBudgetTransferBytes,
    string CurrentStatus,
    IReadOnlyList<RunDto> RecentRuns)
{
    public static CheckDetailDto From(Check c, IReadOnlyList<Run> recentRuns) => new(
        c.Id, c.Name, c.Kind, c.TargetUrl, c.FlowName, c.Method, c.ExpectedStatus,
        c.BodyMustContain, c.IntervalSeconds, c.LastRunAt, c.TimeoutMs, c.FailureThreshold,
        c.Severity, c.Enabled, c.CreatedAt, c.LighthouseEnabled, c.LighthouseIntervalSeconds,
        c.LighthouseFormFactor, c.PerfBudgetLcpMs, c.PerfBudgetTransferBytes,
        CurrentStatus: !c.Enabled ? "paused" : recentRuns.Count > 0 ? recentRuns[0].Status : "unknown",
        RecentRuns: recentRuns.Select(RunDto.From).ToList());
}

/// <summary>Body for POST /api/checks. Id, created_at, last_run_at are server/runner owned.</summary>
public class CreateCheckRequest
{
    public string? Name { get; set; }
    public string? Kind { get; set; }
    public string? TargetUrl { get; set; }
    public string? FlowName { get; set; }
    public string? Method { get; set; }
    public int? ExpectedStatus { get; set; }
    public string? BodyMustContain { get; set; }
    public int? IntervalSeconds { get; set; }
    public int? TimeoutMs { get; set; }
    public int? FailureThreshold { get; set; }
    public string? Severity { get; set; }
    public bool? Enabled { get; set; }
    public bool? LighthouseEnabled { get; set; }
    public int? LighthouseIntervalSeconds { get; set; }
    public string? LighthouseFormFactor { get; set; }
    public int? PerfBudgetLcpMs { get; set; }
    public long? PerfBudgetTransferBytes { get; set; }
}

/// <summary>Body for PATCH /api/checks/{id}. Every field optional; only present fields change.</summary>
public class UpdateCheckRequest
{
    public string? Name { get; set; }
    public string? TargetUrl { get; set; }
    public string? FlowName { get; set; }
    public string? Method { get; set; }
    public int? ExpectedStatus { get; set; }
    public string? BodyMustContain { get; set; }
    public int? IntervalSeconds { get; set; }
    public int? TimeoutMs { get; set; }
    public int? FailureThreshold { get; set; }
    public string? Severity { get; set; }
    public bool? Enabled { get; set; }
    public bool? LighthouseEnabled { get; set; }
    public int? LighthouseIntervalSeconds { get; set; }
    public string? LighthouseFormFactor { get; set; }
    public int? PerfBudgetLcpMs { get; set; }
    public long? PerfBudgetTransferBytes { get; set; }
}
