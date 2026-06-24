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

    public async Task<bool> StartAsync(CancellationToken ct)
    {
        try
        {
            var token = await _credential.GetTokenAsync(
                new TokenRequestContext(new[] { _options.TokenScope }), ct);

            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15); // bounded: a slow ARM must not stall the request

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.StartUrl);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
            // EMPTY body — preserves the job's secretRefs (no template/env override).
            request.Content = new StringContent(string.Empty);

            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return true;

            var detail = await response.Content.ReadAsStringAsync(ct);
            RunnerJobLog.StartNonSuccess(_logger, (int)response.StatusCode, _options.JobName, detail);
            return false;
        }
        catch (Exception ex)
        {
            // Never throw ARM failures into the request — the pending row is a cron-tick fallback.
            RunnerJobLog.StartFailed(_logger, _options.JobName, ex);
            return false;
        }
    }
}

/// <summary>High-performance (CA1848) log delegates for the runner job-start trigger.</summary>
internal static partial class RunnerJobLog
{
    [LoggerMessage(EventId = 5000, Level = LogLevel.Warning,
        Message = "Runner job start returned {Status} for {Job}: {Detail}")]
    public static partial void StartNonSuccess(ILogger logger, int status, string job, string detail);

    [LoggerMessage(EventId = 5001, Level = LogLevel.Warning,
        Message = "Runner job start failed for {Job}")]
    public static partial void StartFailed(ILogger logger, string job, Exception ex);
}
