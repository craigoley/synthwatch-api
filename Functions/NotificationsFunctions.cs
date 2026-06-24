using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using SynthWatch.Api.Data;
using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Dtos;

namespace SynthWatch.Api.Functions;

/// <summary>
/// GET /api/notifications/health — alerting deliverability readiness.
///
/// The API reports ONLY what it can actually verify (the recurring "don't let the banner lie" rule):
///   - channelsConfigured: ≥1 ENABLED channel with a real target (email: a recipient; webhook: a URL) — DB-visible;
///   - routingConfigured: ≥1 routing rule (alert_routes OR tag_routes) — DB-visible;
///   - transportConfigured: the ACS email transport (ACS_EMAIL_CONNECTION_STRING + ALERT_EMAIL_FROM)
///     lives in RUNNER env, NOT this Function App. So it is reported true ONLY if those vars happen to
///     be present here, otherwise null = UNKNOWN. The API never returns false for a transport it can't
///     see (absence here says nothing about the runner).
/// </summary>
public class NotificationsFunctions
{
    private readonly SynthWatchDbContext _db;

    public NotificationsFunctions(SynthWatchDbContext db) => _db = db;

    [Function("NotificationsReadiness")]
    public async Task<IActionResult> Readiness(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "notifications/health")] HttpRequest req,
        CancellationToken ct)
    {
        // Config readiness — DB state the API owns. Channels are few; evaluate deliverability in memory
        // (the jsonb config doesn't translate cleanly to SQL predicates).
        var enabled = await _db.Channels.AsNoTracking().Where(c => c.Enabled).ToListAsync(ct);
        var channelsConfigured = enabled.Any(Deliverable);

        var routingConfigured = await _db.AlertRoutes.AnyAsync(ct) || await _db.TagRoutes.AnyAsync(ct);

        // Transport readiness — knowable only if the ACS env is on the API too; otherwise UNKNOWN (null).
        var hasConn = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ACS_EMAIL_CONNECTION_STRING"));
        var hasFrom = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ALERT_EMAIL_FROM"));
        bool? transportConfigured = hasConn && hasFrom ? true : null;

        var detail = transportConfigured == true
            ? "Email transport (ACS) is configured."
            : "Email transport (ACS) is configured in runner infrastructure; the API can't verify it from here.";

        return new OkObjectResult(new NotificationsReadinessDto(
            channelsConfigured, routingConfigured, transportConfigured, detail));
    }

    /// <summary>A channel can actually deliver if it has a target: email → ≥1 recipient; webhook → a URL.</summary>
    private static bool Deliverable(Channel c) => c.Type switch
    {
        "email" => c.Config.To is { Count: > 0 } && c.Config.To.All(t => !string.IsNullOrWhiteSpace(t)),
        "webhook" => !string.IsNullOrWhiteSpace(c.Config.Url),
        _ => false,
    };
}
