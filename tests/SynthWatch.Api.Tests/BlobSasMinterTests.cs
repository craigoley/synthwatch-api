using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>
/// Offline unit tests for the SAS minter's DEFENCE-IN-DEPTH validation — the branches that return before any
/// Azure call (so no live storage / user-delegation key needed). The happy-path mint (GetUserDelegationKey +
/// signing) needs a real account and is exercised end-to-end by Craig; the endpoint's auth gate + 404/200
/// wiring is covered by the ArtifactsFunctions forensic-gate integration test.
/// </summary>
public class BlobSasMinterTests
{
    private sealed class NeverCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) =>
            throw new InvalidOperationException("credential must not be used for a rejected url");
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) =>
            throw new InvalidOperationException("credential must not be used for a rejected url");
    }

    private static BlobSasMinter Minter() => new(new NeverCredential(), NullLogger<BlobSasMinter>.Instance);

    // ★ The TTL is a deliberate contract, not an incidental constant: it must cover an interactive trace-viewer
    // SESSION (the viewer lazily range-fetches the SAS URL throughout the dig), not just the initial fetch. Pins
    // 30 min so a shrink back toward the old 2-min window — which 403'd every investigation past ~2 min — is a
    // conscious change that trips this test, not a silent regression.
    [Fact]
    public void Sas_ttl_covers_an_interactive_session_not_just_the_initial_fetch()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), BlobSasMinter.Ttl);
        Assert.True(BlobSasMinter.Ttl >= TimeSpan.FromMinutes(10), "TTL must comfortably outlast a real trace investigation");
    }

    [Fact]
    public async Task Null_or_empty_url_is_Missing_without_touching_the_credential()
    {
        Assert.Equal(SasStatus.Missing, (await Minter().MintReadSasAsync(null, default)).Status);
        Assert.Equal(SasStatus.Missing, (await Minter().MintReadSasAsync("", default)).Status);
    }

    [Theory]
    [InlineData("https://evil.example.com/synthwatch-artifacts/traces/x.zip")] // not a blob host
    [InlineData("https://acct.blob.core.windows.net.evil.com/c/x.zip")]        // suffix-spoof
    [InlineData("not-a-url")]
    public async Task Non_blob_host_is_Missing_never_signs(string url)
    {
        // Must reject BEFORE constructing a BlobServiceClient / fetching a delegation key (NeverCredential
        // throws if reached) — never mint a SAS for an arbitrary host from a runner-written url.
        var r = await Minter().MintReadSasAsync(url, default);
        Assert.Equal(SasStatus.Missing, r.Status);
        Assert.Null(r.Url);
    }
}
