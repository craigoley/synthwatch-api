using SynthWatch.Api.Dtos;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// The transport seam for channel test-sends (path (c)): send a built test message through one channel's
/// transport. Behind an interface so the handler is unit-testable without touching ACS / a real webhook.
/// </summary>
public interface IChannelTestTransport
{
    Task<ChannelTestResult> SendEmailAsync(IReadOnlyList<string> recipients, string subject, string body, CancellationToken ct);
    Task<ChannelTestResult> SendWebhookAsync(string url, string? authHeader, string jsonPayload, CancellationToken ct);
}
