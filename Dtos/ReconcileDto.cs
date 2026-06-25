using System.Text.Json;
using System.Text.Json.Serialization;

namespace SynthWatch.Api.Dtos;

/// <summary>
/// One reconcile_drift row served read-only (Phase 6b). driftType is the stored lowercase value
/// (new|changed|missing|orphan); detail is the runner-written jsonb passed through verbatim (its shape
/// varies by driftType — e.g. a 'changed' row carries { fields: { name: { git, live }, … } }). The
/// dashboard groups + labels these (resolvable config drift vs the known orphan gap).
/// </summary>
public record ReconcileDriftItemDto(
    [property: JsonPropertyName("sourceKey")] string SourceKey,
    [property: JsonPropertyName("driftType")] string DriftType,
    [property: JsonPropertyName("detail")] JsonElement Detail,
    [property: JsonPropertyName("detectedAt")] DateTimeOffset DetectedAt);

/// <summary>
/// GET /api/reconcile/drift — the latest reconcile snapshot. items is the full current drift set
/// (empty = the live monitors are in sync with Git). detectedAt is the most recent detected_at across
/// the rows (when the last reconcile ran), null when there's no drift. The API never applies anything —
/// reconcile runs in report mode; apply is a later runner capability.
/// </summary>
public record ReconcileDriftDto(
    [property: JsonPropertyName("items")] IReadOnlyList<ReconcileDriftItemDto> Items,
    [property: JsonPropertyName("detectedAt")] DateTimeOffset? DetectedAt);
