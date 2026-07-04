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

    private static HttpRequest RawReq(string? contentType, string body)
    {
        var ctx = new DefaultHttpContext();
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentLength = bytes.Length;
        if (contentType is not null) ctx.Request.ContentType = contentType;
        return ctx.Request;
    }

    // ── ★ CONTENT-TYPE GATE (the fix): a non-JSON body is a clean 415, never a shielded 500. Before the fix
    //    ReadFromJsonAsync threw InvalidOperationException (NOT the caught JsonException) on a wrong content
    //    type → this handler would THROW here rather than return 415 (the must-go-red). A JSON content type
    //    with a malformed body stays the existing 400. FakeAoai is Configured=true so the body IS parsed. ──
    [Fact]
    public async Task Non_json_content_type_is_415_not_500()
    {
        var text = await new ParseIntentFunctions(new FakeAoai()).ParseMonitorIntent(RawReq("text/plain", "hello"), default);
        Assert.Equal(415, Assert.IsType<ObjectResult>(text).StatusCode);

        var none = await new ParseIntentFunctions(new FakeAoai()).ParseMonitorIntent(RawReq(null, "hello"), default);
        Assert.Equal(415, Assert.IsType<ObjectResult>(none).StatusCode);   // missing Content-Type → 415 too
    }

    [Fact]
    public async Task Malformed_json_is_still_400()
    {
        var res = await new ParseIntentFunctions(new FakeAoai()).ParseMonitorIntent(RawReq("application/json", "not json"), default);
        Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(res).StatusCode);
    }

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

    // ── #158: the chat path must CARRY the request fields the user asked for (method / headers / body /
    //    assertions), not silently drop them into a weaker or broken monitor. ──

    [Fact]
    public async Task Http_request_carries_headers_body_method_and_assertions()
    {
        // The 341/342 (custom headers) + 343 (POST + body) repro shape — mapped through now, not dropped.
        var dto = await Parse(Returning("""
            {"kind":"http","name":"algolia product search","targetUrl":"https://api.example.com/search",
             "method":"POST","expectedStatus":200,
             "requestHeaders":{"X-Api-Key":"k123","Content-Type":"application/json"},
             "requestBody":"{\"query\":\"cake\"}",
             "assertions":[{"source":"body","comparison":"contains","expected":"results"}]}
            """), "POST https://api.example.com/search with an X-Api-Key header and body {query:cake}, assert body contains results");
        Assert.True(dto.Valid);
        Assert.Null(dto.Redirect);
        Assert.Equal("POST", dto.Fields!.Method);
        Assert.Equal("k123", dto.Fields.RequestHeaders!["X-Api-Key"]);
        Assert.Equal("""{"query":"cake"}""", dto.Fields.RequestBody);
        Assert.Single(dto.Fields.Assertions!);
        Assert.Equal("body", dto.Fields.Assertions![0].Source);
        Assert.Empty(dto.FieldErrors);
    }

    [Fact]
    public async Task Content_check_request_carries_a_body_assertion_not_a_status_only_check()
    {
        // The 350/351 SILENT-WEAKER repro: a "make sure the page shows X" ask must produce the content
        // assertion, not a trivially-green status-only monitor.
        var dto = await Parse(Returning("""
            {"kind":"http","name":"cake catering handoff","targetUrl":"https://www.wegmans.com/cake",
             "bodyMustContain":"Order your cake"}
            """), "check https://www.wegmans.com/cake shows 'Order your cake'");
        Assert.True(dto.Valid);
        Assert.Equal("Order your cake", dto.Fields!.BodyMustContain);
    }

    // ★ VALIDATE-DON'T-TRUST still holds for the new fields: a malformed assertion / an inline auth secret is a
    //   visible fieldError (valid=false), NOT a silent drop and NOT a 500.
    [Fact]
    public async Task Malformed_assertion_or_inline_auth_secret_is_a_field_error_not_silent()
    {
        var badAssertion = await Parse(Returning("""
            {"kind":"http","name":"x","targetUrl":"https://x.com",
             "assertions":[{"source":"telepathy","comparison":"eq","expected":1}]}
            """), "http x asserting telepathy");
        Assert.False(badAssertion.Valid);
        Assert.True(badAssertion.FieldErrors.ContainsKey("assertions[0].source"));
        Assert.NotNull(badAssertion.Fields);                 // still prefilled so the human sees + fixes it

        var inlineSecret = await Parse(Returning("""
            {"kind":"http","name":"x","targetUrl":"https://x.com","auth":{"type":"bearer","token":"sk-live-123"}}
            """), "http x with bearer token sk-live-123");
        Assert.False(inlineSecret.Valid);
        Assert.True(inlineSecret.FieldErrors.ContainsKey("auth")); // references-only rule caught the inline secret
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
