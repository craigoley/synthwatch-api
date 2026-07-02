using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using SynthWatch.Api.Functions;
using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>
/// The shared JSON body reader (<see cref="RequestJson"/>) that fixed the non-JSON-Content-Type 500. Ground
/// truth (confirmed locally + against the deployed API): <c>ReadFromJsonAsync</c> throws
/// <see cref="InvalidOperationException"/> — NOT the <see cref="JsonException"/> the handlers caught — on a
/// wrong/absent Content-Type, which fell through to a shielded 500. The helper turns that into a clean 415;
/// malformed JSON stays 400; valid JSON is unchanged. This pins the matrix for the 8 endpoints that route
/// through the helper, plus the auth path (which uses its own guard → the enumeration-safe uniform 400).
/// </summary>
public class RequestJsonTests
{
    private sealed class Dummy { public int X { get; set; } }

    private static HttpRequest Req(string? contentType, string body)
    {
        var ctx = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentLength = bytes.Length;
        if (contentType is not null) ctx.Request.ContentType = contentType;
        return ctx.Request;
    }

    private static int Status(IActionResult? r) =>
        (r as ObjectResult)?.StatusCode ?? (r as StatusCodeResult)?.StatusCode ?? 0;

    [Fact]
    public async Task Non_json_content_type_is_415()
    {
        var (body, error) = await RequestJson.ReadAsync<Dummy>(Req("text/plain", "hello"));
        Assert.Null(body);
        Assert.Equal(415, Status(error));
    }

    [Fact]
    public async Task Missing_content_type_is_415()
    {
        var (body, error) = await RequestJson.ReadAsync<Dummy>(Req(null, "hello"));
        Assert.Null(body);
        Assert.Equal(415, Status(error));
    }

    [Fact]
    public async Task Malformed_json_is_400()
    {
        var (body, error) = await RequestJson.ReadAsync<Dummy>(Req("application/json", "not json"));
        Assert.Null(body);
        Assert.Equal(400, Status(error));
    }

    [Fact]
    public async Task Valid_json_returns_the_body_and_no_error()
    {
        var (body, error) = await RequestJson.ReadAsync<Dummy>(Req("application/json", "{\"x\":5}"));
        Assert.Null(error);
        Assert.NotNull(body);
        Assert.Equal(5, body!.X);
    }

    [Fact]
    public async Task Json_null_literal_is_a_null_body_with_no_error()
    {
        // A literal `null` body deserializes to (null, null) — the caller keeps its own "body is required" check.
        var (body, error) = await RequestJson.ReadAsync<Dummy>(Req("application/json", "null"));
        Assert.Null(error);
        Assert.Null(body);
    }

    [Fact]
    public async Task Charset_suffixed_json_content_type_still_parses()
    {
        var (body, error) = await RequestJson.ReadAsync<Dummy>(Req("application/json; charset=utf-8", "{\"x\":7}"));
        Assert.Null(error);
        Assert.Equal(7, body!.X);
    }

    // ── The AUTH path uses its own body reader (returns T? → the callers map an absent/bad body to a uniform
    //    400, the enumeration-safe posture). A non-JSON Content-Type must be that 400, NOT a 500. The path
    //    returns before touching the DB/email, so a null-dependency instance is safe here. ──
    [Fact]
    public async Task Auth_request_code_non_json_is_400_not_500()
    {
        var fn = new AuthFunctions(null!, null!, NullLogger<AuthFunctions>.Instance);
        var res = await fn.RequestCode(Req("text/plain", "hello"), default);
        Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(res).StatusCode);
    }
}
