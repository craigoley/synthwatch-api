namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Starts a Container App Job on-demand via ARM (the #101-fixed start: {} body + application/json) so it
/// runs right away instead of waiting for its next cron tick. Behind an interface so handlers are testable
/// without calling ARM. Returns true if ARM accepted the start; on failure it LOGS the ARM status/error and
/// returns false (callers fall back / report a clear non-2xx) rather than throwing.
/// </summary>
public interface IRunnerJobTrigger
{
    /// <summary>Start the default runner job (the "Run now" / test-send path).</summary>
    Task<bool> StartAsync(CancellationToken ct);

    /// <summary>Start an arbitrary job by name (e.g. the reconcile job) — same ARM body/auth, different target.</summary>
    Task<bool> StartAsync(string jobName, CancellationToken ct);

    /// <summary>
    /// Start a job with a PER-RUN env override on ONE container (the sandbox preview path). The override is a
    /// template override applied to this execution only; the job's CONFIGURED template (and, for the sandbox
    /// job, its DELIBERATELY-EMPTY secrets) are unchanged.
    /// ★ NON-WIDENING BY CONSTRUCTION: the caller (the API) builds <paramref name="env"/> server-side from
    /// NON-SECRET data only (the base64 spec + the target + the result token). An uploaded spec is a STRING in
    /// ONE value — it cannot add env keys, and it cannot introduce a secret the API doesn't have. The sandbox
    /// still runs the spec in a re-allowlisted child (runner/sandbox), so this override can only carry data.
    /// </summary>
    Task<bool> StartWithEnvOverrideAsync(string jobName, string containerName, IReadOnlyDictionary<string, string> env, CancellationToken ct);
}
