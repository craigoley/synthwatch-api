using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>
/// The B10 enable-gate predicate (CheckValidation.SensitiveNeedsRedaction) — the API mirror of the runner's
/// reconcile.validateManifest gate (synthwatch #137). Same input → same verdict, so a check valid via the API
/// is valid via reconcile. The runner rule:
///   e.sensitive === true &amp;&amp; (!Array.isArray(e.redact_patterns) || e.redact_patterns.length === 0) → REJECT.
/// </summary>
public class B10SensitiveGateTests
{
    [Theory]
    // non-sensitive → always allowed (redaction is irrelevant), regardless of patterns
    [InlineData(false, null, false)]
    [InlineData(false, new[] { "token=\\S+" }, false)]
    // sensitive but NO redaction declared → needs redaction (block enable) — null OR empty array
    [InlineData(true, null, true)]
    [InlineData(true, new string[0], true)]
    // sensitive WITH ≥1 declared pattern → allowed
    [InlineData(true, new[] { "token=\\S+" }, false)]
    [InlineData(true, new[] { "sessionId=\\w+", "cart=.*" }, false)]
    public void Predicate_matches_reconcile_validateManifest(bool sensitive, string[]? patterns, bool expectedNeedsRedaction) =>
        Assert.Equal(expectedNeedsRedaction, CheckValidation.SensitiveNeedsRedaction(sensitive, patterns));
}
