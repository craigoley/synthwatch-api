namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Coordinates for the runner Container App Job that the test-send path starts on-demand via ARM
/// (Microsoft.App/jobs/{name}/start). Bound from the "RunnerJob" config section (env: RunnerJob__*),
/// with the live-deployment values as defaults so the app works without extra config. The defaults are
/// non-secret resource identifiers (subscription/rg/job-name) — overridable per environment, NOT magic.
/// </summary>
public class RunnerJobOptions
{
    public string SubscriptionId { get; set; } = "505a01eb-9a5f-4212-a293-ff82a68d0d03";
    public string ResourceGroup { get; set; } = "synthwatch-rg";
    public string JobName { get; set; } = "synthwatch-runner-job";

    /// <summary>The monitors-as-code reconcile ACA job, started on-demand by POST /api/reconcile/trigger.
    /// Config-sourced (RunnerJob__ReconcileJobName) like <see cref="JobName"/>, not a literal in the handler.</summary>
    public string ReconcileJobName { get; set; } = "synthwatch-reconcile-job";

    /// <summary>The low-privilege sandbox preview job (infra/main.bicep) — a SEPARATE identity + secret-free env.
    /// Config-sourced (RunnerJob__SandboxJobName). The API starts it with a per-run env override carrying the
    /// spec as data.</summary>
    public string SandboxJobName { get; set; } = "synthwatch-sandbox";

    /// <summary>The sandbox job's container name — the target of the per-run env override (must match the bicep
    /// container name). Config-sourced (RunnerJob__SandboxContainerName).</summary>
    public string SandboxContainerName { get; set; } = "sandbox";

    /// <summary>ARM api-version for the Microsoft.App/jobs start action.</summary>
    public string ApiVersion { get; set; } = "2024-03-01";

    /// <summary>ARM management endpoint (token scope is "{endpoint}/.default").</summary>
    public string ManagementEndpoint { get; set; } = "https://management.azure.com";

    /// <summary>POST URL for the job-start action of a named job (EMPTY body preserves the job's secretRefs).</summary>
    public string StartUrlFor(string jobName) =>
        $"{ManagementEndpoint.TrimEnd('/')}/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroup}" +
        $"/providers/Microsoft.App/jobs/{jobName}/start?api-version={ApiVersion}";

    /// <summary>GET URL for a named job's resource — read the configured template so an env override can carry
    /// the container's image/command/args/resources (a jobs/start override REPLACES the container wholesale).</summary>
    public string JobUrlFor(string jobName) =>
        $"{ManagementEndpoint.TrimEnd('/')}/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroup}" +
        $"/providers/Microsoft.App/jobs/{jobName}?api-version={ApiVersion}";

    /// <summary>POST URL for the default runner job's start action (the "Run now" / test-send path).</summary>
    public string StartUrl => StartUrlFor(JobName);

    /// <summary>OAuth scope for the ARM bearer token.</summary>
    public string TokenScope => $"{ManagementEndpoint.TrimEnd('/')}/.default";
}
