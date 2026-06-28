using System.Net.Mail;
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
/// Phase 12 slice 3 — editor (user) management. ADMIN-ONLY: an admin lists / adds / removes the editor
/// allowlist (the editors table). Admins themselves come from the ADMIN_EMAILS app setting (env-based,
/// never in this table) — so removing editors can NEVER lock out an admin.
///
/// ★ Self-guarding, independent of AUTH_ENFORCEMENT_ENABLED. The AuthorizationMiddleware admin-gates the
/// MUTATING /editors routes only when the flag is ON; this handler re-checks admin on EVERY verb (incl. the
/// GET list, which the verb-based gate doesn't cover) so user-management is never open — even with the flag
/// off during the slice-3 build. Defense in depth: the gate and the handler must BOTH say admin.
/// </summary>
public class EditorsFunctions
{
    private readonly SynthWatchDbContext _db;
    private readonly IAuthPrincipal _auth;
    private readonly IAuditScope _audit;

    public EditorsFunctions(SynthWatchDbContext db, IAuthPrincipal auth, IAuditScope audit)
    {
        _db = db;
        _auth = auth;
        _audit = audit;
    }

    /// <summary>Resolve the bearer caller and require admin. Returns the admin Principal, or sets
    /// <paramref name="deny"/> to a 401 (no/invalid session) / 403 (valid session, not admin) — matching
    /// the middleware's shapes exactly.</summary>
    private async Task<Principal?> RequireAdminAsync(HttpRequest req, Action<IActionResult> deny, CancellationToken ct)
    {
        var principal = await _auth.FromBearerAsync(req.Headers.Authorization, ct);
        if (principal is null)
        {
            deny(ApiResults.Unauthorized("Authentication required."));
            return null;
        }
        if (!principal.IsAdmin)
        {
            deny(ApiResults.Forbidden("You do not have permission to perform this action."));
            return null;
        }
        return principal;
    }

    /// <summary>GET /api/editors — the editor allowlist (admin-only).</summary>
    [Function("ListEditors")]
    public async Task<IActionResult> ListEditors(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "editors")] HttpRequest req,
        CancellationToken ct)
    {
        IActionResult? deny = null;
        if (await RequireAdminAsync(req, d => deny = d, ct) is null) return deny!;

        var editors = await _db.Editors.AsNoTracking()
            .OrderBy(e => e.Email)
            .Select(e => new EditorDto(e.Email, e.AddedBy, e.AddedAt))
            .ToListAsync(ct);
        return ApiResults.Ok(editors);
    }

    /// <summary>POST /api/editors { email } — add an editor (admin-only). Audited by the middleware when
    /// enforcement is on; this handler also records the rich diff. 409 if already an editor.</summary>
    [Function("AddEditor")]
    public async Task<IActionResult> AddEditor(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "editors")] HttpRequest req,
        CancellationToken ct)
    {
        IActionResult? deny = null;
        var admin = await RequireAdminAsync(req, d => deny = d, ct);
        if (admin is null) return deny!;

        AddEditorRequest? body;
        try { body = await req.ReadFromJsonAsync<AddEditorRequest>(ct); }
        catch (JsonException) { return ApiResults.BadRequest("Request body is not valid JSON."); }

        if (!TryReadEmail(body?.Email, out var email))
            return ApiResults.BadRequest("A valid email is required.");

        if (await _db.Editors.AnyAsync(e => e.Email == email, ct))
            return ApiResults.Conflict($"{email} is already an editor.");

        var editor = new Editor { Email = email, AddedBy = admin.Email };
        _db.Editors.Add(editor);
        await _db.SaveChangesAsync(ct);

        var dto = new EditorDto(email, admin.Email, DateTimeOffset.UtcNow);
        _audit.Record("editor", email, before: null, after: dto, note: "add editor");
        return ApiResults.Created($"/api/editors/{email}", dto);
    }

    /// <summary>DELETE /api/editors/{email} — remove an editor (admin-only). 404 if not an editor. Removing
    /// an editor revokes their write access on their NEXT request (role is re-resolved live). Can't lock out
    /// an admin: admins are env-based (ADMIN_EMAILS), never rows here.</summary>
    [Function("RemoveEditor")]
    public async Task<IActionResult> RemoveEditor(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "editors/{email}")] HttpRequest req,
        string email,
        CancellationToken ct)
    {
        IActionResult? deny = null;
        var admin = await RequireAdminAsync(req, d => deny = d, ct);
        if (admin is null) return deny!;

        var normalized = AuthTokens.NormalizeEmail(email);
        var editor = await _db.Editors.FirstOrDefaultAsync(e => e.Email == normalized, ct);
        if (editor is null)
            return ApiResults.NotFound($"{normalized} is not an editor.");

        _db.Editors.Remove(editor);
        await _db.SaveChangesAsync(ct);

        _audit.Record("editor", normalized,
            before: new EditorDto(editor.Email, editor.AddedBy, editor.AddedAt), after: null, note: "remove editor");
        return ApiResults.NoContent();
    }

    /// <summary>GET /api/access-requests — pending "request edit access" entries (admin-only), newest first,
    /// EXCLUDING emails that are already editors/admins (they don't need access). One row per email with the
    /// latest request time + how many times they asked — so an admin can act (add them as an editor).</summary>
    [Function("ListAccessRequests")]
    public async Task<IActionResult> ListAccessRequests(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "access-requests")] HttpRequest req,
        CancellationToken ct)
    {
        IActionResult? deny = null;
        if (await RequireAdminAsync(req, d => deny = d, ct) is null) return deny!;

        var editors = await _db.Editors.AsNoTracking().Select(e => e.Email).ToListAsync(ct);
        var exclude = new HashSet<string>(editors, StringComparer.Ordinal);
        foreach (var a in AuthPrincipalService.AdminEmails()) exclude.Add(a);

        var grouped = (await _db.AccessRequests.AsNoTracking().ToListAsync(ct))
            .Where(a => !exclude.Contains(a.Email))
            .GroupBy(a => a.Email)
            .Select(g => new AccessRequestDto(g.Key, g.Max(a => a.RequestedAt), g.Count()))
            .OrderByDescending(d => d.RequestedAt)
            .ToList();
        return ApiResults.Ok(grouped);
    }

    /// <summary>DELETE /api/access-requests/{email} — dismiss/deny a pending access request (admin-only).
    /// Idempotent: returns 204 even if the email had no pending request.</summary>
    [Function("DismissAccessRequest")]
    public async Task<IActionResult> DismissAccessRequest(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "access-requests/{email}")] HttpRequest req,
        string email,
        CancellationToken ct)
    {
        IActionResult? deny = null;
        if (await RequireAdminAsync(req, d => deny = d, ct) is null) return deny!;

        var normalized = AuthTokens.NormalizeEmail(email);
        var rows = await _db.AccessRequests.Where(a => a.Email == normalized).ToListAsync(ct);
        if (rows.Count > 0)
        {
            _db.AccessRequests.RemoveRange(rows);
            await _db.SaveChangesAsync(ct);
        }
        return ApiResults.NoContent();
    }

    /// <summary>Validate + normalize an email (format check only — reveals nothing about existence).</summary>
    private static bool TryReadEmail(string? raw, out string email)
    {
        email = string.Empty;
        if (string.IsNullOrWhiteSpace(raw) || !MailAddress.TryCreate(raw.Trim(), out _))
            return false;
        email = AuthTokens.NormalizeEmail(raw);
        return true;
    }
}
