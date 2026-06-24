using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using SynthWatch.Api.Data;
using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;

namespace SynthWatch.Api.Functions;

/// <summary>
/// POST /api/channels/{id}/test — send a clearly-marked TEST alert through a channel so an operator can
/// verify it delivers, WITHOUT creating an incident or touching routing/history. Pure channel test.
/// Sends via the same transport the runner uses (path (c)); returns { ok, detail } so the dashboard
/// shows delivered / the failure reason.
/// </summary>
public class ChannelTestFunctions
{
    private readonly SynthWatchDbContext _db;
    private readonly IChannelTestTransport _transport;

    public ChannelTestFunctions(SynthWatchDbContext db, IChannelTestTransport transport)
    {
        _db = db;
        _transport = transport;
    }

    [Function("TestChannel")]
    public async Task<IActionResult> TestChannel(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "channels/{id:long}/test")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        var channel = await _db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (channel is null) return ApiResults.NotFound($"Channel {id} not found.");

        var (subject, body) = ChannelTestMessage.Build(channel.Name);
        var result = channel.Type switch
        {
            "email" => channel.Config.To is { Count: > 0 } to
                ? await _transport.SendEmailAsync(to, subject, body, ct)
                : new ChannelTestResult(false, "channel has no recipients (config.to is empty)."),
            "webhook" => !string.IsNullOrWhiteSpace(channel.Config.Url)
                ? await _transport.SendWebhookAsync(channel.Config.Url, channel.Config.AuthHeader, ChannelTestMessage.WebhookPayload(channel.Name), ct)
                : new ChannelTestResult(false, "channel has no webhook url."),
            _ => new ChannelTestResult(false, $"unknown channel type '{channel.Type}'."),
        };

        // Pure channel test: no incident, no routing, no history touched. Always 200 with { ok, detail }
        // for a known channel (the body carries success/failure); 404 only for an unknown channel id.
        return ApiResults.Ok(result);
    }
}
