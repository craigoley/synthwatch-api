namespace SynthWatch.Api.Data.Entities;

/// <summary>Keyless projection for GET /api/reports/cost — one row per ENABLED check from the SHARED cost
/// model <c>cost_projection(rate)</c> (runner migration 0069). The $ math (projected/measured/divergence) now
/// lives ONLY in that SQL function, called with the CONFIG rate — so /reports/cost and the runner narrative
/// fact pack are byte-identical by construction (no second C# copy of the formula). Projected/Measured are
/// rounded 2dp for display; ProjectedRaw/MeasuredRaw are unrounded (sum these for the fleet total, THEN round
/// — no per-check rounding drift). ★ Pre-prod INCLUDED — a staging monitor is real vCPU spend.</summary>
public class CostReportRow
{
    public long CheckId { get; set; }
    public string? SourceKey { get; set; }        // null for hand-made (non-manifest) checks
    public string CheckName { get; set; } = "";
    public string Kind { get; set; } = "";
    public int IntervalSeconds { get; set; }
    public int RegionCount { get; set; }           // count of assigned check_locations
    public double? AvgDurationS { get; set; }       // avg(duration_ms)/1000 over last 7d; null = no runs in window
    // ★ 0089 — the HONEST per-monitor metric. ActiveSeconds = Σ measured active-seconds over 7d (the
    // attributable compute); ActiveSecondsPct = this check's share of FLEET active-seconds (null when no
    // monitor ran → never a fake 0%). A share cancels the systematic error the from-zero $ carried (the
    // per-subscription free grant + non-ACA line items), so it is the ranking signal Projected/Measured were
    // pretending to be. ★ SOURCE CAVEAT: active-seconds is the check's own duration_ms (runFinalize.ts), NOT
    // the job-execution billed wall-clock — so the share slightly under-weights heavy-cold-start monitors.
    public decimal ActiveSeconds { get; set; }      // 0089: active_seconds_7d — Σ duration_ms/1000 over 7d
    public decimal? ActiveSecondsPct { get; set; }  // 0089: compute_share_pct — % of fleet; null when fleet total is 0
    public decimal Projected { get; set; }          // rounded 2dp (display)
    public decimal Measured { get; set; }           // rounded 2dp (display)
    public decimal? Divergence { get; set; }        // rounded 3dp; null when projected = 0
    public bool DivergenceFlag { get; set; }        // divergence > 1.5
    public decimal ProjectedRaw { get; set; }       // unrounded — sum for the fleet total
    public decimal MeasuredRaw { get; set; }
    // 0078 — run-count columns for HONEST divergence attribution. divergence = RunCount7d / expected is a
    // pure run-count ratio (duration cancels), so a flag is a config-change straddle / confirmation / sandbox,
    // NEVER retries. RunCountRecent/Prior split the 7d window at 3.5d (a cadence step ⇒ an interval change).
    public int RunCount7d { get; set; }             // runs (duration_ms NOT NULL) in the last 7d = N in divergence=N/expected
    public int ConfirmationCount7d { get; set; }    // of those, confirmation re-runs (0077)
    public int SandboxCount7d { get; set; }         // of those, sandbox / on-demand fires (0065)
    public int RunCountRecent { get; set; }         // runs in the recent half (last 3.5d)
    public int RunCountPrior { get; set; }          // runs in the prior half (3.5–7d ago)
    // ★ 0091 — the PRIMARY per-monitor $, restored: free-grant-aware, Σ = the reconcile anchor. Allocates the
    // grant-corrected fleet total by compute share (grant spread proportionally — a cheap check is non-zero,
    // never $0). NULL when the monitor had no runs (never a fake $0). FleetBillableMonthly is the anchor when
    // the reconcile target is unset (grant-corrected fleet), CONSTANT per row — for the drift check vs Azure.
    public decimal? EstimatedMonthly { get; set; }       // 0091: estimated_monthly
    public decimal FleetBillableMonthly { get; set; }    // 0091: fleet_billable_monthly (grant-corrected fleet total)
}
