using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>
/// B10 redaction-status VISIBILITY (June-29). The derived health + the additive DTO fields make a
/// sensitive-but-unredacted check (the leak risk) detectable from the response — no manual DB query.
/// </summary>
public class RedactionStatusTests
{
    [Theory]
    [InlineData(false, null, "n/a")]                          // not sensitive -> not applicable
    [InlineData(false, new[] { "token=\\S+" }, "n/a")]        // patterns irrelevant when not sensitive
    [InlineData(true, null, "misconfigured")]                 // ★ sensitive, NO patterns -> runs unredacted
    [InlineData(true, new string[0], "misconfigured")]        // sensitive, empty patterns -> still unredacted
    [InlineData(true, new[] { "token=\\S+" }, "ok")]          // sensitive + patterns -> redaction wired
    public void Health_derives_the_three_states(bool sensitive, string[]? patterns, string expected) =>
        Assert.Equal(expected, RedactionStatus.Health(sensitive, patterns));

    [Fact]
    public void HasPatterns_is_true_only_for_a_non_empty_list()
    {
        Assert.False(RedactionStatus.HasPatterns(null));
        Assert.False(RedactionStatus.HasPatterns([]));
        Assert.True(RedactionStatus.HasPatterns(["token=\\S+"]));
    }

    [Fact]
    public void Summary_dto_surfaces_the_misconfigured_leak_risk()
    {
        // A check flagged sensitive but with no redaction wired — exactly what must not hide.
        var check = new Check { Id = 1, Name = "cart", Kind = "browser", TargetUrl = "https://x", Enabled = true,
            Sensitive = true, RedactPatterns = null };
        var dto = CheckSummaryDto.From(check, latest: null, CheckMetricsDto.Empty, [], []);

        Assert.True(dto.Sensitive);
        Assert.False(dto.HasRedactPatterns);
        Assert.Equal("misconfigured", dto.RedactionHealth);
    }

    [Fact]
    public void Detail_dto_surfaces_ok_when_redaction_is_wired()
    {
        var check = new Check { Id = 1, Name = "cart", Kind = "browser", TargetUrl = "https://x", Enabled = true,
            Sensitive = true, RedactPatterns = ["token=\\S+", "sessionId=\\w+"] };
        var dto = CheckDetailDto.From(check, recentRuns: [], tags: []);

        Assert.True(dto.Sensitive);
        Assert.True(dto.HasRedactPatterns);
        Assert.Equal("ok", dto.RedactionHealth);
    }

    [Fact]
    public void Non_sensitive_check_is_n_a()
    {
        var check = new Check { Id = 1, Name = "home", Kind = "http", TargetUrl = "https://x", Enabled = true };
        var dto = CheckDetailDto.From(check, recentRuns: [], tags: []);

        Assert.False(dto.Sensitive);
        Assert.False(dto.HasRedactPatterns);
        Assert.Equal("n/a", dto.RedactionHealth);
    }
}
