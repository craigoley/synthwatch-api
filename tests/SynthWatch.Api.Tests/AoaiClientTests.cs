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

/// <summary>The C# AOAI client — mocks the HTTP so we assert the request shape, the OUTCOME classification
/// (finish_reason length/content_filter/empty), token-usage parsing, and the transient retry — all without a
/// live call. Plus the endpoint's honest failure messages + the inert path.</summary>
public class AoaiClientTests
{
    // Returns each queued response in order, sticking on the last; counts calls (for the retry assertions).
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Code, string Body)> _responses;
        private readonly bool _throws;
        public int Calls { get; private set; }
        public HttpRequestMessage? Request;
        public string? Body;

        public StubHandler(HttpStatusCode code, string body, bool throws = false)
            : this([(code, body)], throws) { }
        public StubHandler((HttpStatusCode Code, string Body)[] responses, bool throws = false)
        {
            _responses = new Queue<(HttpStatusCode, string)>(responses);
            _throws = throws;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            if (_throws) throw new HttpRequestException("boom");
            var (code, body) = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
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

    private const string OkBody =
        """{"choices":[{"message":{"content":"{\"summary\":\"ok\"}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1500,"completion_tokens":2000,"total_tokens":3500,"completion_tokens_details":{"reasoning_tokens":1200}}}""";

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
    public Task Request_shape_and_usage_and_finish_reason_are_parsed() => WithAoaiEnvAsync(async () =>
    {
        var h = new StubHandler(HttpStatusCode.OK, OkBody);
        var r = await Client(h).ChatJsonAsync("SYSTEM-PROMPT", "USER-SUMMARY", default);

        Assert.Equal(AoaiOutcome.Ok, r.Outcome);
        Assert.Equal("{\"summary\":\"ok\"}", r.Content);
        Assert.Equal("stop", r.FinishReason);
        // ★ usage parsed for observability
        Assert.NotNull(r.Usage);
        Assert.Equal(1500, r.Usage!.PromptTokens);
        Assert.Equal(2000, r.Usage.CompletionTokens);
        Assert.Equal(1200, r.Usage.ReasoningTokens);

        var url = h.Request!.RequestUri!.ToString();
        Assert.Contains("/openai/deployments/gpt-5-mini/chat/completions", url, StringComparison.Ordinal);
        Assert.Contains("api-version=2025-04-01-preview", url, StringComparison.Ordinal);
        Assert.Equal("Bearer", h.Request.Headers.Authorization!.Scheme);

        using var doc = JsonDocument.Parse(h.Body!);
        var root = doc.RootElement;
        Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());
        Assert.True(root.TryGetProperty("max_completion_tokens", out _));
        var messages = root.GetProperty("messages");
        Assert.Equal("SYSTEM-PROMPT", messages[0].GetProperty("content").GetString());
        Assert.Equal("USER-SUMMARY", messages[1].GetProperty("content").GetString());
    });

    [Fact]
    public Task Finish_reason_length_is_classified_truncated() => WithAoaiEnvAsync(async () =>
    {
        var h = new StubHandler(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"{partial"},"finish_reason":"length"}]}""");
        var r = await Client(h).ChatJsonAsync("s", "u", default);
        Assert.Equal(AoaiOutcome.Truncated, r.Outcome);
        Assert.Equal("length", r.FinishReason);
    });

    [Fact]
    public Task Content_filter_is_classified_filtered() => WithAoaiEnvAsync(async () =>
    {
        var h = new StubHandler(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":""},"finish_reason":"content_filter"}]}""");
        var r = await Client(h).ChatJsonAsync("s", "u", default);
        Assert.Equal(AoaiOutcome.Filtered, r.Outcome);
    });

    [Fact]
    public Task Empty_content_with_stop_is_classified_empty() => WithAoaiEnvAsync(async () =>
    {
        var h = new StubHandler(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":""},"finish_reason":"stop"}]}""");
        var r = await Client(h).ChatJsonAsync("s", "u", default);
        Assert.Equal(AoaiOutcome.EmptyContent, r.Outcome);
    });

    [Fact]
    public Task A_transient_5xx_retries_once_then_succeeds() => WithAoaiEnvAsync(async () =>
    {
        var h = new StubHandler([(HttpStatusCode.ServiceUnavailable, "busy"), (HttpStatusCode.OK, OkBody)]);
        var r = await Client(h).ChatJsonAsync("s", "u", default);
        Assert.Equal(2, h.Calls);                       // retried once
        Assert.Equal(AoaiOutcome.Ok, r.Outcome);        // the retry succeeded
    });

    [Fact]
    public Task A_non_transient_4xx_does_not_retry() => WithAoaiEnvAsync(async () =>
    {
        var h = new StubHandler(HttpStatusCode.BadRequest, "bad");
        var r = await Client(h).ChatJsonAsync("s", "u", default);
        Assert.Equal(1, h.Calls);                       // 400 is not transient → no retry
        Assert.Equal(AoaiOutcome.HttpError, r.Outcome);
        Assert.False(r.Transient);
    });

    [Fact]
    public Task A_thrown_request_is_non_fatal_faulted() => WithAoaiEnvAsync(async () =>
    {
        var r = await Client(new StubHandler(HttpStatusCode.OK, "{}", throws: true)).ChatJsonAsync("s", "u", default);
        Assert.Equal(AoaiOutcome.Faulted, r.Outcome);
        Assert.Null(r.Content);
    });

    // ── the endpoint maps outcomes to HONEST, distinct messages ──

    [Theory]
    [InlineData(AoaiOutcome.Truncated, false)]
    [InlineData(AoaiOutcome.Filtered, false)]
    [InlineData(AoaiOutcome.EmptyContent, false)]
    [InlineData(AoaiOutcome.Timeout, true)]
    public void MapFailure_marks_retryable_honestly(AoaiOutcome outcome, bool expectedRetryable)
    {
        var dto = AiInsightsFunctions.MapFailure(new AoaiResult(outcome, null, null, 0, null));
        Assert.Equal(expectedRetryable, dto.Retryable);
        Assert.NotNull(dto.Note);
    }

    [Fact]
    public void MapFailure_transient_http_is_retryable_but_hard_http_is_not()
    {
        Assert.True(AiInsightsFunctions.MapFailure(new AoaiResult(AoaiOutcome.HttpError, null, null, 503, null)).Retryable);
        Assert.False(AiInsightsFunctions.MapFailure(new AoaiResult(AoaiOutcome.HttpError, null, null, 400, null)).Retryable);
    }

    // ── the endpoint is INERT until configured ──
    private sealed class FakeAoai : IAoaiClient
    {
        public bool IsConfigured { get; init; }
        public Task<AoaiResult> ChatJsonAsync(string system, string user, CancellationToken ct) =>
            Task.FromResult(new AoaiResult(AoaiOutcome.NotConfigured, null, null, 0, null));
    }

    [Fact]
    public async Task Endpoint_returns_not_configured_when_aoai_is_unset_never_500()
    {
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
