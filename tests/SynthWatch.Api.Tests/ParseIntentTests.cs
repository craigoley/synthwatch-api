using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SynthWatch.Api.Dtos;
using SynthWatch.Api.Functions;
using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>
/// POST /api/checks/parse-intent — chat-to-prefill. No DB: the model is stubbed (IAoaiClient) so the test is
/// deterministic. Pins ★ VALIDATE-DON'T-TRUST (a hallucinated spec returns field-errors, never a 500), the
/// browser→redirect path, the not-configured inert state, and the response wire shape (the #123 discipline).
/// </summary>
public class ParseIntentTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class FakeAoai : IAoaiClient
    {
        public bool Configured = true;
        public AoaiResult Result = new(AoaiOutcome.Ok, "{}", "stop", 200, null);
        public bool IsConfigured => Configured;
        public Task<AoaiResult> ChatJsonAsync(string system, string user, CancellationToken ct) => Task.FromResult(Result);
    }

    private static FakeAoai Returning(string json) => new() { Result = new(AoaiOutcome.Ok, json, "stop", 200, null) };

    private static HttpRequest TextReq(string text)
    {
        var ctx = new DefaultHttpContext();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { text });
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentType = "application/json";
        ctx.Request.ContentLength = bytes.Length;
        return ctx.Request;
    }

    private static async Task<ParseIntentDto> Parse(IAoaiClient aoai, string text) =>
        Assert.IsType<ParseIntentDto>(Assert.IsType<OkObjectResult>(
            await new ParseIntentFunctions(aoai).ParseMonitorIntent(TextReq(text), default)).Value!);

    // ── the validation cases ──
    [Fact]
    public async Task Ping_request_prefills_tcp_reachability_kind()
    {
        var dto = await Parse(Returning(
            """{"kind":"ping","name":"meals2go reachability","targetUrl":"meals2go.com","netConfig":{"port":443}}"""),
            "set up a ping monitor for meals2go.com");
        Assert.True(dto.Valid);
        Assert.Null(dto.Redirect);
        Assert.Equal("ping", dto.Fields!.Kind);          // native kind = TCP-reachability (not ICMP)
        Assert.Equal("meals2go.com", dto.Fields.TargetUrl);
        Assert.Empty(dto.FieldErrors);
    }

    [Fact]
    public async Task Http_and_ssl_requests_prefill_their_kinds()
    {
        var http = await Parse(Returning("""{"kind":"http","name":"wegmans","targetUrl":"https://www.wegmans.com"}"""), "http check for https://www.wegmans.com");
        Assert.True(http.Valid);
        Assert.Equal("http", http.Fields!.Kind);

        var ssl = await Parse(Returning("""{"kind":"ssl","name":"amore cert","targetUrl":"https://wegmansamore.com","certExpiryWarnDays":21}"""), "ssl monitor for wegmansamore.com");
        Assert.True(ssl.Valid);
        Assert.Equal("ssl", ssl.Fields!.Kind);
        Assert.Equal(21, ssl.Fields.CertExpiryWarnDays);
    }

    // ★ VALIDATE-DON'T-TRUST: a hallucinated kind / a net_config on an http check → field-errors, NOT a 500.
    [Fact]
    public async Task Hallucinated_spec_returns_field_errors_not_a_500()
    {
        var bogusKind = await Parse(Returning("""{"kind":"telepathy","name":"x","targetUrl":"x.com"}"""), "monitor x");
        Assert.False(bogusKind.Valid);
        Assert.True(bogusKind.FieldErrors.ContainsKey("kind"));  // the SAME validator POST /checks uses caught it
        Assert.NotNull(bogusKind.Fields);                        // fields still returned so the form prefills what parsed

        var badTarget = await Parse(Returning("""{"kind":"http","name":"x","targetUrl":"not-a-url"}"""), "http for x");
        Assert.False(badTarget.Valid);
        Assert.True(badTarget.FieldErrors.ContainsKey("targetUrl"));
    }

    // ★ Browser/multistep ask → redirect, never a fabricated prefill.
    [Fact]
    public async Task Browser_request_redirects_with_no_prefill()
    {
        var dto = await Parse(Returning("""{"redirect":"browser","reason":"Browser monitors are authored as code in the monitors repo."}"""),
            "monitor the checkout flow");
        Assert.Equal("browser", dto.Redirect);
        Assert.Contains("authored as code", dto.Reason!, StringComparison.Ordinal);
        Assert.Null(dto.Fields);
        Assert.False(dto.Valid);
    }

    [Fact]
    public async Task Not_configured_is_inert_not_a_500()
    {
        var dto = await Parse(new FakeAoai { Configured = false }, "anything");
        Assert.False(dto.Configured);
        Assert.Null(dto.Fields);
    }

    [Fact]
    public async Task Empty_text_is_a_400()
    {
        var res = await new ParseIntentFunctions(new FakeAoai()).ParseMonitorIntent(TextReq("  "), default);
        Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(res).StatusCode);
    }

    // ── the response wire shape (the #123 discipline) ──
    [Fact]
    public async Task Response_shape_is_pinned()
    {
        var dto = await Parse(Returning("""{"kind":"http","name":"x","targetUrl":"https://x.com"}"""), "http x");
        var root = JsonDocument.Parse(JsonSerializer.Serialize(dto, Web)).RootElement;
        Assert.Equal(
            new[] { "configured", "fieldErrors", "fields", "note", "notes", "reason", "redirect", "retryable", "valid" },
            root.EnumerateObject().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }
}
