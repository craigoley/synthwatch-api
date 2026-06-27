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

    private static ArmRunnerJobTrigger Trigger(CapturingHandler h) =>
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
}
