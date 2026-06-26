using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;

namespace SynthWatch.Api.Infrastructure;

/// <summary>What happened fetching an artifact blob — so callers map to the right response (and a non-404
/// blob error is a clean "unavailable", never a 500).</summary>
public enum ArtifactStatus
{
    Ok,           // downloaded — Content is set
    Missing,      // no url, or not an Azure Blob host (defence-in-depth) → 404
    Gone,         // 404 from Blob (url recorded but the blob is purged) → 404
    Unavailable,  // a non-404 Blob error (429/503/auth/etc.) → a clean 503, NOT a 500
}

/// <summary>The result of an artifact fetch. <see cref="Content"/> is set only when <see cref="Status"/> is Ok;
/// the caller OWNS it (dispose after streaming / parsing).</summary>
public sealed record ArtifactBlob(ArtifactStatus Status, Stream? Content)
{
    public static readonly ArtifactBlob Missing = new(ArtifactStatus.Missing, null);
    public static readonly ArtifactBlob Gone = new(ArtifactStatus.Gone, null);
    public static readonly ArtifactBlob Unavailable = new(ArtifactStatus.Unavailable, null);
    public static ArtifactBlob Of(Stream s) => new(ArtifactStatus.Ok, s);
}

/// <summary>
/// The ONE place the API fetches a runner-written artifact blob (Playwright traces, screenshots) — the
/// blob-host allowlist + managed-identity <see cref="BlobClient"/> + the 404/non-404 classification, shared by
/// the artifact proxy (streaming) and the trace parsers (buffered). Previously duplicated 3× across
/// StreamRunArtifact / GetTraceSignals / AiInsightsFunctions, with divergent error handling.
/// </summary>
public interface IArtifactReader
{
    /// <summary>Download the blob into a seekable MemoryStream (for in-process parsing).</summary>
    Task<ArtifactBlob> DownloadToMemoryAsync(string? blobUrl, string artifact, long id, CancellationToken ct);

    /// <summary>Open the blob as a forward stream (to proxy straight to the client, never buffered).</summary>
    Task<ArtifactBlob> OpenStreamAsync(string? blobUrl, string artifact, long id, CancellationToken ct);
}

public sealed class ArtifactReader : IArtifactReader
{
    private readonly TokenCredential _credential;
    private readonly ILogger<ArtifactReader> _logger;

    public ArtifactReader(TokenCredential credential, ILogger<ArtifactReader> logger)
    {
        _credential = credential;
        _logger = logger;
    }

    public Task<ArtifactBlob> DownloadToMemoryAsync(string? blobUrl, string artifact, long id, CancellationToken ct) =>
        FetchAsync(blobUrl, artifact, id, async blob =>
        {
            var ms = new MemoryStream();
            await blob.DownloadToAsync(ms, ct);
            ms.Position = 0;
            return (Stream)ms;
        });

    public Task<ArtifactBlob> OpenStreamAsync(string? blobUrl, string artifact, long id, CancellationToken ct) =>
        FetchAsync(blobUrl, artifact, id, async blob =>
        {
            var resp = await blob.DownloadStreamingAsync(cancellationToken: ct);
            return resp.Value.Content; // forward stream; the caller's FileStreamResult disposes it
        });

    private async Task<ArtifactBlob> FetchAsync(string? blobUrl, string artifact, long id, Func<BlobClient, Task<Stream>> download)
    {
        if (string.IsNullOrEmpty(blobUrl))
            return ArtifactBlob.Missing;

        // Defence-in-depth: the *_url is runner-written, but never attach the API's managed-identity token to
        // an arbitrary host. Only proxy Azure Blob endpoints.
        if (!Uri.TryCreate(blobUrl, UriKind.Absolute, out var blobUri) ||
            !blobUri.Host.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase))
        {
            ArtifactLog.InvalidUrl(_logger, artifact, id);
            return ArtifactBlob.Missing;
        }

        try
        {
            var stream = await download(new BlobClient(blobUri, _credential));
            return ArtifactBlob.Of(stream);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return ArtifactBlob.Gone; // url recorded but the blob is gone (retention/cleanup)
        }
        catch (RequestFailedException ex)
        {
            // ★ A non-404 blob error (throttle/transient/auth) is now a CLEAN "unavailable", not an unhandled 500.
            ArtifactLog.BlobError(_logger, artifact, id, ex.Status, ex);
            return ArtifactBlob.Unavailable;
        }
    }
}

/// <summary>High-performance (CA1848) log delegates for the artifact (trace/screenshot) proxy.</summary>
internal static partial class ArtifactLog
{
    [LoggerMessage(EventId = 4000, Level = LogLevel.Error,
        Message = "{Artifact} blob download failed for {RunId} (status {Status})")]
    public static partial void BlobError(ILogger logger, string artifact, long runId, int status, Exception ex);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Warning,
        Message = "{RunId} {Artifact} url is not an Azure Blob endpoint; refusing to proxy it")]
    public static partial void InvalidUrl(ILogger logger, string artifact, long runId);
}
