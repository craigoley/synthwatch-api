using System.Text.Json.Serialization;
using SynthWatch.Api.Data;
using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Infrastructure;

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
    // Per-monitor secret-header REFERENCES ({ headerName -> ENV_VAR_NAME }, runner 0061) for the cred-mgmt
    // UI. References only — NEVER a credential value (the value lives in env, resolved runner-side). Session-
    // gated on readback like RequestHeaders (see ChecksFunctions): null for anonymous/viewer callers.
    IReadOnlyDictionary<string, string>? SecretHeaders,
    // Network checks (dns/tcp/ping): per-kind config; null for other kinds.
    NetConfig? NetConfig,
    // Multistep API chains: ordered step list; null for non-multistep kinds.
    IReadOnlyList<ChainStep>? Steps,
    // Per-location rollup: each location's latest-run status (multi-location migration). One entry
    // per location that has run; single-location checks carry one "default" entry so the dashboard
    // grid's regional indicator is uniform. Empty only when the check has never run.
    IReadOnlyList<LocationStatusDto> Locations,
    // key:value tags (Phase 9a) joined from check_tags, so the grid/detail can show + filter by them.
    IReadOnlyList<TagDto> Tags,
    // Monitors-as-code (Phase 13): the manifest id + spec path this check was activated from (null for
    // hand-made checks). specPath being non-null is what puts the runner on the Git-fetch (Option C) path.
    string? SourceKey,
    string? SpecPath,
    // B10 redaction status (read-only visibility, June-29). sensitive/hasRedactPatterns are the DB-actual state;
    // redactionHealth derives "ok"|"misconfigured"|"n/a" so a sensitive-but-unredacted check (or "0/N redacted")
    // is detectable from the response instead of a manual DB query. See RedactionStatus.
    bool Sensitive,
    bool HasRedactPatterns,
    string RedactionHealth)
{
    public static CheckSummaryDto From(Check c, Run? latest, CheckMetricsDto m,
        IReadOnlyList<LocationStatusDto> locations, IReadOnlyList<TagDto> tags) => new(
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
        SecretHeaders: c.SecretHeaders,
        NetConfig: c.NetConfig,
        Steps: c.Steps,
        Locations: locations,
        Tags: tags,
        SourceKey: c.SourceKey,
        SpecPath: c.SpecPath,
        Sensitive: c.Sensitive,
        HasRedactPatterns: RedactionStatus.HasPatterns(c.RedactPatterns),
        RedactionHealth: RedactionStatus.Health(c.Sensitive, c.RedactPatterns));
}

/// <summary>A check's latest-run status from one location (per-location rollup for the grid).</summary>
public record LocationStatusDto(string Location, string Status)
{
    /// <summary>Status for an ASSIGNED location that has no run yet (freshly added, or never claimed) — the
    /// honest no-data state. Not a run status, not a fabricated pass; the panel renders it as awaiting data.</summary>
    public const string Pending = "pending";
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
    // Per-monitor secret-header REFERENCES for the cred-mgmt UI (runner 0061) — references only, never a
    // value; session-gated on readback like RequestHeaders (see CheckSummaryDto).
    IReadOnlyDictionary<string, string>? SecretHeaders,
    NetConfig? NetConfig,
    IReadOnlyList<ChainStep>? Steps,
    // SLO error-budget + burn rate (migration 0016). Null when the check has no slo_target (opt-in).
    SloDto? Slo,
    string CurrentStatus,
    string CurrentHealth,
    IReadOnlyList<RunDto> RecentRuns,
    // Per-location rollup for the "By location" panel. Keyed on the check's ASSIGNED locations
    // (check_locations), each LEFT JOINed to its latest run's status — NOT derived from runs history. So a
    // DROPPED location (old runs, no longer assigned) is absent, and a freshly-ADDED location with no run yet
    // is present with status "pending". The panel's "N/M failing" count uses M = assigned locations.
    IReadOnlyList<LocationStatusDto> Locations,
    // key:value tags (Phase 9a) joined from check_tags.
    IReadOnlyList<TagDto> Tags,
    // Monitors-as-code (Phase 13): the manifest id + spec path this check was activated from (null for
    // hand-made checks). Surfaced so the create response echoes the binding the runner will execute.
    string? SourceKey,
    string? SpecPath,
    // Last-known-good success-trace baseline timestamp (migration 0039). null => no baseline yet;
    // the dashboard shows "View last success trace" (-> GET /api/checks/{id}/success-trace) iff set.
    DateTimeOffset? SuccessTraceAt,
    // B10 redaction status (read-only visibility, June-29). See CheckSummaryDto + RedactionStatus.
    bool Sensitive,
    bool HasRedactPatterns,
    string RedactionHealth)
{
    public static CheckDetailDto From(Check c, IReadOnlyList<Run> recentRuns, IReadOnlyList<TagDto> tags,
        SloDto? slo = null, IReadOnlyList<LocationStatusDto>? locations = null) => new(
        c.Id, c.Name, c.Kind, c.TargetUrl, c.FlowName, c.Method, c.ExpectedStatus,
        c.BodyMustContain, c.IntervalSeconds, c.LastRunAt, c.TimeoutMs, c.FailureThreshold,
        c.Severity, c.Enabled, c.CreatedAt, c.LighthouseEnabled, c.LighthouseIntervalSeconds,
        c.LighthouseFormFactor, c.PerfBudgetLcpMs, c.PerfBudgetTransferBytes, c.CertExpiryWarnDays,
        Assertions: c.Assertions,
        RequestHeaders: c.RequestHeaders,
        RequestBody: c.RequestBody,
        Auth: c.Auth,
        SecretHeaders: c.SecretHeaders,
        NetConfig: c.NetConfig,
        Steps: c.Steps,
        Slo: slo,
        CurrentStatus: !c.Enabled ? "paused" : recentRuns.Count > 0 ? recentRuns[0].Status : "unknown",
        CurrentHealth: !c.Enabled ? RunStatus.HealthPaused
            : recentRuns.Count > 0 ? RunStatus.Classify(recentRuns[0].Status) : RunStatus.HealthUnknown,
        RecentRuns: recentRuns.Select(RunDto.From).ToList(),
        Locations: locations ?? Array.Empty<LocationStatusDto>(),
        Tags: tags,
        SourceKey: c.SourceKey,
        SpecPath: c.SpecPath,
        SuccessTraceAt: c.SuccessTraceAt,
        Sensitive: c.Sensitive,
        HasRedactPatterns: RedactionStatus.HasPatterns(c.RedactPatterns),
        RedactionHealth: RedactionStatus.Health(c.Sensitive, c.RedactPatterns));
}

/// <summary>
/// SLO error-budget + burn rate for a check (from slo_status, migration 0016). target/budget/
/// consumed/remaining/burnRate are over the 30-day SLO window. fastBurn/slowBurn are the canonical
/// Google-SRE multi-window burn alerts: fastBurn = 1h burn rate &gt;= 14.4 (page-now), slowBurn = 6h
/// burn rate &gt;= 6 (sustained). Null whole object when the check has no slo_target (opt-in).
/// </summary>
public record SloDto(
    float Target,
    decimal Budget,
    long Consumed,
    decimal Remaining,
    decimal BurnRate,
    bool FastBurn,
    bool SlowBurn);

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
    // Monitors-as-code activation (Phase 13): the manifest id + spec path to bind this check to. Both
    // optional — a hand-made check omits them. When set, spec_path makes the runner fetch+run the Git
    // spec (Option C), and source_key links the catalog row (a duplicate source_key → 409).
    public string? SourceKey { get; set; }
    public string? SpecPath { get; set; }
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
