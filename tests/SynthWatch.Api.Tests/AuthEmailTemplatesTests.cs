using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>The auth email bodies (no DB/host). Asserts the code/requester + SynthWatch branding render, that
/// both an HTML and a plaintext part exist (multipart), and that the HTML is email-safe (table layout, inline
/// styles, escaped input). Mirrors the proven runner alert template.</summary>
public class AuthEmailTemplatesTests
{
    [Fact]
    public void SignInCode_shows_the_code_prominently_in_both_html_and_text()
    {
        var m = AuthEmailTemplates.SignInCode("482913");

        Assert.Contains("sign-in code", m.Subject, StringComparison.OrdinalIgnoreCase);

        // HTML: the code is unmissable + branded + expiry + footer.
        Assert.Contains("482913", m.Html, StringComparison.Ordinal);
        Assert.Contains("SYNTHWATCH", m.Html, StringComparison.Ordinal);
        Assert.Contains("10 minutes", m.Html, StringComparison.Ordinal);
        Assert.Contains("synthetic monitoring", m.Html, StringComparison.Ordinal); // footer
        Assert.Contains("ignore this email", m.Html, StringComparison.OrdinalIgnoreCase);

        // Plaintext fallback also carries the code (readable without HTML).
        Assert.Contains("482913", m.Text, StringComparison.Ordinal);
        Assert.Contains("[SynthWatch]", m.Text, StringComparison.Ordinal);
        Assert.Contains("10 minutes", m.Text, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(m.Html));
        Assert.False(string.IsNullOrWhiteSpace(m.Text));
    }

    [Fact]
    public void Html_uses_email_safe_structure()
    {
        var html = AuthEmailTemplates.SignInCode("000123").Html;
        Assert.Contains("<!DOCTYPE html>", html, StringComparison.Ordinal);
        Assert.Contains("role=\"presentation\"", html, StringComparison.Ordinal); // TABLE layout, not flex/grid
        Assert.Contains("style=\"", html, StringComparison.Ordinal);              // inline styles
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase); // no <style> (Gmail strips it)
    }

    [Fact]
    public void AccessRequest_shows_requester_and_a_users_cta_when_dashboard_is_set()
    {
        var m = AuthEmailTemplates.AccessRequest("alice@corp.test", "https://dash.test/");

        Assert.Contains("ACCESS REQUEST", m.Html, StringComparison.Ordinal);
        Assert.Contains("alice@corp.test", m.Html, StringComparison.Ordinal);
        Assert.Contains("https://dash.test/users", m.Html, StringComparison.Ordinal); // CTA target (no double slash)
        Assert.Contains("Review in Users", m.Html, StringComparison.Ordinal);
        Assert.Contains("synthetic monitoring", m.Html, StringComparison.Ordinal);

        Assert.Contains("alice@corp.test", m.Text, StringComparison.Ordinal);
        Assert.Contains("https://dash.test/users", m.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AccessRequest_omits_the_button_when_no_dashboard_url()
    {
        var m = AuthEmailTemplates.AccessRequest("bob@corp.test", dashboardUrl: null);
        Assert.Contains("bob@corp.test", m.Html, StringComparison.Ordinal);   // still informative
        Assert.DoesNotContain("/users", m.Html, StringComparison.Ordinal);    // no broken CTA
        Assert.DoesNotContain("Review in Users", m.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Requester_email_is_html_escaped()
    {
        // Defensive: any markup-ish input in the email is encoded, never injected into the HTML.
        var m = AuthEmailTemplates.AccessRequest("a&b<x>@evil.test", "https://d.test");
        Assert.DoesNotContain("<x>", m.Html, StringComparison.Ordinal);
        Assert.Contains("a&amp;b", m.Html, StringComparison.Ordinal);
    }
}
