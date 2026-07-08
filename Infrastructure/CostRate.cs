using System.Globalization;

namespace SynthWatch.Api.Infrastructure;

/// <summary>The cost RATE — a CONFIG tunable (Function App env vars / app settings), changeable WITHOUT a
/// code deploy (mirrors the AUTH_ENFORCEMENT_ENABLED / AZURE_OPENAI_* env-var pattern). Default $0.00003
/// per vCPU-second (ACA Consumption). cpu=1.0 vCPU / mem=2 GiB are infra-anchored (main.bicep:528-529), so
/// the per-run-second rate already folds them in — reproduces #229's ~$67/mo. GET /reports/cost ECHOES all
/// three (rateUsed/rateSource/rateSetDate) so every figure is self-describing: an ESTIMATE, not billed truth.</summary>
public static class CostRate
{
    public const decimal DefaultPerVcpuSecond = 0.00003m;
    public const string DefaultSource = "ACA Consumption vCPU-second (cpu=1.0 vCPU / mem=2 GiB, main.bicep:528-529)";
    public const string DefaultSetDate = "2026-07-08"; // recon #220/#229; update the env var + this date when the rate changes

    /// <summary>$ per vCPU-second. Reads COST_RATE_PER_VCPU_SECOND; falls back to the documented default when
    /// unset/invalid/&lt;=0 (fail-safe — the endpoint never divides by or serves a nonsense rate).</summary>
    public static decimal PerVcpuSecond =>
        decimal.TryParse(Environment.GetEnvironmentVariable("COST_RATE_PER_VCPU_SECOND"),
            NumberStyles.Any, CultureInfo.InvariantCulture, out var r) && r > 0m
            ? r : DefaultPerVcpuSecond;

    public static string Source => Environment.GetEnvironmentVariable("COST_RATE_SOURCE") ?? DefaultSource;
    public static string SetDate => Environment.GetEnvironmentVariable("COST_RATE_SET_DATE") ?? DefaultSetDate;
}
