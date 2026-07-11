using System.Text.Json.Serialization;

namespace SynthWatch.Api.Dtos;

/// <summary>
/// Per-check health for a MONITORED spec (null on an unmonitored one). currentStatus is the latest run's
/// raw status (pass|warn|fail|error|running|infra_error, or null if never run) — coverage (Active/Paused)
/// is derived separately from `enabled`, so this stays the run-health dimension only.
/// </summary>
public record SpecHealthDto(
    [property: JsonPropertyName("currentStatus")] string? CurrentStatus,
    [property: JsonPropertyName("p95Ms")] double? P95Ms,
    [property: JsonPropertyName("openIncidentCount")] int OpenIncidentCount,
    [property: JsonPropertyName("lastRunAt")] DateTimeOffset? LastRunAt);

/// <summary>
/// One catalog row: a manifest spec + its coverage/runnable state (Phase 13, read-only). The dashboard
/// renders TWO orthogonal dimensions: Coverage (monitored=false → Unmonitored; monitored=true → Active if
/// enabled, Paused if not) and Runnable? (runnable / ⚠ orphan + notRunnableReason). health is null unless
/// monitored. checkId links to the live monitor (/checks/{checkId}).
/// </summary>
public record SpecCatalogItemDto(
    [property: JsonPropertyName("sourceKey")] string SourceKey,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("specPath")] string SpecPath,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("target")] string? Target,
    [property: JsonPropertyName("suggestedIntervalSeconds")] int? SuggestedIntervalSeconds,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("enabledByDefault")] bool EnabledByDefault,
    [property: JsonPropertyName("runnable")] bool Runnable,
    [property: JsonPropertyName("notRunnableReason")] string? NotRunnableReason,
    [property: JsonPropertyName("monitored")] bool Monitored,
    [property: JsonPropertyName("checkId")] long? CheckId,
    [property: JsonPropertyName("checkName")] string? CheckName,
    [property: JsonPropertyName("enabled")] bool? Enabled,
    // Reversible archive (0071): set => the check is archived. Coverage (specs/page.tsx coverageOf) shows
    // "Archived" when this is non-null, taking precedence over active/paused. null on an unmonitored spec.
    [property: JsonPropertyName("archivedAt")] DateTimeOffset? ArchivedAt,
    [property: JsonPropertyName("health")] SpecHealthDto? Health);

/// <summary>
/// GET /api/specs — the latest spec catalog (one row per manifest spec). Empty items = the reconcile job
/// hasn't populated spec_catalog yet (run reconcile). probedAt = the most recent probe time across rows
/// (when the last reconcile ran), null when empty. Read-only inventory — activation is a later PR.
/// </summary>
public record SpecCatalogDto(
    [property: JsonPropertyName("items")] IReadOnlyList<SpecCatalogItemDto> Items,
    [property: JsonPropertyName("probedAt")] DateTimeOffset? ProbedAt);
