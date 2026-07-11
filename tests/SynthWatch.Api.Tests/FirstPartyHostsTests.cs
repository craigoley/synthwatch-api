using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>Parity tests for the Wegmans first-party allowlist (Error-diff P1). Mirrors the runner's
/// firstPartyHosts.test.ts — a regression on either side is caught by the shared trace-signals golden AND
/// these unit cases. The ★ cases are the must-go-reds the classifier fix exists for.</summary>
public class FirstPartyHostsTests
{
    private const string Target = "www.wegmans.com";

    [Theory]
    [InlineData("wegmans.com")]
    [InlineData("www.wegmans.com")]
    [InlineData("images.wegmans.com")]        // ★ sibling subdomain of the usual target — old exact-host missed it
    [InlineData("preview.commerce.wegmans.com")]
    [InlineData("wegmans.cloud")]             // ★ the headline bug: *.wegmans.cloud was marked third-party
    [InlineData("api.wegmans.cloud")]
    [InlineData("anything.deeper.wegmans.cloud")]
    [InlineData("wegmans-prod.azure-api.net")] // APIM gateway (*.azure-api.net)
    [InlineData("foo.azure-api.net")]
    [InlineData("wegapi.prod.example")]       // wegapi storefront backend (substring)
    [InlineData("kitting-catering-api.example.net")] // kitting/catering backend (substring)
    public void IsWegmansHost_true_for_first_party(string host) =>
        Assert.True(FirstPartyHosts.IsWegmansHost(host), $"{host} should be first-party");

    [Theory]
    [InlineData("di.rlcdn.com")]              // CSP third-party
    [InlineData("bot.emplifi.io")]
    [InlineData("realtime-c.astutebot.com")]
    [InlineData("connect.facebook.net")]
    [InlineData("notwegmans.com")]            // suffix rule, not a bare "wegmans" contains
    [InlineData("wegmans.com.evil.example")]  // apex must be a SUFFIX boundary, not anywhere in the host
    [InlineData("azure-api.net")]             // bare apex is never a real gateway host; only *.azure-api.net
    [InlineData("")]                          // empty host (blob:/data:) → third-party
    public void IsWegmansHost_false_for_third_party(string host) =>
        Assert.False(FirstPartyHosts.IsWegmansHost(host), $"{host} should NOT be first-party");

    [Fact]
    public void IsWegmansHost_is_case_insensitive()
    {
        Assert.True(FirstPartyHosts.IsWegmansHost("IMAGES.WEGMANS.COM"));
        Assert.True(FirstPartyHosts.IsWegmansHost("API.Wegmans.Cloud"));
    }

    [Fact]
    public void IsFirstParty_allowlist_or_target_host_or_its_subdomains()
    {
        // Allowlist hosts are first-party regardless of the target.
        Assert.True(FirstPartyHosts.IsFirstParty("images.wegmans.com", Target));
        Assert.True(FirstPartyHosts.IsFirstParty("api.wegmans.cloud", Target));
        // The check's own target + subdomains are first-party even when NOT in the static allowlist.
        Assert.True(FirstPartyHosts.IsFirstParty("www.meals2go.com", "www.meals2go.com")); // target host itself
        Assert.True(FirstPartyHosts.IsFirstParty("cdn.meals2go.com", "meals2go.com"));      // subdomain of target
        Assert.False(FirstPartyHosts.IsFirstParty("meals2go.com", Target));                 // not target, not allowlist
        // Genuine third parties stay third-party.
        Assert.False(FirstPartyHosts.IsFirstParty("di.rlcdn.com", Target));
        Assert.False(FirstPartyHosts.IsFirstParty("", Target));                             // no host
        Assert.True(FirstPartyHosts.IsFirstParty("www.wegmans.com", null));                 // allowlist, no target
    }
}
