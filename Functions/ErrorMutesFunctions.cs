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
/// Error-mute CRUD (error-diff P4): an operator MUTES a known/accepted NEW error fingerprint for a check, and
/// the error-diff read (<see cref="ErrorDiffFunctions"/>) then moves that fingerprint out of <c>new[]</c> into
/// its <c>muted[]</c> bucket (never silently dropped). Mute is PER-MONITOR and persists until unmuted.
///
/// error_mutes is runner-owned (migration 0076) DASHBOARD config, like check_tags/env_domain_map — inert text
/// used only to filter a read, NOT RCE-sensitive. GET is session-gated (it echoes the same fingerprints as the
/// forensic error-diff read); POST/DELETE are gated + audited by the <see cref="AuthorizationMiddleware"/>
/// (fail-closed by verb — a new write endpoint is protected the moment it exists). No UPDATE: a note is set at
/// mute time, unmute + re-mute to change it (INSERT + DELETE is the whole write surface).
/// </summary>
public class ErrorMutesFunctions
{
    private readonly SynthWatchDbContext _db;
    private readonly IAuthPrincipal? _auth;

    // _auth optional for test convenience only — DI always injects it; the read gate is inert flag-off.
    public ErrorMutesFunctions(SynthWatchDbContext db, IAuthPrincipal? auth = null)
    {
        _db = db;
        _auth = auth;
    }

    /// <summary>GET /api/checks/{id}/error-mutes — every mute for the check, newest first. Session-gated (same
    /// forensic posture as the error-diff read it feeds).</summary>
    [Function("GetCheckErrorMutes")]
    public async Task<IActionResult> GetCheckErrorMutes(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "checks/{id:long}/error-mutes")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        if (await SessionReadGate.RequireSessionAsync(_auth, req, ct) is { } denied) return denied;

        var mutes = await _db.ErrorMutes.AsNoTracking()
            .Where(m => m.CheckId == id)
            .OrderByDescending(m => m.MutedAt)
            .Select(m => new ErrorMuteDto(m.Fingerprint, m.MutedAt, m.MutedBy, m.Note))
            .ToListAsync(ct);

        // Not forensic-cacheable (echoes fingerprints); mirror the error-diff read's no-store.
        req.HttpContext.Response.Headers.CacheControl = "no-store";
        return ApiResults.Ok(new ErrorMutesResponse(mutes));
    }

    /// <summary>POST /api/checks/{id}/error-mutes — mute a fingerprint. Idempotent: muting an already-muted
    /// fingerprint is a no-op that returns the existing mute (200), never a UNIQUE-violation 500. Gated + audited.</summary>
    [Function("MuteCheckError")]
    public async Task<IActionResult> MuteCheckError(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "checks/{id:long}/error-mutes")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        var (body, bodyError) = await RequestJson.ReadAsync<MuteErrorRequest>(req, ct);
        if (bodyError is not null) return bodyError;

        var fingerprint = (body?.Fingerprint ?? "").Trim();
        if (fingerprint.Length == 0) return ApiResults.BadRequest("fingerprint is required.");
        var note = string.IsNullOrWhiteSpace(body!.Note) ? null : body.Note!.Trim();

        // FK would 500 on a bad check id — a friendly 404 instead.
        if (!await _db.Checks.AnyAsync(c => c.Id == id, ct))
            return ApiResults.NotFound($"check {id} not found.");

        // Idempotent: already muted → echo it (no duplicate, no 500 on the UNIQUE(check_id, fingerprint)).
        var existing = await _db.ErrorMutes.AsNoTracking()
            .FirstOrDefaultAsync(m => m.CheckId == id && m.Fingerprint == fingerprint, ct);
        if (existing is not null)
            return ApiResults.Ok(new ErrorMuteDto(existing.Fingerprint, existing.MutedAt, existing.MutedBy, existing.Note));

        var who = (req.HttpContext.Items.TryGetValue("principal", out var p) ? p as Principal : null)?.Email;
        var row = new ErrorMuteRow { CheckId = id, Fingerprint = fingerprint, MutedBy = who, Note = note };
        _db.ErrorMutes.Add(row);
        await _db.SaveChangesAsync(ct);

        return ApiResults.Created($"/api/checks/{id}/error-mutes/{Uri.EscapeDataString(fingerprint)}",
            new ErrorMuteDto(row.Fingerprint, row.MutedAt, row.MutedBy, row.Note));
    }

    /// <summary>DELETE /api/checks/{id}/error-mutes?fingerprint=&lt;encoded&gt; — unmute. The fingerprint is a
    /// QUERY param, not a path segment: a network fingerprint embeds a canonical URL (with '/' and ':'), which
    /// can't be a single path segment — the query string is unambiguous + host-portable. Gated + audited;
    /// idempotent (unmuting a non-mute → 204, not 404 — the end state is what was asked for).</summary>
    [Function("UnmuteCheckError")]
    public async Task<IActionResult> UnmuteCheckError(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "checks/{id:long}/error-mutes")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        var fingerprint = ((string?)req.Query["fingerprint"] ?? "").Trim();
        if (fingerprint.Length == 0) return ApiResults.BadRequest("fingerprint query parameter is required.");

        var row = await _db.ErrorMutes.FirstOrDefaultAsync(m => m.CheckId == id && m.Fingerprint == fingerprint, ct);
        if (row is not null)
        {
            _db.ErrorMutes.Remove(row);
            await _db.SaveChangesAsync(ct);
        }
        return ApiResults.NoContent(); // idempotent — already-unmuted is the same end state
    }
}
