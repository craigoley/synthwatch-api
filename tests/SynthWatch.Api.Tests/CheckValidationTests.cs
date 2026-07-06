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

    // #169: a check created with NO explicit failure_threshold must get the runner's canonical default (1),
    // not the old drifted 3. Runner db/schema.sql = DEFAULT 1 (migration 0045, "was 3" — alert on the FIRST
    // scheduled-down since in-run retries already confirm the failure). Goes RED if the create-path default
    // (CheckValidation.TryBuildNew `?? 1`) is reverted to 3 or the comparison drifts.
    [Fact]
    public void New_check_without_explicit_failure_threshold_defaults_to_1()
    {
        var req = HttpReq(); // no FailureThreshold set → null
        Assert.Null(req.FailureThreshold);
        Assert.True(CheckValidation.TryBuildNew(req, out var check, out _));
        Assert.Equal(1, check.FailureThreshold);
    }

    [Fact]
    public void Explicit_failure_threshold_is_preserved_over_the_default()
    {
        var req = HttpReq();
        req.FailureThreshold = 3; // a deliberate value must NOT be coerced to the default
        Assert.True(CheckValidation.TryBuildNew(req, out var check, out _));
        Assert.Equal(3, check.FailureThreshold);
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

    // ---- Phase 13 activation: spec_path / source_key mapping + shape validation ----

    private static CreateCheckRequest ActivationReq() => new()
    {
        Name = "Wegmans — search product",
        Kind = "browser",
        TargetUrl = "https://www.wegmans.com",
        FlowName = "search-product", // synthetic flowNameFor(spec_path) — satisfies browser_needs_flow
        SourceKey = "wegmans-search-product",
        SpecPath = "monitors/wegmans/search-product.spec.ts",
    };

    [Fact]
    public void Browser_check_with_flow_name_and_spec_path_builds_and_maps_both()
    {
        // The gotcha: browser_needs_flow requires flow_name even though spec_path drives runtime. With
        // BOTH set, the check builds and carries the spec binding the runner will fetch+run (Option C).
        var ok = CheckValidation.TryBuildNew(ActivationReq(), out var check, out var errors);
        Assert.True(ok);
        Assert.Empty(errors);
        Assert.Equal("browser", check.Kind);
        Assert.Equal("search-product", check.FlowName);
        Assert.Equal("wegmans-search-product", check.SourceKey);
        Assert.Equal("monitors/wegmans/search-product.spec.ts", check.SpecPath);
    }

    [Theory]
    [InlineData("monitors/../etc/passwd.spec.ts")] // traversal
    [InlineData("monitors/wegmans/search-product.ts")] // not a .spec.ts
    [InlineData("checks/wegmans/search.spec.ts")] // not under monitors/
    [InlineData("monitors/.spec.ts")] // empty middle segment (^monitors/.+\.spec\.ts$)
    public void Bad_spec_path_shape_is_rejected(string specPath)
    {
        var req = ActivationReq();
        req.SpecPath = specPath;
        var ok = CheckValidation.TryBuildNew(req, out _, out var errors);
        Assert.False(ok);
        Assert.Contains("specPath", errors.Keys);
    }

    [Fact]
    public void Browser_activation_without_flow_name_is_rejected() // browser_needs_flow still applies
    {
        var req = ActivationReq();
        req.FlowName = null;
        var ok = CheckValidation.TryBuildNew(req, out _, out var errors);
        Assert.False(ok);
        Assert.Contains("flowName", errors.Keys);
    }

    [Fact]
    public void Hand_made_check_leaves_spec_binding_null() // no spec_path/source_key → baked-flow path
    {
        Assert.True(CheckValidation.TryBuildNew(HttpReq(), out var check, out _));
        Assert.Null(check.SpecPath);
        Assert.Null(check.SourceKey);
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
