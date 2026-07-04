using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>
/// The authz decision is a PURE function (AuthGate.Decide), so the ENTIRE security matrix is exhaustively
/// tested here with no DB/host. The middleware is thin glue over this — a bug in the gate is a bypass or a
/// lockout, so this is the highest-value test file in the slice.
/// </summary>
public class AuthGateTests
{
    // The enumerated authz surface — every mutating endpoint that must require an editor/admin session.
    public static readonly (string Method, string Path)[] Writes =
    {
        ("POST", "/api/checks"),
        ("PATCH", "/api/checks/5"),
        ("DELETE", "/api/checks/5"),
        ("PUT", "/api/checks/5/tags"),
        ("PUT", "/api/checks/5/locations"),
        ("PUT", "/api/routing"),
        ("POST", "/api/channels"),
        ("PUT", "/api/channels/5"),
        ("DELETE", "/api/channels/5"),
        ("POST", "/api/channels/5/test"),
        ("POST", "/api/runs/5/ai-insights"),   // spends AOAI tokens → must be gated (cost control)
        ("POST", "/api/runs/5/baseline-diff"), // spends AOAI tokens → must be gated (cost control)
        ("POST", "/api/reconcile/trigger"),    // starts the reconcile ACA job → must be gated (compute spend)
    };

    public static TheoryData<string, string> WriteEndpoints()
    {
        var d = new TheoryData<string, string>();
        foreach (var (m, p) in Writes) d.Add(m, p);
        return d;
    }

    // ── enforcement ON: every write is gated by role ────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(WriteEndpoints))]
    public void Write_with_no_session_is_401(string method, string path) =>
        Assert.Equal(GateOutcome.Deny401, AuthGate.Decide(method, path, enforcementEnabled: true, role: null));

    [Theory]
    [MemberData(nameof(WriteEndpoints))]
    public void Write_with_anonymous_role_is_403(string method, string path) =>
        Assert.Equal(GateOutcome.Deny403, AuthGate.Decide(method, path, enforcementEnabled: true, role: Roles.Anonymous));

    [Theory]
    [MemberData(nameof(WriteEndpoints))]
    public void Write_with_editor_is_allowed(string method, string path) =>
        Assert.Equal(GateOutcome.Allow, AuthGate.Decide(method, path, enforcementEnabled: true, role: Roles.Editor));

    [Theory]
    [MemberData(nameof(WriteEndpoints))]
    public void Write_with_admin_is_allowed(string method, string path) =>
        Assert.Equal(GateOutcome.Allow, AuthGate.Decide(method, path, enforcementEnabled: true, role: Roles.Admin));

    // ── GETs are always open (read-only default) ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/checks")]
    [InlineData("/api/checks/5")]
    [InlineData("/api/checks/5/runs")]
    [InlineData("/api/incidents")]
    [InlineData("/api/sla")]
    [InlineData("/api/specs")]
    [InlineData("/api/channels")]
    [InlineData("/api/auth/me")]
    public void Get_is_open_regardless_of_token(string path)
    {
        Assert.Equal(GateOutcome.Allow, AuthGate.Decide("GET", path, enforcementEnabled: true, role: null));
        Assert.Equal(GateOutcome.Allow, AuthGate.Decide("HEAD", path, enforcementEnabled: true, role: null));
    }

    // ── the unauthenticated-write allowlist: login + access-request, no token needed ─────────────────

    [Theory]
    [InlineData("/api/auth/request-code")]
    [InlineData("/api/auth/verify")]
    [InlineData("/api/auth/request-access")]
    public void Allowlisted_auth_writes_pass_without_a_token(string path) =>
        Assert.Equal(GateOutcome.Allow, AuthGate.Decide("POST", path, enforcementEnabled: true, role: null));

    [Fact]
    public void Logout_is_NOT_allowlisted_so_it_requires_a_session() =>
        Assert.Equal(GateOutcome.Deny401, AuthGate.Decide("POST", "/api/auth/logout", enforcementEnabled: true, role: null));

    // ── the session-floor route: logout works for ANY valid session, including a demoted editor ──────

    [Theory]
    [InlineData(Roles.Anonymous)] // the demoted-editor case: valid session whose live role was revoked
    [InlineData(Roles.Editor)]
    [InlineData(Roles.Admin)]
    public void Logout_is_session_floor_so_any_valid_session_can_revoke_itself(string role) =>
        Assert.Equal(GateOutcome.Allow, AuthGate.Decide("POST", "/api/auth/logout", enforcementEnabled: true, role: role));

    [Theory]
    [InlineData("/auth/logout")]      // no /api prefix
    [InlineData("/api/auth/logout/")] // trailing slash
    public void Session_floor_matches_prefix_and_slash_variants(string path) =>
        Assert.Equal(GateOutcome.Allow, AuthGate.Decide("POST", path, enforcementEnabled: true, role: Roles.Anonymous));

    [Fact]
    public void Session_floor_does_not_leak_to_other_writes() =>
        Assert.Equal(GateOutcome.Deny403, AuthGate.Decide("POST", "/api/checks", enforcementEnabled: true, role: Roles.Anonymous));

    // ── enforcement OFF: the inert, deploy-safe default — every write passes as today ────────────────

    [Theory]
    [MemberData(nameof(WriteEndpoints))]
    public void Enforcement_off_passes_all_writes_inert(string method, string path) =>
        Assert.Equal(GateOutcome.Allow, AuthGate.Decide(method, path, enforcementEnabled: false, role: null));

    // ── admin-only routes (user management, slice 3) — ready now ─────────────────────────────────────

    [Theory]
    [InlineData("POST", "/api/editors")]
    [InlineData("DELETE", "/api/editors/someone@x.test")]
    [InlineData("DELETE", "/api/access-requests/someone@x.test")]
    public void Editor_is_forbidden_on_admin_only_routes(string method, string path) =>
        Assert.Equal(GateOutcome.Deny403, AuthGate.Decide(method, path, enforcementEnabled: true, role: Roles.Editor));

    [Theory]
    [InlineData("POST", "/api/editors")]
    [InlineData("DELETE", "/api/editors/someone@x.test")]
    [InlineData("DELETE", "/api/access-requests/someone@x.test")]
    public void Admin_is_allowed_on_admin_only_routes(string method, string path) =>
        Assert.Equal(GateOutcome.Allow, AuthGate.Decide(method, path, enforcementEnabled: true, role: Roles.Admin));

    // ── verb/path edge cases: case, trailing slash, the optional /api prefix ─────────────────────────

    [Theory]
    [InlineData("post", "/api/checks")]            // lowercase verb
    [InlineData("POST", "/API/Checks")]            // uppercase path
    [InlineData("POST", "/api/checks/")]           // trailing slash
    [InlineData("POST", "/checks")]                // no /api prefix
    [InlineData("PuT", "/api/routing/")]           // mixed case + trailing slash
    public void Edge_cased_writes_are_still_caught(string method, string path) =>
        Assert.Equal(GateOutcome.Deny401, AuthGate.Decide(method, path, enforcementEnabled: true, role: null));

    [Theory]
    [InlineData("/auth/verify")]                   // allowlist without the /api prefix
    [InlineData("/api/auth/verify/")]              // allowlist with a trailing slash
    public void Allowlist_matches_prefix_and_slash_variants(string path) =>
        Assert.Equal(GateOutcome.Allow, AuthGate.Decide("POST", path, enforcementEnabled: true, role: null));

    // ── ShouldAudit: only enforced, gated, non-allowlisted writes are audited ────────────────────────

    [Fact]
    public void ShouldAudit_only_for_enforced_gated_writes()
    {
        Assert.True(AuthGate.ShouldAudit("POST", "/api/channels", enforcementEnabled: true));
        Assert.False(AuthGate.ShouldAudit("POST", "/api/channels", enforcementEnabled: false)); // flag off
        Assert.False(AuthGate.ShouldAudit("GET", "/api/channels", enforcementEnabled: true));   // read
        Assert.False(AuthGate.ShouldAudit("POST", "/api/auth/verify", enforcementEnabled: true)); // allowlisted
    }
}
