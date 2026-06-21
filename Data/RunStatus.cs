namespace SynthWatch.Api.Data;

/// <summary>
/// The API's committed copy of the runner-owned run/step status taxonomy — verified against
/// the LIVE CHECK constraints on 2026-06-21:
/// <list type="bullet">
///   <item><c>runs_status_check</c>: <c>pass, warn, fail, error, running</c></item>
///   <item><c>run_steps_status_check</c>: <c>pass, fail, error</c></item>
/// </list>
/// The runner widened these from the original <c>(pass, fail)</c>. Status is stored/served as a
/// free string (no DB enum), so new values pass through EF unchanged; this type exists to keep
/// the contract explicit and to classify health in ONE place.
///
/// Health classification mirrors the <c>sla_availability()</c> function exactly:
/// <c>up = (pass, warn)</c>, <c>down = (fail, error)</c>, <c>running</c> is in-flight and excluded
/// from completed runs. Do not let <c>warn</c> count as down or <c>error</c> be dropped.
/// </summary>
public static class RunStatus
{
    public const string Pass = "pass";
    public const string Warn = "warn";
    public const string Fail = "fail";
    public const string Error = "error";
    public const string Running = "running";

    // Health buckets. up/down/running match sla_availability(); the others are API-only states.
    public const string HealthUp = "up";
    public const string HealthDown = "down";
    public const string HealthRunning = "running";
    public const string HealthPaused = "paused";
    public const string HealthUnknown = "unknown";

    /// <summary>
    /// Classify a raw run status into a health bucket: <c>up</c> (pass|warn), <c>down</c>
    /// (fail|error), <c>running</c> (in-flight), or <c>unknown</c> (null / unrecognized).
    /// </summary>
    public static string Classify(string? status) => status switch
    {
        Pass or Warn => HealthUp,
        Fail or Error => HealthDown,
        Running => HealthRunning,
        _ => HealthUnknown,
    };
}
