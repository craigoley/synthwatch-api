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
    private const int MaxAccessPerWindow = 3; // per-EMAIL RECORDING cap (one email can't flood the ledger)

    // ★ Gap-1 email-bomb defense. request-access fans a notification out to every admin, so an attacker
    // VARYING THE EMAIL (bypassing the per-email cap) could bomb the admin inboxes. Three layers, none of
    // which reveals anything to the requester (the response is byte-identical in every branch):
    //   • GLOBAL hourly notify cap — THE REAL FIX. At most this many request-access NOTIFICATIONS per rolling
    //     hour, TOTAL, regardless of source. Beyond it the individual email is suppressed and admins get ONE
    //     digest. Sized generously: legitimate access requests are rare (a handful a week); 10/hour leaves room
    //     for a batch onboarding while turning a 100-email bomb into ≤10 emails + 1 digest. Does NOT depend on
    //     the IP — which is worthless as a discriminator when EVERY legit Wegmans user shares one egress IP.
    public const int GlobalNotifyCapPerHour = 10;
    private static readonly TimeSpan GlobalNotifyWindow = TimeSpan.FromHours(1);
    //   • Per-IP cap — a BLAST-RADIUS limiter for the lazy case ONLY, NOT a real defense: X-Forwarded-For's
    //     leftmost hop is client-controllable (see ClientIp) → SPOOFABLE, and the shared corporate egress means
    //     a cap tight enough to stop a bomb would lock out the whole company. 100/24h: a whole floor of Wegmans
    //     employees onboarding from the shared IP never hits it; a single un-rotated script does. Claims nothing more.
    public const int PerIpAccessCapPerDay = 100;

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
    /// regardless of whether the email is unknown / already an editor / an admin, and regardless of whether the
    /// notification was emailed, digested, deduped, or rate-limited — never reveals status (a distinguishable
    /// reply would be a probing oracle AND a new dead-end: "my request went nowhere and nobody told me").
    /// Records the request (admin visibility); notifies ADMIN_EMAILS under an email-bomb-resistant policy
    /// (<see cref="DecideAccessNotify"/>).
    /// </summary>
    [Function("AuthRequestAccess")]
    public async Task<IActionResult> RequestAccess(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/request-access")] HttpRequest req,
        CancellationToken ct)
    {
        if (!TryReadEmail((await ReadBodyAsync<EmailRequest>(req))?.Email, out var email))
            return ApiResults.BadRequest("A valid email is required.");

        var now = DateTimeOffset.UtcNow;
        var ip = ClientIp(req);

        // Per-EMAIL RECORDING cap: one email can't flood the ledger. Over it → uniform 200, no record, no email
        // (a same-email repeat is already captured + already notified via the first request's dedupe below).
        var perEmail = await _db.AccessRequests.CountAsync(a => a.Email == email && a.RequestedAt > now - AccessRateWindow, ct);
        if (perEmail >= MaxAccessPerWindow)
            return ApiResults.Ok(new MessageDto(AccessMessage));

        // ★ ALWAYS record every distinct request — nobody's request is silently lost (that would be the
        // dead-end all over again, in a new place). Recorded BEFORE the counts so they include this request.
        _db.AccessRequests.Add(new AccessRequest { Email = email, RequestIp = ip });
        await _db.SaveChangesAsync(ct);

        // The three inputs to the notify policy (all over the indexed access_requests table).
        var priorSameEmail = perEmail > 0; // an earlier request from THIS email in the window → dedupe
        var perIpCount = ip is null ? 0 : await _db.AccessRequests.CountAsync(a => a.RequestIp == ip && a.RequestedAt > now - AccessRateWindow, ct);
        var hourlyCount = await _db.AccessRequests.CountAsync(a => a.RequestedAt > now - GlobalNotifyWindow, ct);

        switch (DecideAccessNotify(priorSameEmail, perIpCount, ip is not null, hourlyCount))
        {
            case AccessNotify.Individual:
                foreach (var admin in AdminEmails())
                    await TrySendAsync(admin, AuthEmailTemplates.AccessRequest(email, DashboardUrl()), ct);
                break;
            case AccessNotify.Digest:
                // ★ THE BOMB BECOMES A DIGEST LINK: one digest to the admins instead of N individual emails.
                foreach (var admin in AdminEmails())
                    await TrySendAsync(admin, AuthEmailTemplates.AccessRequestDigest(hourlyCount, DashboardUrl()), ct);
                break;
            case AccessNotify.Suppress:
                break; // recorded + reviewable on the Users page; no email (deduped / over a cap)
        }

        return ApiResults.Ok(new MessageDto(AccessMessage)); // ★ BYTE-IDENTICAL across every branch above
    }

    /// <summary>What to do with the admin notification for a just-recorded access request — the pure, testable
    /// email-bomb policy. The requester's HTTP response is byte-identical regardless of the verdict.
    ///   • <c>Individual</c> — a fresh request, under both the per-IP blast-radius cap and the global hourly cap.
    ///   • <c>Digest</c>     — the exact moment the global hourly cap is CROSSED (hourlyCount == cap + 1): send
    ///                         ONE digest, stateless (no per-digest state column — access_requests is runner-owned).
    ///   • <c>Suppress</c>   — a same-email dedupe, an over-IP request, or beyond the crossing: recorded, no email.
    /// ★ Only the GLOBAL cap is a real defense (the per-IP source is spoofable + shared); it needs no IP at all.</summary>
    public enum AccessNotify { Individual, Digest, Suppress }

    public static AccessNotify DecideAccessNotify(bool priorSameEmailInWindow, long perIpCountInDay, bool ipKnown, long hourlyCount)
    {
        var overGlobal = hourlyCount > GlobalNotifyCapPerHour;
        var overIp = ipKnown && perIpCountInDay > PerIpAccessCapPerDay;
        if (!priorSameEmailInWindow && !overIp && !overGlobal)
            return AccessNotify.Individual;
        // Over the global cap → ONE digest, fired exactly at the crossing (stateless). Under a sustained bomb
        // hourlyCount climbs monotonically within the hour, so the digest fires once; the rest are suppressed.
        if (overGlobal && hourlyCount == GlobalNotifyCapPerHour + 1)
            return AccessNotify.Digest;
        return AccessNotify.Suppress;
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
        // A non-JSON Content-Type makes ReadFromJsonAsync throw InvalidOperationException (NOT JsonException) →
        // a shielded 500. Guard it: a non-JSON or malformed body is treated as an ABSENT body → null, which
        // every caller maps to a uniform 400 (the enumeration-safe posture — never leak request-shape detail).
        if (!req.HasJsonContentType()) return null;
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
