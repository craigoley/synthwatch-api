using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using SynthWatch.Api.Data;
using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;

namespace SynthWatch.Api.Functions;

/// <summary>
/// CRUD for alerting delivery channels (runner-owned `channels`, migration 0023 / #81). config holds
/// delivery targets only — write-validation rejects transport secrets (those stay in runner env).
/// </summary>
public class ChannelsFunctions
{
    private readonly SynthWatchDbContext _db;

    public ChannelsFunctions(SynthWatchDbContext db) => _db = db;

    /// <summary>GET /api/channels — all channels.</summary>
    [Function("GetChannels")]
    public async Task<IActionResult> GetChannels(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "channels")] HttpRequest req,
        CancellationToken ct)
    {
        var channels = await _db.Channels.AsNoTracking()
            .OrderBy(c => c.Id)
            .ToListAsync(ct);
        return ApiResults.Ok(channels.Select(ChannelDto.From).ToList());
    }

    /// <summary>POST /api/channels — create a channel (validates type + per-type config, no secrets).</summary>
    [Function("CreateChannel")]
    public async Task<IActionResult> CreateChannel(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "channels")] HttpRequest req,
        CancellationToken ct)
    {
        var (body, parseError) = await ReadBodyAsync(req, ct);
        if (parseError is not null) return parseError;

        var error = AlertingValidation.ValidateChannel(body!.Name, body.Type, body.Config);
        if (error is not null) return ApiResults.BadRequest(error);

        var channel = new Channel
        {
            Name = body.Name!.Trim(),
            Type = body.Type!,
            Config = body.Config ?? new ChannelConfig(),
            Enabled = body.Enabled ?? true,
        };
        _db.Channels.Add(channel);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) // channels_name_key unique violation
        {
            return ApiResults.BadRequest($"a channel named '{channel.Name}' already exists.");
        }
        return ApiResults.Created($"/api/channels/{channel.Id}", ChannelDto.From(channel));
    }

    /// <summary>PUT /api/channels/{id} — replace a channel's mutable fields (same validation as create).</summary>
    [Function("UpdateChannel")]
    public async Task<IActionResult> UpdateChannel(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "channels/{id:long}")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        var (body, parseError) = await ReadBodyAsync(req, ct);
        if (parseError is not null) return parseError;

        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (channel is null) return ApiResults.NotFound($"Channel {id} not found.");

        var error = AlertingValidation.ValidateChannel(body!.Name, body.Type, body.Config);
        if (error is not null) return ApiResults.BadRequest(error);

        channel.Name = body.Name!.Trim();
        channel.Type = body.Type!;
        channel.Config = body.Config ?? new ChannelConfig();
        if (body.Enabled is not null) channel.Enabled = body.Enabled.Value;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return ApiResults.BadRequest($"a channel named '{channel.Name}' already exists.");
        }
        return ApiResults.Ok(ChannelDto.From(channel));
    }

    /// <summary>
    /// DELETE /api/channels/{id} — delete a channel. GUARD: if any routing rule references it, return
    /// 409 (blocking is safer for v1 — the operator must remove it from routing first). NOTE: the DB FK
    /// is ON DELETE CASCADE (#81), so absent this guard a delete would silently drop its routes; the API
    /// enforces the block instead.
    /// </summary>
    [Function("DeleteChannel")]
    public async Task<IActionResult> DeleteChannel(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "channels/{id:long}")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (channel is null) return ApiResults.NotFound($"Channel {id} not found.");

        var refs = await _db.AlertRoutes.CountAsync(r => r.ChannelId == id, ct);
        if (refs > 0)
            return new ConflictObjectResult(new
            {
                error = "conflict",
                message = $"channel {id} is referenced by {refs} routing rule(s); remove it from routing before deleting.",
            });

        _db.Channels.Remove(channel);
        await _db.SaveChangesAsync(ct);
        return new NoContentResult();
    }

    private static async Task<(ChannelWriteRequest?, IActionResult?)> ReadBodyAsync(HttpRequest req, CancellationToken ct)
    {
        ChannelWriteRequest? body;
        try
        {
            body = await req.ReadFromJsonAsync<ChannelWriteRequest>(ct);
        }
        catch (JsonException)
        {
            return (null, ApiResults.BadRequest("Request body is not valid JSON."));
        }
        return body is null
            ? (null, ApiResults.BadRequest("Request body is required."))
            : (body, null);
    }
}
