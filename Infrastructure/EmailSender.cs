using Azure;
using Azure.Communication.Email;
using Azure.Core;
using Microsoft.Extensions.Logging;

namespace SynthWatch.Api.Infrastructure;

/// <summary>Sends a plain-text transactional email (OTP login codes, access-request notices). Injected so
/// tests substitute a fake — the auth flow is exercised without a live ACS send.</summary>
public interface IEmailSender
{
    Task SendAsync(string recipient, string subject, string body, CancellationToken ct);
}

/// <summary>
/// ACS email transport for the API. The runner sends alerts via a connection string; the API prefers
/// MANAGED IDENTITY — <c>EmailClient(endpoint, DefaultAzureCredential)</c> against <c>ACS_EMAIL_ENDPOINT</c>
/// — so no ACS access key is stored on the Function App (consistent with how it does Postgres/blob/ARM).
/// Falls back to <c>ACS_EMAIL_CONNECTION_STRING</c> if MI send is blocked. Sender = <c>AUTH_EMAIL_FROM</c>.
///
/// ★ Deploy prerequisite: the API's MI needs the "Communication Services Contributor" role on the ACS
/// resource for MI send (a manual role assignment — see the PR). Until then, set the connection string.
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

    public async Task SendAsync(string recipient, string subject, string body, CancellationToken ct)
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

        var message = new EmailMessage(from, recipient, new EmailContent(subject) { PlainText = body });
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
