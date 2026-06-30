using System.Text.Json;
using System.Text.Json.Serialization;

namespace SynthWatch.Api.Dtos;

/// <summary>
/// One reconcile_drift row served read-only (Phase 6b). driftType is the stored lowercase value
/// (new|changed|missing|orphan); detail is the runner-written jsonb passed through verbatim (its shape
/// varies by driftType — e.g. a 'changed' row carries { fields: { name: { git, live }, … } }). The
/// dashboard groups + labels these (resolvable config drift vs the known orphan gap).
/// </summary>
/// <summary>
/// One reconcile_apply_plan row served read-only (reconcile-apply Phase 0, dry-run). status ∈
/// pending (needs Phase-1 human approval) | auto (already auto-applied, #144) | blocked (a forbidden
/// redaction-strip) | noop (orphan). plan is the runner-written jsonb passed through verbatim:
/// { summary, disposition, statements:[{purpose,text,values?,regions?}], blockedReason? }.
/// ★ Read-only preview — the API never applies and (this phase) never approves/rejects.
/// </summary>
public record ReconcileApplyPlanItemDto(
    [property: JsonPropertyName("sourceKey")] string SourceKey,
    [property: JsonPropertyName("driftType")] string DriftType,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("plan")] JsonElement Plan,
    [property: JsonPropertyName("computedAt")] DateTimeOffset ComputedAt);

/// <summary>GET /api/reconcile/plan — the dry-run apply plan per drift row. items empty = no plan yet.</summary>
public record ReconcileApplyPlanDto(
    [property: JsonPropertyName("items")] IReadOnlyList<ReconcileApplyPlanItemDto> Items,
    [property: JsonPropertyName("computedAt")] DateTimeOffset? ComputedAt);

/// <summary>Body for POST /api/reconcile/approve | /reject — identifies the plan row (Phase 1).</summary>
public record ReconcileDecisionRequest(
    [property: JsonPropertyName("sourceKey")] string? SourceKey,
    [property: JsonPropertyName("driftType")] string? DriftType);

/// <summary>202 body for POST /api/reconcile/apply — which approved plans were applied / left for retry.</summary>
public record ReconcileApplyResultDto(
    [property: JsonPropertyName("applied")] IReadOnlyList<string> Applied,
    [property: JsonPropertyName("failed")] IReadOnlyList<string> Failed,
    [property: JsonPropertyName("cap")] int Cap);

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

/// <summary>
/// 202 body for POST /api/reconcile/trigger — the reconcile ACA job was ARM-started (fire-and-forget; the job
/// runs async). triggered is always true here (a failed start returns a non-2xx, not this).
/// </summary>
public record ReconcileTriggeredDto(
    [property: JsonPropertyName("triggered")] bool Triggered);
