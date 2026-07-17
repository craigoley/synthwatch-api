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

    public Task<bool> StartWithEnvOverrideAsync(
        string jobName,
        string containerName,
        IReadOnlyDictionary<string, string> env,
        CancellationToken ct)
    {
        // ★ A template override for THIS execution only — override ONE container's env with the caller's
        //   NON-SECRET data (the sandbox job carries no secrets, so nothing sensitive is merged/replaced). The
        //   uploaded spec is a STRING in one value; it cannot add keys or smuggle a secret the API doesn't hold.
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            template = new
            {
                containers = new[]
                {
                    new
                    {
                        name = containerName,
                        env = env.Select(kv => new { name = kv.Key, value = kv.Value }).ToArray(),
                    },
                },
            },
        });
        return PostStartAsync(jobName, body, ct);
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
