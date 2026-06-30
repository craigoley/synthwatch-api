namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// Keyless projection of a runner-written reconcile_apply_plan row (reconcile-apply Phase 0 / runner
/// migration 0051). The RECONCILE job computes, per drift row, the DRY-RUN plan of the statement(s) apply
/// WOULD run, and persists it here. ★ Nothing is applied — this is read-only preview. The API only SERVES
/// it (no apply, no approve/reject yet — those are Phase 1).
///
/// status ∈ pending (will need human approval in Phase 1) | auto (already auto-applied, #144) | blocked
/// (a forbidden redaction-strip) | noop (orphan). plan is read as raw jsonb TEXT and re-emitted verbatim:
/// { summary, disposition, statements:[{purpose,text,values?,regions?}], blockedReason? }.
/// </summary>
public class ReconcileApplyPlanRow
{
    public string SourceKey { get; set; } = "";
    public string DriftType { get; set; } = "";
    public string Status { get; set; } = "";
    public string Plan { get; set; } = "{}"; // jsonb::text
    public DateTimeOffset ComputedAt { get; set; }
}
