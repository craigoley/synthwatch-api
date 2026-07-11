namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// Keyless projection of the spec catalog read: the runner-written spec_catalog snapshot (migration 0036)
/// LEFT JOINed to live checks by source_key, plus per-check health for the monitored ones (Phase 13).
/// The reconcile job owns spec_catalog (one row per manifest spec, full reload each run, incl. the
/// runnability probe); the API only SERVES this read-only — no reconcile, no writes, no GitHub fetch.
///
/// Coverage is derived downstream from (CheckId, Enabled, ArchivedAt, RemovedAt): CheckId null => Unmonitored;
/// else RemovedAt set => Removed (pending purge); ArchivedAt set => Archived; else Enabled => Active, !Enabled => Paused. Health columns
/// (CurrentStatus/P95Ms/OpenIncidentCount/LastRunAt) are NULL
/// for an unmonitored spec (no check). Tags is jsonb read as TEXT (cast ::text) and deserialized here.
/// </summary>
public class SpecCatalogRow
{
    // ── spec_catalog (manifest snapshot) ──
    public string SourceKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string SpecPath { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Target { get; set; }
    public int? SuggestedIntervalSeconds { get; set; }
    public string Tags { get; set; } = "[]"; // jsonb::text — a string[]
    public string? Description { get; set; }
    public bool EnabledByDefault { get; set; }
    public bool Runnable { get; set; }
    public string? NotRunnableReason { get; set; }
    public DateTimeOffset ProbedAt { get; set; }

    // ── coverage (LEFT JOIN checks ON source_key) ──
    public long? CheckId { get; set; }
    public string? CheckName { get; set; }
    public bool? Enabled { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }

    // ── health for monitored specs (NULL when unmonitored) ──
    public string? CurrentStatus { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public double? P95Ms { get; set; }
    public int? OpenIncidentCount { get; set; }
}
