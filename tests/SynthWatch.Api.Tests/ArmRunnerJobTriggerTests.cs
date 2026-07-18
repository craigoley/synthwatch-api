using System.Net;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>
/// The ARM job-start trigger. Pins the content-type fix: ARM's Microsoft.App/jobs/start REQUIRES an
/// application/json body — the old empty text/plain body returned 415 and the on-demand run silently fell
/// back to the next */5 cron tick (DIAGNOSED in prod). A regress to text/plain fails the request-shape test.
/// </summary>
public class ArmRunnerJobTriggerTests
{
    private sealed class CapturingHandler(HttpStatusCode code, bool throws = false) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? ContentType;
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            if (throws) throw new HttpRequestException("network down");
            return new HttpResponseMessage(code) { Content = new StringContent("{}") };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) => new("tok", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) => new(GetToken(c, ct));
    }

    private static ArmRunnerJobTrigger Trigger(HttpMessageHandler h) =>
        new(new FakeCredential(), new StubFactory(h), Options.Create(new RunnerJobOptions()),
            NullLogger<ArmRunnerJobTrigger>.Instance);

    [Fact]
    public async Task Start_posts_an_empty_json_object_with_application_json_content_type()
    {
        var h = new CapturingHandler(HttpStatusCode.OK);
        var ok = await Trigger(h).StartAsync(default);

        Assert.True(ok);
        Assert.Equal(HttpMethod.Post, h.Request!.Method);
        // ★ the fix: application/json (not text/plain) + a {} body — else ARM returns 415.
        Assert.Equal("application/json", h.ContentType);
        Assert.Equal("{}", h.Body);
        Assert.Equal("Bearer", h.Request.Headers.Authorization!.Scheme);
        Assert.Contains("Microsoft.App/jobs/synthwatch-runner-job/start", h.Request.RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Contains("api-version=2024-03-01", h.Request.RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_non_success_status_is_non_fatal_returns_false()
    {
        // e.g. the 415 the old body produced — must NOT throw into the request (the cron tick is the fallback).
        Assert.False(await Trigger(new CapturingHandler(HttpStatusCode.UnsupportedMediaType)).StartAsync(default));
    }

    [Fact]
    public async Task A_thrown_request_is_non_fatal_returns_false()
    {
        Assert.False(await Trigger(new CapturingHandler(HttpStatusCode.OK, throws: true)).StartAsync(default));
    }

    // ── by-name overload (reconcile path): same {} + application/json body, a DIFFERENT target job ──
    [Fact]
    public async Task Start_by_name_targets_the_named_job_with_the_same_415_fixed_body()
    {
        var h = new CapturingHandler(HttpStatusCode.OK);
        var ok = await Trigger(h).StartAsync("synthwatch-reconcile-job", default);

        Assert.True(ok);
        // ★ targets the RECONCILE job (not the runner job) — and the 415 fix is shared, not re-implemented.
        Assert.Contains("Microsoft.App/jobs/synthwatch-reconcile-job/start", h.Request!.RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("synthwatch-runner-job", h.Request.RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Equal("application/json", h.ContentType);
        Assert.Equal("{}", h.Body);
    }

    [Fact]
    public async Task The_parameterless_start_still_targets_the_runner_job_unchanged()
    {
        var h = new CapturingHandler(HttpStatusCode.OK);
        await Trigger(h).StartAsync(default); // the "Run now" / test-send path

        Assert.Contains("Microsoft.App/jobs/synthwatch-runner-job/start", h.Request!.RequestUri!.ToString(), StringComparison.Ordinal);
    }

    // ── env-override (sandbox preview) path — the #269-followup fix ────────────────────────────────────────
    // A jobs/start env override REPLACES the container wholesale, so it must carry image/command/args/resources
    // (a bare {name,env} → 400 "must have an 'Image'"; dropping command → runs the MAIN runner). BuildStartBody
    // GETs the configured container and MERGES the override env over it.
    private const string SandboxJobJson = """
    { "properties": { "template": { "containers": [ {
        "name": "sandbox",
        "image": "acr.azurecr.io/synthwatch-runner:abc123",
        "command": [ "node" ],
        "args": [ "dist/sandbox/sandboxMain.js" ],
        "resources": { "cpu": 1.0, "memory": "2Gi" },
        "env": [ { "name": "SW_SANDBOX", "value": "1" },
                 { "name": "SANDBOX_CONTAINER", "value": "synthwatch-sandbox" } ]
    } ] } } }
    """;

    private static readonly Dictionary<string, string> OverrideEnv = new()
    {
        ["SW_SANDBOX_SPEC_B64"] = "aW1wb3J0",
        ["SW_SANDBOX_TARGET_URL"] = "https://example.com",
        ["SW_SANDBOX_RESULT_TOKEN"] = "tok-123",
    };

    [Fact]
    public void BuildStartBody_top_level_containers_with_image_command_args_resources_and_merged_env()
    {
        var body = ArmRunnerJobTrigger.BuildStartBody(SandboxJobJson, "sandbox", OverrideEnv);
        Assert.NotNull(body);
        var root = System.Text.Json.Nodes.JsonNode.Parse(body!)!;

        // ★ TOP-LEVEL containers — NOT wrapped in `template` (that 400s "Unknown properties template").
        Assert.Null(root["template"]);
        var c = root["containers"]!.AsArray()[0]!;
        Assert.Equal("sandbox", (string?)c["name"]);
        // ★ image/command/args/resources PRESERVED (else 400, or the wrong entrypoint runs).
        Assert.Equal("acr.azurecr.io/synthwatch-runner:abc123", (string?)c["image"]);
        Assert.Equal("node", (string?)c["command"]!.AsArray()[0]);
        Assert.Equal("dist/sandbox/sandboxMain.js", (string?)c["args"]!.AsArray()[0]);
        Assert.Equal("2Gi", (string?)c["resources"]!["memory"]);

        // env = base (2) + override (3), override values present, no duplicates.
        var env = c["env"]!.AsArray().ToDictionary(n => (string)n!["name"]!, n => (string?)n!["value"]);
        Assert.Equal("1", env["SW_SANDBOX"]);
        Assert.Equal("synthwatch-sandbox", env["SANDBOX_CONTAINER"]);
        Assert.Equal("aW1wb3J0", env["SW_SANDBOX_SPEC_B64"]);
        Assert.Equal("https://example.com", env["SW_SANDBOX_TARGET_URL"]);
        Assert.Equal("tok-123", env["SW_SANDBOX_RESULT_TOKEN"]);
        Assert.Equal(5, c["env"]!.AsArray().Count);
    }

    [Fact]
    public void BuildStartBody_override_replaces_a_same_named_env_without_duplicating()
    {
        var body = ArmRunnerJobTrigger.BuildStartBody(SandboxJobJson, "sandbox",
            new Dictionary<string, string> { ["SANDBOX_CONTAINER"] = "override-wins" });
        var env = System.Text.Json.Nodes.JsonNode.Parse(body!)!["containers"]!.AsArray()[0]!["env"]!.AsArray();
        var matches = env.Where(n => (string?)n!["name"] == "SANDBOX_CONTAINER").ToList();
        Assert.Single(matches);
        Assert.Equal("override-wins", (string?)matches[0]!["value"]);
    }

    [Fact]
    public void BuildStartBody_preserves_a_secretRef_env_entry_untouched()
    {
        const string withSecret = """
        { "properties": { "template": { "containers": [ {
            "name": "c", "image": "img",
            "env": [ { "name": "DB", "secretRef": "db-conn" } ] } ] } } }
        """;
        var body = ArmRunnerJobTrigger.BuildStartBody(withSecret, "c",
            new Dictionary<string, string> { ["X"] = "y" });
        var db = System.Text.Json.Nodes.JsonNode.Parse(body!)!["containers"]!.AsArray()[0]!["env"]!.AsArray()
            .First(n => (string?)n!["name"] == "DB")!;
        Assert.Equal("db-conn", (string?)db["secretRef"]);
    }

    [Fact]
    public void BuildStartBody_returns_null_when_the_named_container_is_absent()
        => Assert.Null(ArmRunnerJobTrigger.BuildStartBody(SandboxJobJson, "does-not-exist", OverrideEnv));

    [Fact]
    public async Task Env_override_GETs_the_job_then_POSTs_the_full_container_start_body()
    {
        // Handler: GET → the job JSON; POST → capture the start body.
        var flow = new SequencedHandler(SandboxJobJson);
        var trigger = Trigger(flow);
        var ok = await trigger.StartWithEnvOverrideAsync("synthwatch-sandbox", "sandbox", OverrideEnv, default);

        Assert.True(ok);
        Assert.Equal(HttpMethod.Get, flow.GetMethod);
        Assert.Equal(HttpMethod.Post, flow.PostMethod);
        Assert.Contains("Microsoft.App/jobs/synthwatch-sandbox/start", flow.PostUri!, StringComparison.Ordinal);
        Assert.Equal("application/json", flow.PostContentType);
        var posted = System.Text.Json.Nodes.JsonNode.Parse(flow.PostBody!)!;
        Assert.Null(posted["template"]);
        Assert.Equal("acr.azurecr.io/synthwatch-runner:abc123", (string?)posted["containers"]!.AsArray()[0]!["image"]);
    }

    private sealed class SequencedHandler(string jobJson) : HttpMessageHandler
    {
        public HttpMethod? GetMethod, PostMethod;
        public string? PostUri, PostContentType, PostBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
            {
                GetMethod = request.Method;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(jobJson) };
            }
            PostMethod = request.Method;
            PostUri = request.RequestUri!.ToString();
            PostContentType = request.Content?.Headers.ContentType?.MediaType;
            PostBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
