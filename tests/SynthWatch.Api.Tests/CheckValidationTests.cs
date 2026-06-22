using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>
/// Pure validation tests — the security-critical refusals and per-kind rules that MUST stay green.
/// No DB/host needed; CheckValidation is a pure static surface. A regression here would (e.g.) let
/// plaintext credentials into the DB, so these are the highest-value lock-down tests.
/// </summary>
public class CheckValidationTests
{
    private static CreateCheckRequest HttpReq() =>
        new() { Name = "x", Kind = "http", TargetUrl = "https://example.com" };

    [Fact]
    public void Valid_http_check_passes() // backward-compat: a plain single-kind check round-trips
    {
        var ok = CheckValidation.TryBuildNew(HttpReq(), out var check, out var errors);
        Assert.True(ok);
        Assert.Empty(errors);
        Assert.Equal("http", check.Kind);
    }

    // ---- secret-ref auth: inline credentials MUST be refused (the security property) ----

    [Fact]
    public void Inline_credential_in_auth_is_refused()
    {
        var req = HttpReq();
        req.Auth = new() { ["type"] = "bearer", ["token"] = "sk-live-REALSECRET" }; // raw value, not *_env
        var ok = CheckValidation.TryBuildNew(req, out _, out var errors);
        Assert.False(ok);
        Assert.Contains("auth", errors.Keys);
    }

    [Fact]
    public void Envref_auth_is_accepted()
    {
        var req = HttpReq();
        req.Auth = new() { ["type"] = "bearer", ["token_env"] = "API_TOKEN_ENV" };
        Assert.True(CheckValidation.TryBuildNew(req, out _, out _));
    }

    // ---- assertions ----

    [Theory]
    [InlineData("statusish", "eq", "assertions[0].source")]
    [InlineData("status", "equalsish", "assertions[0].comparison")]
    public void Bad_assertion_is_rejected_with_field_key(string source, string comparison, string key)
    {
        var req = HttpReq();
        req.Assertions = new() { new Assertion { Source = source, Comparison = comparison } };
        var ok = CheckValidation.TryBuildNew(req, out _, out var errors);
        Assert.False(ok);
        Assert.Contains(key, errors.Keys);
    }

    // ---- network checks ----

    [Fact]
    public void Tcp_without_a_port_is_rejected()
    {
        var req = new CreateCheckRequest { Name = "x", Kind = "tcp", TargetUrl = "example.com" };
        var ok = CheckValidation.TryBuildNew(req, out _, out var errors);
        Assert.False(ok);
        Assert.Contains("netConfig.port", errors.Keys);
    }

    [Fact]
    public void Dns_with_bad_recordtype_is_rejected()
    {
        var req = new CreateCheckRequest
        {
            Name = "x", Kind = "dns", TargetUrl = "example.com",
            NetConfig = new NetConfig { RecordType = "ZZZ" }
        };
        var ok = CheckValidation.TryBuildNew(req, out _, out var errors);
        Assert.False(ok);
        Assert.Contains("netConfig.recordType", errors.Keys);
    }

    [Fact]
    public void Non_network_kind_with_netconfig_is_rejected()
    {
        var req = HttpReq();
        req.NetConfig = new NetConfig { Port = 80 };
        Assert.False(CheckValidation.TryBuildNew(req, out _, out var errors));
        Assert.Contains("netConfig", errors.Keys);
    }

    // ---- multistep ----

    private static CreateCheckRequest MultistepReq(params ChainStep[] steps) =>
        new() { Name = "x", Kind = "multistep", TargetUrl = "https://example.com", Steps = steps.ToList() };

    [Fact]
    public void Multistep_with_empty_steps_is_rejected()
    {
        Assert.False(CheckValidation.TryBuildNew(MultistepReq(), out _, out var errors));
        Assert.Contains("steps", errors.Keys);
    }

    [Fact]
    public void Non_multistep_carrying_steps_is_rejected()
    {
        var req = HttpReq();
        req.Steps = new() { new ChainStep { Name = "s", Url = "https://x" } };
        Assert.False(CheckValidation.TryBuildNew(req, out _, out var errors));
        Assert.Contains("steps", errors.Keys);
    }

    [Fact]
    public void Multistep_dangling_template_var_is_rejected()
    {
        var req = MultistepReq(
            new ChainStep { Name = "s1", Url = "https://example.com/a" },
            new ChainStep { Name = "s2", Url = "https://example.com/b", Headers = new() { ["X-Tok"] = "{{nope}}" } });
        Assert.False(CheckValidation.TryBuildNew(req, out _, out var errors));
        Assert.Contains("steps[1].template", errors.Keys);
    }

    [Fact]
    public void Multistep_step_with_inline_credential_is_refused() // per-step security property
    {
        var req = MultistepReq(new ChainStep
        {
            Name = "login", Url = "https://example.com/login",
            Auth = new() { ["type"] = "bearer", ["token"] = "sk-live-REALSECRET" }
        });
        Assert.False(CheckValidation.TryBuildNew(req, out _, out var errors));
        Assert.Contains("steps[0].auth", errors.Keys);
    }

    [Fact]
    public void Multistep_valid_chain_with_extract_and_template_passes()
    {
        var req = MultistepReq(
            new ChainStep
            {
                Name = "login", Method = "POST", Url = "https://example.com/login",
                Auth = new() { ["type"] = "basic", ["username"] = "svc", ["password_env"] = "PW_ENV" },
                Extract = new() { new ExtractRule { Var = "token", JsonPath = "$.access_token" } }
            },
            new ChainStep
            {
                Name = "call", Url = "https://example.com/me",
                Headers = new() { ["Authorization"] = "Bearer {{token}}" }
            });
        Assert.True(CheckValidation.TryBuildNew(req, out _, out var errors));
        Assert.Empty(errors);
    }
}
