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

    /// <summary>ARM api-version for the Microsoft.App/jobs start action.</summary>
    public string ApiVersion { get; set; } = "2024-03-01";

    /// <summary>ARM management endpoint (token scope is "{endpoint}/.default").</summary>
    public string ManagementEndpoint { get; set; } = "https://management.azure.com";

    /// <summary>
    /// POST URL for the job-start action (EMPTY body): no env override, so the job's secretRefs are
    /// preserved — it picks up the pending test_send_requests row itself.
    /// </summary>
    public string StartUrl =>
        $"{ManagementEndpoint.TrimEnd('/')}/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroup}" +
        $"/providers/Microsoft.App/jobs/{JobName}/start?api-version={ApiVersion}";

    /// <summary>OAuth scope for the ARM bearer token.</summary>
    public string TokenScope => $"{ManagementEndpoint.TrimEnd('/')}/.default";
}
