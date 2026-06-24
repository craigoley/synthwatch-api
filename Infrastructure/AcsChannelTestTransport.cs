using System.Text;
using Azure;
using Azure.Communication.Email;
using Microsoft.Extensions.Configuration;
using SynthWatch.Api.Dtos;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Real channel-test transport (path (c)). Sends the test via the SAME transport the runner's dispatch
/// uses — ACS for email (reading the SAME env: ACS_EMAIL_CONNECTION_STRING / ALERT_EMAIL_FROM), an HTTP
/// POST for webhook — but for ONE known channel with a fixed [TEST] message. It does NOT replicate the
/// dispatch RESOLUTION (severity/tag/per-check union — the runner owns that), so the only shared surface
/// is the transport call + env (low drift). Email is gated on the env being present; if it is not (it is
/// NOT on the API Function App today), the test fails gracefully with a clear "not configured" reason.
/// </summary>
public sealed class AcsChannelTestTransport : IChannelTestTransport
{
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpFactory;

    public AcsChannelTestTransport(IConfiguration config, IHttpClientFactory httpFactory)
    {
        _config = config;
        _httpFactory = httpFactory;
    }

    public async Task<ChannelTestResult> SendEmailAsync(IReadOnlyList<string> recipients, string subject, string body, CancellationToken ct)
    {
        var connectionString = _config["ACS_EMAIL_CONNECTION_STRING"];
        var from = _config["ALERT_EMAIL_FROM"];
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(from))
            return new ChannelTestResult(false,
                "email transport not configured — ACS_EMAIL_CONNECTION_STRING / ALERT_EMAIL_FROM are not set on the API.");

        try
        {
            var client = new EmailClient(connectionString);
            var message = new EmailMessage(
                senderAddress: from,
                content: new EmailContent(subject) { PlainText = body },
                recipients: new EmailRecipients(recipients.Select(r => new EmailAddress(r.Trim())).ToList()));
            // WaitUntil.Started: confirm ACS ACCEPTED the send (the transport works) without polling to
            // delivery — enough for a test, and bounded so a hung ACS can't stall the request.
            EmailSendOperation op = await client.SendAsync(WaitUntil.Started, message, ct);
            return new ChannelTestResult(true, $"Test email accepted by ACS for {recipients.Count} recipient(s) (operation {op.Id}).");
        }
        catch (Exception ex)
        {
            return new ChannelTestResult(false, $"ACS send failed: {ex.Message}");
        }
    }

    public async Task<ChannelTestResult> SendWebhookAsync(string url, string? authHeader, string jsonPayload, CancellationToken ct)
    {
        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10); // a hung webhook must not stall the request
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(authHeader))
                request.Headers.TryAddWithoutValidation("Authorization", authHeader);
            using var res = await http.SendAsync(request, ct);
            var code = (int)res.StatusCode;
            return res.IsSuccessStatusCode
                ? new ChannelTestResult(true, $"Webhook responded {code}.")
                : new ChannelTestResult(false, $"Webhook responded {code}.");
        }
        catch (Exception ex)
        {
            return new ChannelTestResult(false, $"Webhook POST failed: {ex.Message}");
        }
    }
}
