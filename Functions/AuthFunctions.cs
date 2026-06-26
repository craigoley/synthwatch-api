using System.Net.Mail;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SynthWatch.Api.Data;
using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;

namespace SynthWatch.Api.Functions;

/// <summary>
/// Phase 12 slice 1 — identity plumbing. Email-OTP login → opaque DB-backed bearer sessions, plus an
/// enumeration-safe access request. ★ NO ENFORCEMENT THIS SLICE: these endpoints MINT and VERIFY tokens,
/// but nothing checks them yet — every existing write stays open (anonymous) until slice 2 adds the
/// AuthorizationMiddleware. All endpoints here are intentionally anonymous (they're how you GET a token).
/// </summary>
public class AuthFunctions
{
    private readonly SynthWatchDbContext _db;
    private readonly IEmailSender _email;
    private readonly ILogger<AuthFunctions> _logger;

    public AuthFunctions(SynthWatchDbContext db, IEmailSender email, ILogger<AuthFunctions> logger)
    {
        _db = db;
        _email = email;
        _logger = logger;
    }

    private static readonly TimeSpan CodeTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SessionTtl = TimeSpan.FromDays(30);
    private const int MaxVerifyAttempts = 5;
    // request-code: ≤5 codes per email per 15 min. request-access: ≤3 per email per 24h (it pages admins).
    private static readonly TimeSpan CodeRateWindow = TimeSpan.FromMinutes(15);
    private const int MaxCodesPerWindow = 5;
    private static readonly TimeSpan AccessRateWindow = TimeSpan.FromHours(24);
    private const int MaxAccessPerWindow = 3;

    private const string CodeSentMessage = "If your email is registered, a sign-in code has been sent.";
    private const string AccessMessage = "If your request is valid, an admin will review it.";
    private const string InvalidCodeMessage = "That code is invalid or has expired.";

    /// <summary>
    /// POST /api/auth/request-code { email } — issue a 6-digit code. Enumeration-safe: ALWAYS 202 with a
    /// uniform message; a code is generated + stored (hashed) for any valid email, but only EMAILED to a
    /// known editor/admin. Rate-limited per email (silent no-op over the limit, so the response can't be
    /// used as an oracle).
    /// </summary>
    [Function("AuthRequestCode")]
    public async Task<IActionResult> RequestCode(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/request-code")] HttpRequest req,
        CancellationToken ct)
    {
        if (!TryReadEmail((await ReadBodyAsync<EmailRequest>(req))?.Email, out var email))
            return ApiResults.BadRequest("A valid email is required.");

        var now = DateTimeOffset.UtcNow;
        var recent = await _db.OtpCodes.CountAsync(o => o.Email == email && o.CreatedAt > now - CodeRateWindow, ct);
        if (recent >= MaxCodesPerWindow)
            return ApiResults.Accepted(new MessageDto(CodeSentMessage)); // silent rate-limit (uniform response)

        var code = AuthTokens.NewNumericCode();
        _db.OtpCodes.Add(new OtpCode
        {
            Email = email,
            CodeHash = AuthTokens.Sha256Hex(code),
            ExpiresAt = now + CodeTtl,
            RequestIp = ClientIp(req),
        });
        await _db.SaveChangesAsync(ct);

        // Send ONLY to a known editor/admin — an unknown email gets a stored (unsendable) code and no
        // email, so enumeration learns nothing (uniform 202, no email unless they own a real account).
        if (await ResolveRoleAsync(email, ct) != Roles.Anonymous)
            await TrySendAsync(email, AuthEmailTemplates.SignInCode(code), ct);

        return ApiResults.Accepted(new MessageDto(CodeSentMessage));
    }

    /// <summary>
    /// POST /api/auth/verify { email, code } — consume the newest unexpired code and mint a session.
    /// One-time (consumed), expiring, attempt-capped (≥5 wrong → locked). Only an editor/admin gets a
    /// session; an anonymous email (never/no-longer an editor) is rejected as invalid.
    /// </summary>
    [Function("AuthVerify")]
    public async Task<IActionResult> Verify(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/verify")] HttpRequest req,
        CancellationToken ct)
    {
        var body = await ReadBodyAsync<VerifyRequest>(req);
        if (!TryReadEmail(body?.Email, out var email) || string.IsNullOrWhiteSpace(body?.Code))
            return ApiResults.BadRequest("email and code are required.");

        var now = DateTimeOffset.UtcNow;
        var otp = await _db.OtpCodes
            .Where(o => o.Email == email && o.ConsumedAt == null)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(ct);

        // Uniform "invalid or expired" for missing / expired / locked — no distinct oracle per case.
        if (otp is null || otp.ExpiresAt < now || otp.AttemptCount >= MaxVerifyAttempts)
            return ApiResults.BadRequest(InvalidCodeMessage);

        if (otp.CodeHash != AuthTokens.Sha256Hex(body!.Code!))
        {
            otp.AttemptCount++; // brute-force cap
            await _db.SaveChangesAsync(ct);
            return ApiResults.BadRequest(InvalidCodeMessage);
        }

        otp.ConsumedAt = now; // one-time
        await _db.SaveChangesAsync(ct);

        // Role is resolved at verify time (not baked) — a removed editor can't mint a usable session.
        var role = await ResolveRoleAsync(email, ct);
        if (role == Roles.Anonymous)
            return ApiResults.BadRequest(InvalidCodeMessage);

        var token = AuthTokens.NewSessionToken();
        var expiresAt = now + SessionTtl;
        _db.Sessions.Add(new Session
        {
            TokenHash = AuthTokens.Sha256Hex(token),
            Email = email,
            ExpiresAt = expiresAt,
            IssuedIp = ClientIp(req),
        });
        await _db.SaveChangesAsync(ct);

        return ApiResults.Ok(new VerifyResponseDto(token, email, role, expiresAt));
    }

    /// <summary>
    /// GET /api/auth/me — resolve the bearer token to { email, role } (role is re-resolved live), or 401.
    /// A voluntary token check on a READ; the enforcing GATE for writes is slice 2.
    /// </summary>
    [Function("AuthMe")]
    public async Task<IActionResult> Me(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/me")] HttpRequest req,
        CancellationToken ct)
    {
        var session = await CurrentSessionAsync(req, ct);
        if (session is null)
            return ApiResults.Unauthorized("No valid session.");

        session.LastUsedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return ApiResults.Ok(new MeDto(session.Email, await ResolveRoleAsync(session.Email, ct)));
    }

    /// <summary>POST /api/auth/logout — revoke the bearer token's session. Idempotent (always 200).</summary>
    [Function("AuthLogout")]
    public async Task<IActionResult> Logout(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/logout")] HttpRequest req,
        CancellationToken ct)
    {
        var token = AuthTokens.BearerFrom(req.Headers.Authorization);
        if (token is not null)
        {
            var hash = AuthTokens.Sha256Hex(token);
            var session = await _db.Sessions.FirstOrDefaultAsync(s => s.TokenHash == hash && s.RevokedAt == null, ct);
            if (session is not null)
            {
                session.RevokedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
            }
        }
        return ApiResults.Ok(new MessageDto("Signed out."));
    }

    /// <summary>
    /// POST /api/auth/request-access { email } — ENUMERATION-SAFE access request. ALWAYS the same response,
    /// regardless of whether the email is unknown / already an editor / an admin — never reveals status.
    /// Records the request (admin visibility) and notifies ADMIN_EMAILS. Rate-limited per email (it pages
    /// admins — an email-bomb vector).
    /// </summary>
    [Function("AuthRequestAccess")]
    public async Task<IActionResult> RequestAccess(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/request-access")] HttpRequest req,
        CancellationToken ct)
    {
        if (!TryReadEmail((await ReadBodyAsync<EmailRequest>(req))?.Email, out var email))
            return ApiResults.BadRequest("A valid email is required.");

        var now = DateTimeOffset.UtcNow;
        var recent = await _db.AccessRequests.CountAsync(a => a.Email == email && a.RequestedAt > now - AccessRateWindow, ct);
        if (recent >= MaxAccessPerWindow)
            return ApiResults.Ok(new MessageDto(AccessMessage)); // silent rate-limit (uniform response)

        _db.AccessRequests.Add(new AccessRequest { Email = email, RequestIp = ClientIp(req) });
        await _db.SaveChangesAsync(ct);

        // Notify each admin (few). Uniform response regardless of send outcome.
        foreach (var admin in AdminEmails())
            await TrySendAsync(admin, AuthEmailTemplates.AccessRequest(email, DashboardUrl()), ct);

        return ApiResults.Ok(new MessageDto(AccessMessage));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    /// <summary>resolveRole(email) = ADMIN_EMAILS ∪ editors. Admins come from the app setting (env-based so
    /// they can't be locked out); editors from the DB allowlist. Anything else is anonymous (read-only).</summary>
    private async Task<string> ResolveRoleAsync(string email, CancellationToken ct)
    {
        if (AdminEmails().Contains(email))
            return Roles.Admin;
        if (await _db.Editors.AnyAsync(e => e.Email == email, ct))
            return Roles.Editor;
        return Roles.Anonymous;
    }

    /// <summary>The admin allowlist from the ADMIN_EMAILS app setting (comma-separated, normalized).
    /// ★ Must be set as an API app setting — not just the dashboard's Vercel env — or admins are unrecognized.</summary>
    private static HashSet<string> AdminEmails() =>
        (Environment.GetEnvironmentVariable("ADMIN_EMAILS") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(AuthTokens.NormalizeEmail)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>The live, non-revoked, non-expired session for the request's bearer token, or null.</summary>
    private async Task<Session?> CurrentSessionAsync(HttpRequest req, CancellationToken ct)
    {
        var token = AuthTokens.BearerFrom(req.Headers.Authorization);
        if (token is null)
            return null;
        var hash = AuthTokens.Sha256Hex(token);
        var now = DateTimeOffset.UtcNow;
        return await _db.Sessions.FirstOrDefaultAsync(
            s => s.TokenHash == hash && s.RevokedAt == null && s.ExpiresAt > now, ct);
    }

    private static async Task<T?> ReadBodyAsync<T>(HttpRequest req) where T : class
    {
        try { return await req.ReadFromJsonAsync<T>(); }
        catch (JsonException) { return null; }
    }

    /// <summary>Validates + normalizes the email. False when missing/malformed (a format check — reveals
    /// nothing about existence, so not an enumeration oracle).</summary>
    private static bool TryReadEmail(string? raw, out string email)
    {
        email = string.Empty;
        if (string.IsNullOrWhiteSpace(raw) || !MailAddress.TryCreate(raw.Trim(), out _))
            return false;
        email = AuthTokens.NormalizeEmail(raw);
        return true;
    }

    private static string? ClientIp(HttpRequest req)
    {
        var fwd = req.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(fwd))
            return fwd.Split(',')[0].Trim();
        return req.HttpContext.Connection.RemoteIpAddress?.ToString();
    }

    /// <summary>Send, but never let a transport failure 500 the auth flow or become an enumeration oracle.</summary>
    private async Task TrySendAsync(string to, AuthEmailTemplates.Email msg, CancellationToken ct)
    {
        try { await _email.SendAsync(to, msg.Subject, msg.Text, msg.Html, ct); }
        catch (Exception ex) { AuthLog.EmailFailed(_logger, msg.Subject, ex); }
    }

    // The dashboard base for the access-request CTA (DASHBOARD_URL app setting, as the runner uses). Null →
    // the button is omitted (the template degrades gracefully, like the alert email's view-incident button).
    private static string? DashboardUrl() => Environment.GetEnvironmentVariable("DASHBOARD_URL");
}

internal static partial class AuthLog
{
    [LoggerMessage(EventId = 5100, Level = LogLevel.Warning, Message = "Auth email send failed (subject {Subject})")]
    public static partial void EmailFailed(ILogger logger, string subject, Exception ex);
}
