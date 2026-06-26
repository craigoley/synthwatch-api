using System.Net;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// HTML + plaintext bodies for the two auth emails (OTP sign-in code, request-access admin notice),
/// MIRRORING the runner's proven alert template (runner/alertEmail.ts) — same control-room palette, TABLE
/// layout (email is a hostile env: no flex/grid), INLINE styles only (Gmail strips &lt;style&gt;), system
/// font stack, a coloured header bar with a SYNTHWATCH badge + label, a 600px card, and the
/// "SynthWatch · synthetic monitoring" footer. Each returns BOTH an HTML body and a plaintext alternative
/// (multipart/alternative) so the code/CTA reads in any client — and a lighter, image-free body keeps the
/// already-weak managed-domain reputation from getting worse (deliverability).
/// </summary>
public static class AuthEmailTemplates
{
    // ── palette (matches runner/alertEmail.ts) ──
    private const string Ink = "#1a1a1a";
    private const string Muted = "#667085";
    private const string Border = "#e4e7ec";
    private const string Card = "#ffffff";
    private const string Page = "#f2f4f7";
    private const string White = "#ffffff";
    private const string Panel = "#f8f9fc";    // the alert's rcaBlock surface — reused for the code box
    private const string Header = "#101828";   // neutral control-room header (auth isn't severity-coloured)

    private const string Font =
        "system-ui, -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif";
    private const string Mono = "'SFMono-Regular', ui-monospace, SFMono, Menlo, Consolas, 'Liberation Mono', monospace";

    public sealed record Email(string Subject, string Html, string Text);

    /// <summary>The emailed 6-digit sign-in code — displayed large + letter-spaced so it's unmissable and easy to copy.</summary>
    public static Email SignInCode(string code)
    {
        var body =
            $"<div style=\"color:{Ink};font-size:15px;line-height:1.5;margin-bottom:18px\">Use this code to finish signing in to SynthWatch.</div>" +
            // the code box — the point of the email; big, monospace, letter-spaced, copy-friendly.
            $"<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" width=\"100%\" style=\"margin:4px 0 18px\">" +
            $"<tr><td align=\"center\" style=\"background:{Panel};border:1px solid {Border};border-radius:10px;padding:22px 16px\">" +
            $"<div style=\"font-family:{Mono};font-size:34px;font-weight:700;letter-spacing:10px;color:{Ink}\">{Esc(code)}</div>" +
            $"</td></tr></table>" +
            $"<div style=\"color:{Muted};font-size:13px;line-height:1.5\">This code expires in 10 minutes and can be used once.</div>" +
            $"<div style=\"color:{Muted};font-size:13px;line-height:1.5;margin-top:8px\">Didn't request this? You can safely ignore this email — no one can sign in without the code.</div>";

        var text =
            $"[SynthWatch] Your sign-in code: {code}\n\n" +
            "This code expires in 10 minutes and can be used once.\n" +
            "Didn't request this? You can safely ignore this email — no one can sign in without the code.";

        return new Email("Your SynthWatch sign-in code", Shell("Sign-in code", "Your sign-in code", body), text);
    }

    /// <summary>The admin notice that <paramref name="requesterEmail"/> asked for edit access, with a CTA to the
    /// users page (omitted when DASHBOARD_URL is unset, mirroring the alert button).</summary>
    public static Email AccessRequest(string requesterEmail, string? dashboardUrl)
    {
        var safeEmail = Esc(requesterEmail);
        var body =
            $"<div style=\"color:{Ink};font-size:15px;line-height:1.5;margin-bottom:16px\">Someone requested edit access to SynthWatch.</div>" +
            // requester fact row (mirrors the alert's factRow)
            $"<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" width=\"100%\">" +
            $"<tr><td style=\"padding:6px 16px 6px 0;color:{Muted};font-size:13px;white-space:nowrap;vertical-align:top\">Requester</td>" +
            $"<td style=\"padding:6px 0;color:{Ink};font-size:13px;font-weight:600;word-break:break-all\">{safeEmail}</td></tr>" +
            $"</table>" +
            Button("Review in Users", dashboardUrl) +
            $"<div style=\"color:{Muted};font-size:13px;line-height:1.5;margin-top:18px\">Add them as an editor on the Users page to grant access. Ignore this email to decline.</div>";

        var cta = string.IsNullOrWhiteSpace(dashboardUrl) ? "" : $"\nReview in Users: {UsersUrl(dashboardUrl)}";
        var text =
            $"[SynthWatch] Edit-access requested\n\n{requesterEmail} requested edit access to SynthWatch.\n" +
            "Add them as an editor on the Users page to grant access, or ignore this email to decline." + cta;

        return new Email("SynthWatch — edit-access request", Shell("Access request", "Edit-access requested", body), text);
    }

    // ── shared shell: header bar (badge + label + title) → body → footer, in a 600px card ──
    private static string Shell(string label, string title, string bodyHtml) =>
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
        "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
        "<meta name=\"color-scheme\" content=\"light dark\"><meta name=\"supported-color-schemes\" content=\"light dark\"></head>" +
        $"<body style=\"margin:0;padding:0;background:{Page}\">" +
        $"<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" width=\"100%\" style=\"background:{Page}\">" +
        "<tr><td align=\"center\" style=\"padding:24px 12px\">" +
        $"<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" width=\"600\" style=\"max-width:600px;width:100%;background:{Card};border:1px solid {Border};border-radius:12px;overflow:hidden\">" +
        // header bar
        $"<tr><td style=\"background:{Header};padding:18px 24px;font-family:{Font}\">" +
        $"<span style=\"display:inline-block;background:rgba(255,255,255,.16);color:{White};font-size:11px;font-weight:700;letter-spacing:.12em;padding:3px 9px;border-radius:6px\">SYNTHWATCH</span>" +
        $"<span style=\"color:{White};font-size:12px;font-weight:700;letter-spacing:.06em;margin-left:8px;opacity:.92\">{Esc(label.ToUpperInvariant())}</span>" +
        $"<div style=\"color:{White};font-size:20px;font-weight:700;margin-top:8px;line-height:1.3\">{Esc(title)}</div>" +
        "</td></tr>" +
        // body
        $"<tr><td style=\"padding:22px 24px;font-family:{Font}\">{bodyHtml}</td></tr>" +
        // footer
        $"<tr><td style=\"padding:14px 24px;border-top:1px solid {Border};font-family:{Font};color:{Muted};font-size:12px\">" +
        "SynthWatch &middot; synthetic monitoring</td></tr>" +
        "</table></td></tr></table></body></html>";

    // Bulletproof button (table-cell <a> with padding + bg) — omitted when there's no dashboard URL.
    private static string Button(string label, string? dashboardUrl)
    {
        if (string.IsNullOrWhiteSpace(dashboardUrl))
            return "";
        var url = Esc(UsersUrl(dashboardUrl));
        return
            "<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"margin:22px 0 4px\">" +
            $"<tr><td align=\"center\" bgcolor=\"{Header}\" style=\"border-radius:8px\">" +
            $"<a href=\"{url}\" target=\"_blank\" style=\"display:inline-block;padding:12px 28px;font-family:{Font};font-size:14px;font-weight:600;color:{White};text-decoration:none;border-radius:8px\">{Esc(label)} &rarr;</a>" +
            "</td></tr></table>";
    }

    private static string UsersUrl(string dashboardUrl) => $"{dashboardUrl.TrimEnd('/')}/users";

    private static string Esc(string s) => WebUtility.HtmlEncode(s);
}
