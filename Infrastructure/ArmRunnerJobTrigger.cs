using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Starts the runner Container App Job via the ARM REST API: a bearer token from the app's managed
/// identity (the SAME <see cref="TokenCredential"/> the DB token uses) for the ARM scope, then a POST to
/// Microsoft.App/jobs/{name}/start with an EMPTY body. Empty body = no env override, so the job keeps its
/// configured secretRefs and drains the pending test_send_requests row through the real dispatch path.
/// A non-2xx / transient failure is logged and reported as false (the handler still returns the
/// requestId; a cron tick drains the row as a fallback) — it never throws ARM noise into the request.
/// </summary>
public sealed class ArmRunnerJobTrigger : IRunnerJobTrigger
{
    private readonly TokenCredential _credential;
    private readonly IHttpClientFactory _httpFactory;
    private readonly RunnerJobOptions _options;
    private readonly ILogger<ArmRunnerJobTrigger> _logger;

    public ArmRunnerJobTrigger(
        TokenCredential credential,
        IHttpClientFactory httpFactory,
        IOptions<RunnerJobOptions> options,
        ILogger<ArmRunnerJobTrigger> logger)
    {
        _credential = credential;
        _httpFactory = httpFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Start the default runner job (the "Run now" / test-send path) — unchanged behaviour.</summary>
    public Task<bool> StartAsync(CancellationToken ct) => StartAsync(_options.JobName, ct);

    // ★ {} = no template/env override, so the job keeps its configured secretRefs (the "Run now"/test-send path).
    public Task<bool> StartAsync(string jobName, CancellationToken ct) => PostStartAsync(jobName, "{}", ct);

    public async Task<bool> StartWithEnvOverrideAsync(
        string jobName,
        string containerName,
        IReadOnlyDictionary<string, string> env,
        CancellationToken ct)
    {
        // ★ jobs/start takes a JobExecutionTemplate at the TOP LEVEL (no `template` wrapper — that 400s with
        //   "Unknown properties template in StartJobExecutionTemplate"), and an overridden container REPLACES the
        //   job's container WHOLESALE. So a bare {name,env} 400s ("Container ... must have an 'Image' property")
        //   and, even with the image, silently runs the image's default entrypoint (the MAIN runner) when it
        //   drops command/args. We therefore GET the job's configured container and MERGE our NON-SECRET env over
        //   its env, keeping image/command/args/resources. The sandbox job carries no secrets, so nothing
        //   sensitive is merged/replaced; the uploaded spec is one string value and cannot add env keys.
        try
        {
            var token = await _credential.GetTokenAsync(
                new TokenRequestContext(new[] { _options.TokenScope }), ct);
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);

            using var getReq = new HttpRequestMessage(HttpMethod.Get, _options.JobUrlFor(jobName));
            getReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            using var getResp = await http.SendAsync(getReq, ct);
            if (!getResp.IsSuccessStatusCode)
            {
                RunnerJobLog.StartNonSuccess(_logger, (int)getResp.StatusCode, jobName, await getResp.Content.ReadAsStringAsync(ct));
                return false;
            }

            var jobJson = await getResp.Content.ReadAsStringAsync(ct);
            var body = BuildStartBody(jobJson, containerName, env);
            if (body is null)
            {
                RunnerJobLog.StartFailed(_logger, jobName,
                    new InvalidOperationException($"container '{containerName}' not found in job '{jobName}' template"));
                return false;
            }
            return await PostStartAsync(jobName, body, ct);
        }
        catch (Exception ex)
        {
            RunnerJobLog.StartFailed(_logger, jobName, ex);
            return false;
        }
    }

    /// <summary>Build the jobs/start body from the GET'd job JSON: the named container's image/command/args/
    /// resources preserved, its env merged with <paramref name="overrideEnv"/> (override/add by name, existing
    /// entries — including any secretRef — otherwise preserved). Returns null if the container isn't found.
    /// Static + pure so the merge is unit-testable without ARM.</summary>
    public static string? BuildStartBody(string jobJson, string containerName, IReadOnlyDictionary<string, string> overrideEnv)
    {
        var containers = JsonNode.Parse(jobJson)?["properties"]?["template"]?["containers"]?.AsArray();
        if (containers is null) return null;

        JsonNode? source = null;
        foreach (var c in containers)
        {
            if (c is not null && (string?)c["name"] == containerName) { source = c; break; }
        }
        if (source is null) return null;

        // Preserve existing env nodes (incl. secretRef), then override/add our keys by name.
        var envArr = source["env"]?.DeepClone().AsArray() ?? new JsonArray();
        foreach (var kv in overrideEnv)
        {
            for (var i = envArr.Count - 1; i >= 0; i--)
            {
                if (envArr[i] is not null && (string?)envArr[i]!["name"] == kv.Key) envArr.RemoveAt(i);
            }
            envArr.Add(new JsonObject { ["name"] = kv.Key, ["value"] = kv.Value });
        }

        // Minimal container ARM's start template accepts (matches the validated shape) — omit null fields.
        var container = new JsonObject
        {
            ["name"] = containerName,
            ["image"] = (string?)source["image"],
            ["env"] = envArr,
        };
        if (source["command"] is JsonNode cmd) container["command"] = cmd.DeepClone();
        if (source["args"] is JsonNode args) container["args"] = args.DeepClone();
        if (source["resources"] is JsonNode res) container["resources"] = res.DeepClone();

        return new JsonObject { ["containers"] = new JsonArray(container) }.ToJsonString();
    }

    private async Task<bool> PostStartAsync(string jobName, string jsonBody, CancellationToken ct)
    {
        try
        {
            var token = await _credential.GetTokenAsync(
                new TokenRequestContext(new[] { _options.TokenScope }), ct);

            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15); // bounded: a slow ARM must not stall the request

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.StartUrlFor(jobName));
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
            // ★ ARM's Microsoft.App/jobs/start REQUIRES application/json — an empty text/plain body returns 415
            // (DIAGNOSED in prod: the {} application/json call fires an immediate off-schedule execution).
            request.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");

            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                RunnerJobLog.Started(_logger, jobName, (int)response.StatusCode);
                return true;
            }

            var detail = await response.Content.ReadAsStringAsync(ct);
            RunnerJobLog.StartNonSuccess(_logger, (int)response.StatusCode, jobName, detail);
            return false;
        }
        catch (Exception ex)
        {
            // Never throw ARM failures into the request — the caller reports a clear non-2xx.
            RunnerJobLog.StartFailed(_logger, jobName, ex);
            return false;
        }
    }
}

/// <summary>High-performance (CA1848) log delegates for the runner job-start trigger.</summary>
internal static partial class RunnerJobLog
{
    [LoggerMessage(EventId = 5002, Level = LogLevel.Information,
        Message = "Runner job start accepted ({Status}) for {Job} — an off-schedule execution should fire now")]
    public static partial void Started(ILogger logger, string job, int status);

    [LoggerMessage(EventId = 5000, Level = LogLevel.Warning,
        Message = "Runner job start returned {Status} for {Job}: {Detail}")]
    public static partial void StartNonSuccess(ILogger logger, int status, string job, string detail);

    [LoggerMessage(EventId = 5001, Level = LogLevel.Warning,
        Message = "Runner job start failed for {Job}")]
    public static partial void StartFailed(ILogger logger, string job, Exception ex);
}
