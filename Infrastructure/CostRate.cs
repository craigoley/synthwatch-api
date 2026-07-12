using System.Globalization;

namespace SynthWatch.Api.Infrastructure;

/// <summary>The cost RATE — DERIVED from the two ACA Consumption ACTIVE-usage meters and the LIVE runner
/// allocation, mirroring the runner's costModel.ts so /reports/cost and the narrative fact pack use ONE rate
/// model. ★ The old single scalar COST_RATE_PER_VCPU_SECOND = 0.00003 was NEVER a vCPU rate: it was the
/// 1.0 vCPU / 2 GiB BLEND (1×0.000024 + 2×0.000003) with memory folded in and the name hiding it — so after
/// the runner resize to 2.0 vCPU / 4 GiB it under-priced every second by EXACTLY 2×. The rate is now
/// cpu×0.000024 + mem×0.000003 against the deploy-stamped allocation (SYNTHWATCH_RUNNER_CPU/MEMORY_GIB), so a
/// resize re-prices automatically. An explicit COST_RATE_PER_ACTIVE_SECOND still allows a deploy-free
/// override. (A monthly free grant of 180,000 vCPU-s + 360,000 GiB-s per subscription applies before billing;
/// not attributed per-check, so these are GROSS active-usage estimates. GET /reports/cost echoes the rate.)</summary>
public static class CostRate
{
    // ACA Consumption ACTIVE-usage meters ($/second), verified 2026-07 against the ACA pricing page.
    public const decimal VcpuSecondRate = 0.000024m;
    public const decimal GibSecondRate = 0.000003m;

    // The runner (browser) allocation being priced — deploy-stamped into the Function App env (mirrors the
    // runner jobs' SYNTHWATCH_RUNNER_CPU/MEMORY_GIB). Fallback = the CURRENT shape, only reached when unset.
    public const decimal DefaultRunnerCpu = 2.0m;
    public const decimal DefaultRunnerMemoryGib = 4m;
    public const string DefaultSetDate = "2026-07-12"; // update alongside the meters/allocation when they change

    private static decimal EnvDecimalOrDefault(string name, decimal fallback) =>
        decimal.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) && v > 0m
            ? v : fallback;

    /// <summary>Live runner cpu / memory (GiB) from the deploy-stamped env, falling back to the current shape.</summary>
    public static decimal RunnerCpu => EnvDecimalOrDefault("SYNTHWATCH_RUNNER_CPU", DefaultRunnerCpu);
    public static decimal RunnerMemoryGib => EnvDecimalOrDefault("SYNTHWATCH_RUNNER_MEMORY_GIB", DefaultRunnerMemoryGib);

    /// <summary>The $/active-second derivation for a container allocation — the two meters blended.</summary>
    public static decimal Blend(decimal cpu, decimal memoryGib) => cpu * VcpuSecondRate + memoryGib * GibSecondRate;

    /// <summary>The fallback rate when nothing is stamped/overridden — the current shape's blend (2.0/4 → 0.00006,
    /// exactly 2× the old 1.0/2 → 0.00003 scalar). Exposed for tests / provenance.</summary>
    public static decimal DefaultPerActiveSecond => Blend(DefaultRunnerCpu, DefaultRunnerMemoryGib);

    /// <summary>$ per ACTIVE second. An explicit COST_RATE_PER_ACTIVE_SECOND override wins (deploy-free
    /// re-price); otherwise DERIVE from the live stamped allocation. Never serves a nonsense (&lt;=0) rate.</summary>
    public static decimal PerActiveSecond =>
        decimal.TryParse(Environment.GetEnvironmentVariable("COST_RATE_PER_ACTIVE_SECOND"),
            NumberStyles.Any, CultureInfo.InvariantCulture, out var r) && r > 0m
            ? r : Blend(RunnerCpu, RunnerMemoryGib);

    public static string Source =>
        Environment.GetEnvironmentVariable("COST_RATE_SOURCE")
        ?? $"ACA Consumption active meters: {RunnerCpu} vCPU × {VcpuSecondRate} + {RunnerMemoryGib} GiB × {GibSecondRate} " +
           $"= {PerActiveSecond} $/active-second (derived from the live allocation)";
    public static string SetDate => Environment.GetEnvironmentVariable("COST_RATE_SET_DATE") ?? DefaultSetDate;
}
