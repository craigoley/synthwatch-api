using System.Runtime.CompilerServices;

namespace SynthWatch.Api.Tests;

/// <summary>
/// Pins the suite's enforcement baseline. Production is FAIL-CLOSED (enforcement ON unless
/// AUTH_ENFORCEMENT_ENABLED is explicitly "false"/"0"), but the tests exercise handlers directly and
/// predate the gate — they assert content, not denials. Setting the variable explicitly here keeps the
/// pre-fail-closed baseline for the whole suite; tests that exercise enforcement (the forensic-artifact
/// gate, the parse matrix) set/restore the variable themselves on top of this.
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    public static void PinEnforcementBaseline()
    {
        if (Environment.GetEnvironmentVariable("AUTH_ENFORCEMENT_ENABLED") is null)
            Environment.SetEnvironmentVariable("AUTH_ENFORCEMENT_ENABLED", "false");
    }
}
