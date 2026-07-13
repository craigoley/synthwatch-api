using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Logging;

namespace SynthWatch.Api.Infrastructure;

/// <summary>Outcome of minting a read SAS — mirrors <see cref="ArtifactStatus"/> so the endpoint maps to the
/// same 404/503 semantics as the streaming proxy.</summary>
public enum SasStatus
{
    Ok,           // Url + ExpiresOn are set
    Missing,      // no url, or not an Azure Blob host (defence-in-depth) → 404
    Unavailable,  // delegation-key / signing error (role missing, throttle, transient) → a clean 503, NOT a 500
}

/// <summary>A minted read SAS. <see cref="Url"/>/<see cref="ExpiresOn"/> are set only when <see cref="Status"/>
/// is Ok. The URL carries a bearer credential for its TTL — NEVER log it.</summary>
public sealed record BlobSasResult(SasStatus Status, string? Url, DateTimeOffset? ExpiresOn)
{
    public static readonly BlobSasResult Missing = new(SasStatus.Missing, null, null);
    public static readonly BlobSasResult Unavailable = new(SasStatus.Unavailable, null, null);
    public static BlobSasResult Of(string url, DateTimeOffset exp) => new(SasStatus.Ok, url, exp);
}

/// <summary>
/// Mints a short-TTL, READ-ONLY, SINGLE-BLOB <b>user-delegation</b> SAS for a runner-written artifact blob, so
/// the browser can fetch a large trace (124 MB+) DIRECTLY from Blob instead of streaming it through the Vercel
/// serverless proxy (which terminates a multi-tens-of-MB transfer at its ~15 s maxDuration). Key-LESS: signed
/// with a user-delegation key obtained via the API's managed identity (needs the <c>Storage Blob Delegator</c>
/// role) — NO account key is ever held or used. The mint stays behind the <c>#154</c> forensic auth gate; the
/// SAS itself is the tightest scope Azure offers (one blob, read, 30 min — long enough for an interactive
/// viewer SESSION, since the viewer lazily range-fetches this URL throughout the investigation).
/// </summary>
public interface IBlobSasMinter
{
    Task<BlobSasResult> MintReadSasAsync(string? blobUrl, CancellationToken ct);
}

public sealed class BlobSasMinter : IBlobSasMinter
{
    /// <summary>SAS lifetime. ★ Must cover an INTERACTIVE trace-viewing SESSION, not just the initial fetch:
    /// the Playwright viewer (public/trace-viewer/sw.bundle.js) opens the zip with zip.js HttpReader + Range
    /// reads and LAZILY range-fetches entries from this SAS URL throughout the investigation — so any request
    /// after <c>se</c> 403s ("Signature not valid in the specified KEY time frame") mid-session. The old 2-min
    /// TTL broke every investigation lasting &gt; ~2 min (blob size is irrelevant — 355's trace is 9.9 MB; the
    /// failing request is a late lazy range-fetch, not a slow download).
    /// ★ 30 min is a JUDGMENT CALL, not a measurement: we have no telemetry on how long a trace session
    /// actually lasts, so this is sized to cover a thorough multi-step forensic dig (actions → snapshots →
    /// network) with margin, while staying FAR short of the 30-day login session — the SAS stays a narrow,
    /// single-blob, read-only, HTTPS-only, authed-mint credential, never a session surrogate. The real fix
    /// (proxy the bytes through the API under the session, so access is governed by the session not a
    /// credential in the URL) is a separate, gated architecture change.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(30); // clock-skew backdate on start

    private readonly TokenCredential _credential;
    private readonly ILogger<BlobSasMinter> _logger;

    public BlobSasMinter(TokenCredential credential, ILogger<BlobSasMinter> logger)
    {
        _credential = credential;
        _logger = logger;
    }

    public async Task<BlobSasResult> MintReadSasAsync(string? blobUrl, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(blobUrl))
            return BlobSasResult.Missing;

        // Same defence-in-depth as ArtifactReader: the *_url is runner-written; only ever mint against an
        // Azure Blob endpoint (never sign a delegation key for an arbitrary host).
        if (!Uri.TryCreate(blobUrl, UriKind.Absolute, out var blobUri) ||
            !blobUri.Host.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase))
        {
            SasLog.InvalidUrl(_logger);
            return BlobSasResult.Missing;
        }

        try
        {
            var blobClient = new BlobClient(blobUri, _credential);
            var accountName = blobUri.Host.Split('.')[0];
            var serviceClient = new BlobServiceClient(new Uri($"{blobUri.Scheme}://{blobUri.Host}"), _credential);

            var now = DateTimeOffset.UtcNow;
            var startsOn = now - Skew;
            var expiresOn = now + Ttl;

            // Requires the Storage Blob Delegator role on the account (AAD-only; no account key).
            UserDelegationKey key = (await serviceClient.GetUserDelegationKeyAsync(startsOn, expiresOn, ct)).Value;

            var sas = new BlobSasBuilder
            {
                BlobContainerName = blobClient.BlobContainerName,
                BlobName = blobClient.Name,
                Resource = "b",            // ★ SINGLE BLOB — not container, not account
                StartsOn = startsOn,
                ExpiresOn = expiresOn,     // ★ short TTL
                Protocol = SasProtocol.Https,
            };
            sas.SetPermissions(BlobSasPermissions.Read); // ★ READ-ONLY

            var sasParams = sas.ToSasQueryParameters(key, accountName);
            var url = $"{blobUri.GetLeftPart(UriPartial.Path)}?{sasParams}";
            return BlobSasResult.Of(url, expiresOn);
        }
        catch (RequestFailedException ex)
        {
            // Role missing / throttle / transient → a clean "unavailable" (503), never an unhandled 500.
            SasLog.MintFailed(_logger, ex.Status, ex);
            return BlobSasResult.Unavailable;
        }
    }
}

/// <summary>High-performance (CA1848) log delegates — status/host only; NEVER the SAS URL (it's a credential).</summary>
internal static partial class SasLog
{
    [LoggerMessage(EventId = 4100, Level = LogLevel.Error,
        Message = "trace SAS mint failed (blob status {Status}) — check the MI has Storage Blob Delegator")]
    public static partial void MintFailed(ILogger logger, int status, Exception ex);

    [LoggerMessage(EventId = 4101, Level = LogLevel.Warning,
        Message = "trace_url is not an Azure Blob endpoint; refusing to mint a SAS for it")]
    public static partial void InvalidUrl(ILogger logger);
}
