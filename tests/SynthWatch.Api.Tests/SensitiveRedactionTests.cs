using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>B10: the new RCA context (assertion text + mutation URLs) must honor a sensitive check's
/// redact_patterns. Non-sensitive passes through; a malformed pattern is skipped, never thrown.</summary>
public class SensitiveRedactionTests
{
    [Fact]
    public void Non_sensitive_passes_through_unchanged() =>
        Assert.Equal("token=abc123", SensitiveRedaction.Redact("token=abc123", sensitive: false, ["token=\\S+"]));

    [Fact]
    public void Sensitive_applies_each_pattern()
    {
        var redacted = SensitiveRedaction.Redact("login as sessionId=XYZ token=abc", true, ["sessionId=\\w+", "token=\\S+"]);
        Assert.DoesNotContain("XYZ", redacted, System.StringComparison.Ordinal);
        Assert.DoesNotContain("abc", redacted, System.StringComparison.Ordinal);
        Assert.Contains("[redacted]", redacted, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_malformed_pattern_is_skipped_not_thrown() =>
        Assert.Equal("keep me", SensitiveRedaction.Redact("keep me", true, ["(unclosed["]));

    [Fact]
    public void Null_or_empty_inputs_are_safe()
    {
        Assert.Null(SensitiveRedaction.Redact(null, true, ["x"]));
        Assert.Equal("x", SensitiveRedaction.Redact("x", true, null));
    }
}
