using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>
/// The fail-closed master switch: enforcement is ON unless AUTH_ENFORCEMENT_ENABLED is EXPLICITLY
/// "false"/"0". Tested via the pure string overload so no test ever races on process-wide env state.
/// </summary>
public class EnforcementDefaultTests
{
    [Theory]
    [InlineData(null)]      // unset — the fresh-environment case that used to mean "everything open"
    [InlineData("")]        // empty
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData("banana")]  // unrecognized value → fail closed, never silently open
    public void Enforcement_is_on_unless_explicitly_disabled(string? raw) =>
        Assert.True(AuthorizationMiddleware.EnforcementEnabled(raw));

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("FALSE")]
    [InlineData("0")]
    public void Enforcement_off_only_for_explicit_false_or_zero(string raw) =>
        Assert.False(AuthorizationMiddleware.EnforcementEnabled(raw));
}
