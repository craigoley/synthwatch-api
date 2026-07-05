using Azure;
using Azure.Communication.Email;
using Azure.Core;
using Microsoft.Extensions.Logging;

namespace SynthWatch.Api.Infrastructure;

/// <summary>Sends a transactional email (OTP login codes, access-request notices) as multipart/alternative
/// — an HTML body plus a plaintext fallback so it reads in any client. Injected so tests substitute a fake
/// (the auth flow is exercised without a live ACS send).</summary>
public interface IEmailSender
{
    Task SendAsync(string recipient, string subject, string plainText, string html, CancellationToken ct);
}

/// <summary>
/// ACS email transport for the API. The runner sends alerts via a connection string; the API prefers
/// MANAGED IDENTITY — <c>EmailClient(endpoint, DefaultAzureCredential)</c> against <c>ACS_EMAIL_ENDPOINT</c>
/// — so no ACS access key is stored on the Function App (consistent with how it does Postgres/blob/ARM).
/// Falls back to <c>ACS_EMAIL_CONNECTION_STRING</c> if MI send is blocked. Sender = <c>AUTH_EMAIL_FROM</c>.
///
/// ★ RBAC: the API's MI needs the "Communication and Email Service Owner" role (GUID
/// 09976791-48a7-449e-bb21-39d1a415f350) on the ACS resource for MI send — auto-assigned by bicep
/// (acsEmailOwnerAssignment in infra/main.bicep). Falls back to the connection string if MI send is blocked.
/// </summary>
public sealed class AcsEmailSender : IEmailSender
{
    private readonly TokenCredential _credential;
    private readonly ILogger<AcsEmailSender> _logger;

    public AcsEmailSender(TokenCredential credential, ILogger<AcsEmailSender> logger)
    {
        _credential = credential;
        _logger = logger;
    }

    public async Task SendAsync(string recipient, string subject, string plainText, string html, CancellationToken ct)
    {
        var from = Environment.GetEnvironmentVariable("AUTH_EMAIL_FROM");
        var endpoint = Environment.GetEnvironmentVariable("ACS_EMAIL_ENDPOINT");
        var connectionString = Environment.GetEnvironmentVariable("ACS_EMAIL_CONNECTION_STRING");

        if (string.IsNullOrWhiteSpace(from))
            throw new InvalidOperationException("AUTH_EMAIL_FROM is not configured.");

        EmailClient client =
            !string.IsNullOrWhiteSpace(endpoint) ? new EmailClient(new Uri(endpoint), _credential) // MI (preferred)
            : !string.IsNullOrWhiteSpace(connectionString) ? new EmailClient(connectionString)      // fallback
            : throw new InvalidOperationException("ACS email transport is not configured (set ACS_EMAIL_ENDPOINT or ACS_EMAIL_CONNECTION_STRING).");

        // multipart/alternative: HTML for rich clients, PlainText fallback for the rest.
        var message = new EmailMessage(from, recipient, new EmailContent(subject) { PlainText = plainText, Html = html });
        // WaitUntil.Started: return once ACS accepts the message — a login code shouldn't wait on delivery.
        await client.SendAsync(WaitUntil.Started, message, ct);
        EmailLog.Sent(_logger, subject);
    }
}

internal static partial class EmailLog
{
    [LoggerMessage(EventId = 5000, Level = LogLevel.Information, Message = "Auth email accepted by ACS (subject {Subject})")]
    public static partial void Sent(ILogger logger, string subject);
}
