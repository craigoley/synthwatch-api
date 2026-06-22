using System.Text.Json.Serialization;
using SynthWatch.Api.Data;
using SynthWatch.Api.Data.Entities;

namespace SynthWatch.Api.Dtos;

/// <summary>
/// One point in a check's recent-run sparkline. Field names (<c>t</c>/<c>d</c>/<c>s</c>) match
/// the dashboard's <c>SparkPoint</c> shape exactly (ported from the old route's json_agg).
/// </summary>
public record SparkPoint(
    [property: JsonPropertyName("t")] DateTimeOffset T,   // started_at (ISO)
    [property: JsonPropertyName("d")] int? D,             // duration_ms
    [property: JsonPropertyName("s")] string S);          // status

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
    // Derived. CurrentStatus is the latest run's raw status (pass|warn|fail|error|running, or
    // paused/unknown); CurrentHealth is that status classified into up|down|running (matching
    // sla_availability()), plus paused/unknown — so consumers don't re-derive up/down.
    string CurrentStatus,
    string CurrentHealth,
    long? LastRunId,
    int? LastDurationMs,
    int? LastHttpStatus,
    bool HasOpenIncident,
    // Dashboard-parity fields (ported from the old TS route's lateral-join SQL). 24h latency
    // percentiles over completed runs, the 24h completed-run count, a recent-run sparkline, and
    // open-incident rollup (count + highest severity for pill coloring).
    double? P50Ms,
    double? P95Ms,
    int Runs24h,
    IReadOnlyList<SparkPoint> Spark,
    int OpenIncidentCount,
    string? MaxOpenSeverity,
    // SSL: the warn threshold config (always present) and the latest run's measured
    // days-remaining (null for non-ssl checks or when there is no latest run).
    int CertExpiryWarnDays,
    int? LastCertDaysRemaining,
    // No-code assertion model + request config. auth is references-only (type + *_env names);
    // write validation forbids inline credential values, so nothing secret is ever stored/echoed.
    IReadOnlyList<Assertion> Assertions,
    IReadOnlyDictionary<string, string>? RequestHeaders,
    string? RequestBody,
    IReadOnlyDictionary<string, string>? Auth,
    // Network checks (dns/tcp/ping): per-kind config; null for other kinds.
    NetConfig? NetConfig,
    // Multistep API chains: ordered step list; null for non-multistep kinds.
    IReadOnlyList<ChainStep>? Steps)
{
    public static CheckSummaryDto From(Check c, Run? latest, CheckMetricsDto m) => new(
        c.Id, c.Name, c.Kind, c.TargetUrl, c.FlowName, c.Method, c.ExpectedStatus,
        c.IntervalSeconds, c.TimeoutMs, c.FailureThreshold, c.Severity, c.Enabled,
        c.LighthouseEnabled, c.LastRunAt, c.CreatedAt,
        CurrentStatus: !c.Enabled ? "paused" : latest?.Status ?? "unknown",
        CurrentHealth: !c.Enabled ? RunStatus.HealthPaused
            : latest is null ? RunStatus.HealthUnknown : RunStatus.Classify(latest.Status),
        LastRunId: latest?.Id,
        LastDurationMs: latest?.DurationMs,
        LastHttpStatus: latest?.HttpStatus,
        HasOpenIncident: m.OpenIncidentCount > 0,
        P50Ms: m.P50Ms,
        P95Ms: m.P95Ms,
        Runs24h: m.Runs24h,
        Spark: m.Spark,
        OpenIncidentCount: m.OpenIncidentCount,
        MaxOpenSeverity: m.MaxOpenSeverity,
        CertExpiryWarnDays: c.CertExpiryWarnDays,
        LastCertDaysRemaining: latest?.CertDaysRemaining,
        Assertions: c.Assertions,
        RequestHeaders: c.RequestHeaders,
        RequestBody: c.RequestBody,
        Auth: c.Auth,
        NetConfig: c.NetConfig,
        Steps: c.Steps);
}

/// <summary>Per-check computed metrics (ported SQL), merged into the check summary by id.</summary>
public record CheckMetricsDto(
    double? P50Ms,
    double? P95Ms,
    int Runs24h,
    int OpenIncidentCount,
    string? MaxOpenSeverity,
    IReadOnlyList<SparkPoint> Spark)
{
    public static readonly CheckMetricsDto Empty =
        new(null, null, 0, 0, null, Array.Empty<SparkPoint>());
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
    int CertExpiryWarnDays,
    // No-code assertion model + request config (auth is references-only, see CheckSummaryDto).
    IReadOnlyList<Assertion> Assertions,
    IReadOnlyDictionary<string, string>? RequestHeaders,
    string? RequestBody,
    IReadOnlyDictionary<string, string>? Auth,
    NetConfig? NetConfig,
    IReadOnlyList<ChainStep>? Steps,
    string CurrentStatus,
    string CurrentHealth,
    IReadOnlyList<RunDto> RecentRuns)
{
    public static CheckDetailDto From(Check c, IReadOnlyList<Run> recentRuns) => new(
        c.Id, c.Name, c.Kind, c.TargetUrl, c.FlowName, c.Method, c.ExpectedStatus,
        c.BodyMustContain, c.IntervalSeconds, c.LastRunAt, c.TimeoutMs, c.FailureThreshold,
        c.Severity, c.Enabled, c.CreatedAt, c.LighthouseEnabled, c.LighthouseIntervalSeconds,
        c.LighthouseFormFactor, c.PerfBudgetLcpMs, c.PerfBudgetTransferBytes, c.CertExpiryWarnDays,
        Assertions: c.Assertions,
        RequestHeaders: c.RequestHeaders,
        RequestBody: c.RequestBody,
        Auth: c.Auth,
        NetConfig: c.NetConfig,
        Steps: c.Steps,
        CurrentStatus: !c.Enabled ? "paused" : recentRuns.Count > 0 ? recentRuns[0].Status : "unknown",
        CurrentHealth: !c.Enabled ? RunStatus.HealthPaused
            : recentRuns.Count > 0 ? RunStatus.Classify(recentRuns[0].Status) : RunStatus.HealthUnknown,
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
    public int? CertExpiryWarnDays { get; set; }
    public List<Assertion>? Assertions { get; set; }
    public Dictionary<string, string>? RequestHeaders { get; set; }
    public string? RequestBody { get; set; }
    public Dictionary<string, string>? Auth { get; set; }
    public NetConfig? NetConfig { get; set; }
    public List<ChainStep>? Steps { get; set; }
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
    public int? CertExpiryWarnDays { get; set; }
    public List<Assertion>? Assertions { get; set; }
    public Dictionary<string, string>? RequestHeaders { get; set; }
    public string? RequestBody { get; set; }
    public Dictionary<string, string>? Auth { get; set; }
    public NetConfig? NetConfig { get; set; }
    public List<ChainStep>? Steps { get; set; }
}
