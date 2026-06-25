namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// Keyless projection of a runner-written reconcile_drift row (monitors-as-code drift, Phase 6b /
/// runner migration 0031). The RECONCILE job (runner/reconcileMain.ts) owns this table: it reads the
/// synthwatch-monitors manifest, diffs it against live <c>checks</c> READ-ONLY, and UPSERTs the current
/// drift set (one row per (source_key, drift_type)). The API only SERVES the latest snapshot read-only —
/// no reconcile, no apply here (apply is a later runner capability; reconcile runs in report mode).
///
/// drift_type ∈ new | changed | missing | orphan (lowercase, as stored). detail is read as raw jsonb TEXT
/// (cast ::text in the query) and re-emitted verbatim by the handler, so a 'changed' row's per-field
/// before/after diff passes through unchanged.
/// </summary>
public class ReconcileDriftRow
{
    public string SourceKey { get; set; } = "";
    public string DriftType { get; set; } = "";
    public string Detail { get; set; } = "{}"; // jsonb::text — shape varies by drift_type
    public DateTimeOffset DetectedAt { get; set; }
}
