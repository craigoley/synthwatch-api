using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SynthWatch.Api.Data;
using SynthWatch.Api.Dtos;
using SynthWatch.Api.Functions;
using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>The C# AOAI client — mocks the HTTP so we assert the REQUEST SHAPE (endpoint, api-version, JSON
/// mode, the prompt in the body) without a live call, plus IsConfigured gating and non-fatal behaviour. Also
/// proves the endpoint is INERT when unconfigured (returns "not configured", never a 500).</summary>
public class AoaiClientTests
{
    private sealed class StubHandler(HttpStatusCode code, string body, bool throws = false) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            if (throws) throw new HttpRequestException("boom");
            return new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) =>
            new("fake-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) =>
            new(GetToken(c, ct));
    }

    private static AoaiClient Client(StubHandler h) =>
        new(new HttpClient(h), new FakeCredential(), NullLogger<AoaiClient>.Instance);

    private static async Task WithAoaiEnvAsync(Func<Task> body)
    {
        var (e, d, v) = (Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT"),
                         Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT"),
                         Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION"));
        try
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", "https://synthwatch-aoai.openai.azure.com/");
            Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT", "gpt-5-mini");
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_VERSION", "2025-04-01-preview");
            await body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", e);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT", d);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_VERSION", v);
        }
    }

    [Fact]
    public async Task IsConfigured_follows_the_settings()
    {
        var h = new StubHandler(HttpStatusCode.OK, "{}");
        Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", null);
        Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT", null);
        Assert.False(Client(h).IsConfigured);
        await WithAoaiEnvAsync(() => { Assert.True(Client(h).IsConfigured); return Task.CompletedTask; });
    }

    [Fact]
    public Task Request_shape_is_the_AOAI_chat_completions_contract() => WithAoaiEnvAsync(async () =>
    {
        var h = new StubHandler(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"{\"summary\":\"ok\"}"}}]}""");
        var content = await Client(h).ChatJsonAsync("SYSTEM-PROMPT", "USER-SUMMARY", default);

        // returned the model content
        Assert.Equal("{\"summary\":\"ok\"}", content);

        // endpoint + deployment + api-version
        var url = h.Request!.RequestUri!.ToString();
        Assert.Contains("/openai/deployments/gpt-5-mini/chat/completions", url, StringComparison.Ordinal);
        Assert.Contains("api-version=2025-04-01-preview", url, StringComparison.Ordinal);
        Assert.Equal("Bearer", h.Request.Headers.Authorization!.Scheme);

        // body: JSON mode + the two messages carrying our prompts
        using var doc = JsonDocument.Parse(h.Body!);
        var root = doc.RootElement;
        Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());
        Assert.True(root.TryGetProperty("max_completion_tokens", out _));
        var messages = root.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("SYSTEM-PROMPT", messages[0].GetProperty("content").GetString());
        Assert.Equal("USER-SUMMARY", messages[1].GetProperty("content").GetString());
    });

    [Fact]
    public Task Non_ok_http_is_non_fatal_null() => WithAoaiEnvAsync(async () =>
        Assert.Null(await Client(new StubHandler(HttpStatusCode.InternalServerError, "nope")).ChatJsonAsync("s", "u", default)));

    [Fact]
    public Task A_thrown_request_is_non_fatal_null() => WithAoaiEnvAsync(async () =>
        Assert.Null(await Client(new StubHandler(HttpStatusCode.OK, "{}", throws: true)).ChatJsonAsync("s", "u", default)));

    // ── the endpoint is INERT until configured ──
    private sealed class FakeAoai : IAoaiClient
    {
        public bool IsConfigured { get; init; }
        public Task<string?> ChatJsonAsync(string system, string user, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    [Fact]
    public async Task Endpoint_returns_not_configured_when_aoai_is_unset_never_500()
    {
        // A context that is never queried (the IsConfigured short-circuit returns first).
        var opts = new DbContextOptionsBuilder<SynthWatchDbContext>().UseNpgsql("Host=localhost;Database=none").Options;
        await using var db = new SynthWatchDbContext(opts);
        var fn = new AiInsightsFunctions(db, new FakeCredential(), new FakeAoai { IsConfigured = false },
            NullLogger<AiInsightsFunctions>.Instance);

        var result = await fn.GetAiInsights(new DefaultHttpContext().Request, 123, default);
        var dto = Assert.IsType<AiInsightsDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.False(dto.Configured);
        Assert.NotNull(dto.Note);
    }
}
