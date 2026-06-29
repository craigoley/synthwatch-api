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
}
