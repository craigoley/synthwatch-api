using System.Text.Json;
using Npgsql;
using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>
/// The two observability fixes (no DB/HTTP): (B) the RFC 9457 problem+json body keeps the legacy error/message
/// the dashboard reads AND carries the correlation id in `instance`; (A) the denial audit row is built right and
/// the shared write NEVER throws (so an audit failure can't turn a 401/403 into a 500).
/// </summary>
public class ProblemResultsAndDenialAuditTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static JsonElement Wire(object body) =>
        JsonDocument.Parse(JsonSerializer.Serialize(body, Web)).RootElement.Clone();

    // ── B: RFC 9457 + backward-compatible legacy fields ──
    [Fact]
    public void Problem_body_is_rfc9457_plus_the_legacy_fields_the_dashboard_reads()
    {
        var root = Wire(ProblemResults.Body(500, "Internal Server Error", "An unexpected error occurred.", "inv-123", "internal_error"));
        // RFC 9457 members
        Assert.Equal("about:blank", root.GetProperty("type").GetString());
        Assert.Equal("Internal Server Error", root.GetProperty("title").GetString());
        Assert.Equal(500, root.GetProperty("status").GetInt32());
        Assert.Equal("An unexpected error occurred.", root.GetProperty("detail").GetString());
        Assert.Equal("inv-123", root.GetProperty("instance").GetString());                 // ★ the correlation id
        // legacy extension members — the dashboard's error path reads body.error / body.message
        Assert.Equal("internal_error", root.GetProperty("error").GetString());
        Assert.Equal("An unexpected error occurred.", root.GetProperty("message").GetString()); // == detail
        Assert.Equal("application/problem+json", ProblemResults.ContentType);
    }

    [Fact]
    public void Problem_body_writes_a_null_instance_key_when_none_is_available()
    {
        var root = Wire(ProblemResults.Body(400, "Bad Request", "bad", null, "bad_request"));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("instance").ValueKind); // key present, value null
    }

    // ── A: denial-row build ──
    [Fact]
    public void Denial_row_for_a_401_has_a_null_actor()
    {
        var row = AuditWriter.BuildDenialRow(actorEmail: null, ip: "9.9.9.9", method: "post", rawPath: "/api/checks", statusCode: 401);
        Assert.Null(row.ActorEmail);                 // 401 = no valid session
        Assert.Equal("auth.denied", row.Action);
        Assert.Equal("POST", row.HttpMethod);
        Assert.Equal(401, row.StatusCode);
        Assert.False(row.Success);
        Assert.Equal("9.9.9.9", row.ActorIp);
        Assert.Contains("unauthenticated", row.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public void Denial_row_for_a_403_records_the_actor()
    {
        var row = AuditWriter.BuildDenialRow(actorEmail: "ed@t.test", ip: null, method: "DELETE", rawPath: "/api/editors/x@t.test", statusCode: 403);
        Assert.Equal("ed@t.test", row.ActorEmail);   // 403 = valid session, insufficient role
        Assert.Equal("auth.denied", row.Action);
        Assert.Equal(403, row.StatusCode);
        Assert.False(row.Success);
        Assert.Contains("insufficient role", row.Note!, StringComparison.Ordinal);
    }

    // ── ★ A guardrail: an audit-write failure is SWALLOWED and NEVER thrown ──
    [Fact]
    public async Task TryPersist_swallows_a_write_failure_so_a_denial_never_becomes_a_500()
    {
        await using var badDs = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=nope;Username=x;Password=y;Timeout=1;Command Timeout=1");
        var row = AuditWriter.BuildDenialRow("x@t.test", "1.1.1.1", "POST", "/api/checks", 403);
        Exception? captured = null;

        var ok = await AuditWriter.TryPersistAsync(badDs, row, ex => captured = ex); // must not throw

        Assert.False(ok);          // the write failed…
        Assert.NotNull(captured);  // …was surfaced to the logger callback (not lost)…
        // …and we reached here WITHOUT an exception → the 401/403 response is unaffected.
    }
}
