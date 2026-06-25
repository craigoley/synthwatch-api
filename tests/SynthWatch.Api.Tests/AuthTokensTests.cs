using System.Text.RegularExpressions;
using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>Pure tests for the auth crypto helpers (no DB). The OTP/session FLOW is in IntegrationTests.</summary>
public class AuthTokensTests
{
    [Fact]
    public void Sha256Hex_is_deterministic_lowercase_hex()
    {
        var a = AuthTokens.Sha256Hex("123456");
        Assert.Equal(a, AuthTokens.Sha256Hex("123456"));
        Assert.NotEqual(a, AuthTokens.Sha256Hex("123457"));
        Assert.Matches("^[0-9a-f]{64}$", a); // 32-byte digest, lowercase hex — never the raw value
    }

    [Fact]
    public void NewNumericCode_is_six_digits()
    {
        for (var i = 0; i < 200; i++)
            Assert.Matches("^[0-9]{6}$", AuthTokens.NewNumericCode());
    }

    [Fact]
    public void NewSessionToken_is_prefixed_random_and_unique()
    {
        var t = AuthTokens.NewSessionToken();
        Assert.StartsWith("swt_", t);
        Assert.Matches("^swt_[A-Za-z0-9_-]+$", t); // base64url, no padding
        Assert.NotEqual(t, AuthTokens.NewSessionToken());
    }

    [Theory]
    [InlineData("Bearer swt_abc", "swt_abc")]
    [InlineData("bearer swt_abc", "swt_abc")] // case-insensitive scheme
    [InlineData("Bearer   swt_abc  ", "swt_abc")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("swt_abc", null)] // no scheme
    [InlineData("Basic abc", null)]
    [InlineData("Bearer ", null)] // empty token
    public void BearerFrom_extracts_the_token(string? header, string? expected)
    {
        Assert.Equal(expected, AuthTokens.BearerFrom(header));
    }

    [Theory]
    [InlineData(" Craig@Example.COM ", "craig@example.com")]
    [InlineData("a@b.io", "a@b.io")]
    public void NormalizeEmail_lowercases_and_trims(string raw, string expected)
    {
        Assert.Equal(expected, AuthTokens.NormalizeEmail(raw));
    }

    [Fact]
    public void Code_is_stored_hashed_not_plaintext()
    {
        // The property the whole design rests on: what we'd persist is the hash, which doesn't contain
        // the code and can't be reversed to it.
        var code = AuthTokens.NewNumericCode();
        var hash = AuthTokens.Sha256Hex(code);
        Assert.DoesNotContain(code, hash, StringComparison.Ordinal);
        Assert.False(Regex.IsMatch(hash, "[0-9]{6}") && hash.Contains(code, StringComparison.Ordinal));
    }
}
