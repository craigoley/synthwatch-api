using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>
/// The ERROR-envelope shape contract (the error-surface analogue of #123's list-shape pins). Every ApiResults
/// 4xx is RFC 9457 problem+json with the legacy error/message (and details) the dashboard reads + a traceable
/// instance — pinned to the EXACT top-level key set so a future error-shape change fails loudly. Also pins the
/// status code + the typed *ObjectResult subclass (so the 50 existing IsType&lt;…ObjectResult&gt; assertions hold).
/// </summary>
public class ErrorEnvelopeContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly string[] BaseKeys = ["type", "title", "status", "detail", "instance", "error", "message"];

    private static JsonElement Wire(object? value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, Web)).RootElement.Clone();

    private static void AssertProblem(IActionResult r, int status, string legacyError, string detail, params string[] extraKeys)
    {
        var obj = Assert.IsAssignableFrom<ObjectResult>(r);
        Assert.Equal(status, obj.StatusCode);                                  // status unchanged
        Assert.Contains(ProblemResults.ContentType, obj.ContentTypes);        // application/problem+json
        var root = Wire(obj.Value);
        // ★ exact top-level key set (the teeth) — RFC 9457 members + the legacy extension members.
        var keys = root.EnumerateObject().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(BaseKeys.Concat(extraKeys).OrderBy(k => k, StringComparer.Ordinal).ToArray(), keys);
        Assert.Equal("about:blank", root.GetProperty("type").GetString());
        Assert.Equal(status, root.GetProperty("status").GetInt32());
        Assert.Equal(detail, root.GetProperty("detail").GetString());
        // backward-compat: the fields the dashboard's error path reads are intact.
        Assert.Equal(legacyError, root.GetProperty("error").GetString());
        Assert.Equal(detail, root.GetProperty("message").GetString());
    }

    [Fact]
    public void Every_4xx_is_problem_json_with_legacy_fields_status_and_typed_result()
    {
        AssertProblem(ApiResults.NotFound("nope"), 404, "not_found", "nope");
        AssertProblem(ApiResults.BadRequest("bad"), 400, "bad_request", "bad");
        AssertProblem(ApiResults.Conflict("dup"), 409, "conflict", "dup");
        AssertProblem(ApiResults.Unauthorized("need auth"), 401, "unauthorized", "need auth");
        AssertProblem(ApiResults.Forbidden("no perm"), 403, "forbidden", "no perm");
        AssertProblem(ApiResults.ServiceUnavailable("retry later"), 503, "unavailable", "retry later");

        // The typed subclasses are preserved — the 50 existing IsType<…ObjectResult> assertions still hold.
        Assert.IsType<NotFoundObjectResult>(ApiResults.NotFound("x"));
        Assert.IsType<BadRequestObjectResult>(ApiResults.BadRequest("x"));
        Assert.IsType<ConflictObjectResult>(ApiResults.Conflict("x"));
        Assert.IsType<UnauthorizedObjectResult>(ApiResults.Unauthorized("x"));
    }

    [Fact]
    public void Validation_error_keeps_details_and_is_problem_json()
    {
        var r = ApiResults.ValidationError(new Dictionary<string, string> { ["name"] = "Required." });
        // `details` (the field→error map the dashboard reads) is retained as an extra key.
        AssertProblem(r, 400, "validation_error", "One or more fields are invalid.", "details");
        var root = Wire(Assert.IsType<BadRequestObjectResult>(r).Value);
        Assert.Equal("Required.", root.GetProperty("details").GetProperty("name").GetString());
    }

    [Fact]
    public void Instance_carries_the_request_correlation_id()
    {
        RequestCorrelation.Current = "corr-abc-123";
        try
        {
            Assert.Equal("corr-abc-123", Wire(Assert.IsType<NotFoundObjectResult>(ApiResults.NotFound("x")).Value).GetProperty("instance").GetString());
        }
        finally
        {
            RequestCorrelation.Current = null;
        }
        // Outside a request (no middleware), instance is present-but-null — never throws, RFC-valid.
        Assert.Equal(JsonValueKind.Null, Wire(Assert.IsType<NotFoundObjectResult>(ApiResults.NotFound("x")).Value).GetProperty("instance").ValueKind);
    }
}
