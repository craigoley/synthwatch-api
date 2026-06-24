using System.Text.Json;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Builds the CLEARLY-MARKED test payload for POST /api/channels/{id}/test. Intentionally a fixed
/// "[TEST]" message — NOT the runner's real alert format — so verifying a channel never looks like a
/// real incident and the API doesn't have to track the runner's alert content.
/// </summary>
public static class ChannelTestMessage
{
    public static (string Subject, string Body) Build(string channelName)
    {
        var subject = $"[SynthWatch][TEST] Test alert for channel \"{channelName}\"";
        var body =
            $"This is a TEST alert from SynthWatch to verify the \"{channelName}\" channel delivers.\n\n" +
            "No incident was created and nothing is wrong — if you received this, the channel works.";
        return (subject, body);
    }

    /// <summary>Webhook test body — mirrors the runner's dispatch payload shape, marked as a test.</summary>
    public static string WebhookPayload(string channelName) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["event"] = "test",
        ["severity"] = "test",
        ["checkName"] = $"[TEST] channel \"{channelName}\"",
        ["summary"] = "SynthWatch test alert — verifying this channel delivers. No incident; nothing is wrong.",
        ["runId"] = null,
        ["failedStep"] = null,
        ["screenshotUrl"] = null,
        ["dashboardUrl"] = null,
    });
}
