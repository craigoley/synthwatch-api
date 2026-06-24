namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Starts the runner Container App Job on-demand so it drains a freshly-inserted test_send_request
/// right away (instead of waiting for the next cron tick). Behind an interface so the test-send handler
/// is testable without calling ARM. <see cref="StartAsync"/> returns true if ARM accepted the start
/// request; on failure it returns false (the handler still returns the requestId — a cron tick drains
/// the pending row as a fallback) rather than throwing.
/// </summary>
public interface IRunnerJobTrigger
{
    Task<bool> StartAsync(CancellationToken ct);
}
