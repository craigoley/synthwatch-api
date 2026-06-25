using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>Redaction is mandatory: the immutable audit trail must NEVER store a plaintext secret. These
/// assert that against a known secret-bearing channel payload (webhook URL token, bearer header, emails).</summary>
public class AuditRedactionTests
{
    private const string WebhookSecret = "B0000000/SUPERSECRETtoken123";
    private const string BearerSecret = "sk-live-DEADBEEFsupersecret";

    private static ChannelDto SecretChannel() => new(
        Id: 7,
        Name: "ops-webhook",
        Type: "webhook",
        Config: new ChannelConfig
        {
            To = new() { "alice@example.com", "bob@corp.test" },
            Url = $"https://hooks.slack.test/services/T000/{WebhookSecret}",
            AuthHeader = $"Bearer {BearerSecret}",
        },
        Enabled: true);

    [Fact]
    public void Channel_redaction_removes_every_plaintext_secret()
    {
        var json = AuditRedaction.RedactToJson(SecretChannel());
        Assert.NotNull(json);

        // ★ The load-bearing assertion: no secret material survives anywhere in the stored JSON.
        Assert.DoesNotContain(WebhookSecret, json, StringComparison.Ordinal);
        Assert.DoesNotContain(BearerSecret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("alice@example.com", json, StringComparison.Ordinal);
        Assert.DoesNotContain("bob@corp.test", json, StringComparison.Ordinal);

        // Secrets become a stable, irreversible fingerprint; emails are masked.
        Assert.Contains("redacted:sha256:", json, StringComparison.Ordinal);
        Assert.Contains("a***@e***", json, StringComparison.Ordinal);
        Assert.Contains("b***@c***", json, StringComparison.Ordinal);

        // Non-secret fields are preserved (the audit row is still useful).
        Assert.Contains("ops-webhook", json, StringComparison.Ordinal);
        Assert.Contains("webhook", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Connection_string_value_is_fingerprinted_by_pattern_regardless_of_key()
    {
        // A secret hiding under an innocuous key still gets caught by the value-pattern rule.
        var leak = new { note = "endpoint=https://x.communication.azure.com/;accesskey=Zm9vYmFyYmF6==" };
        var json = AuditRedaction.RedactToJson(leak);
        Assert.NotNull(json);
        Assert.DoesNotContain("accesskey=Zm9vYmFyYmF6", json, StringComparison.Ordinal);
        Assert.Contains("redacted:sha256:", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_object_redacts_to_null()
    {
        Assert.Null(AuditRedaction.RedactToJson(null));
    }

    [Fact]
    public void AuditWriter_builds_the_envelope_and_redacts_the_after_diff()
    {
        var principal = new Principal("admin@synth.test", Roles.Admin);
        var diff = new AuditDiff("channel", "7", Before: null, After: SecretChannel(), Note: null);

        var row = AuditWriter.BuildRow(principal, "1.2.3.4", "POST", "/api/channels", statusCode: 201, success: true, diff);

        Assert.Equal("admin@synth.test", row.ActorEmail);
        Assert.Equal("1.2.3.4", row.ActorIp);
        Assert.Equal("create", row.Action);           // POST → create
        Assert.Equal("channel", row.TargetType);
        Assert.Equal("7", row.TargetId);
        Assert.Equal("POST", row.HttpMethod);
        Assert.Equal("/api/channels", row.HttpPath);
        Assert.Equal(201, row.StatusCode);
        Assert.True(row.Success);
        Assert.Null(row.BeforeJson);
        Assert.NotNull(row.AfterJson);
        // ★ The diff stored on the row is redacted — no plaintext secret reaches the DB.
        Assert.DoesNotContain(WebhookSecret, row.AfterJson, StringComparison.Ordinal);
        Assert.DoesNotContain(BearerSecret, row.AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Coarse_target_is_derived_from_the_route_when_no_diff()
    {
        var row = AuditWriter.BuildRow(
            new Principal("e@x.test", Roles.Editor), null, "DELETE", "/api/checks/42", 204, true, diff: null);
        Assert.Equal("delete", row.Action);
        Assert.Equal("checks", row.TargetType);        // first route segment
        Assert.Equal("42", row.TargetId);              // first numeric segment
        Assert.Null(row.AfterJson);
    }
}
