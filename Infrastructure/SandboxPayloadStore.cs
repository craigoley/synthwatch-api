using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Writes and deletes the per-run {token}.payload ciphertext blob (see <see cref="SandboxPayload"/> for why
/// the channel exists). Injectable so the endpoint's payload lifecycle is testable without Azure.
/// </summary>
public interface ISandboxPayloadStore
{
    /// <summary>Write {token}.payload. False on any failure — the caller must NOT start the job.</summary>
    Task<bool> WriteAsync(string token, string ciphertext, CancellationToken ct);

    /// <summary>Delete {token}.payload. Idempotent; false only on a real failure (absent counts as done).</summary>
    Task<bool> DeleteAsync(string token, CancellationToken ct);
}

/// <summary>
/// Blob-backed <see cref="ISandboxPayloadStore"/>, using the API MI (no account key, no SAS — same
/// TokenCredential the trace proxies use).
///
/// ★ RBAC: this needs blobs/write + blobs/delete on the SANDBOX CONTAINER. The API MI was Storage Blob Data
/// READER there (read-only), so this class does not work until the companion runner-repo bicep change
/// promotes that container-scoped assignment to Storage Blob Data CONTRIBUTOR. Container-scoped, never
/// account-scoped. It does NOT touch verify_sandbox_least_privilege, which governs the SANDBOX MI's
/// exact-two set — this is the API MI, a different principal.
///
/// ★ NEVER LOGS THE CIPHERTEXT OR THE KEY. Failures log the blob status + the token (a non-secret, random
/// 32-hex handle already present in audit_log and the URL) and nothing else.
/// </summary>
public sealed class SandboxPayloadStore : ISandboxPayloadStore
{
    private readonly TokenCredential _credential;
    private readonly IConfiguration _config;
    private readonly RunnerJobOptions _jobOptions;
    private readonly ILogger<SandboxPayloadStore> _logger;

    public SandboxPayloadStore(
        TokenCredential credential,
        IConfiguration config,
        IOptions<RunnerJobOptions> jobOptions,
        ILogger<SandboxPayloadStore> logger)
    {
        _credential = credential;
        _config = config;
        _jobOptions = jobOptions.Value;
        _logger = logger;
    }

    /// <summary>Resolve the payload blob, or null when storage isn't configured (local dev).</summary>
    private BlobClient? Resolve(string token)
    {
        var account = _config["SandboxBlob:AccountName"] ?? _config["StorageAccountName"];
        var container = _config["SandboxBlob:Container"] ?? _jobOptions.SandboxContainerName;
        if (string.IsNullOrWhiteSpace(account)) return null;
        return new BlobClient(new Uri($"https://{account}.blob.core.windows.net/{container}/{token}.payload"), _credential);
    }

    public async Task<bool> WriteAsync(string token, string ciphertext, CancellationToken ct)
    {
        var blob = Resolve(token);
        if (blob is null)
        {
            SandboxPayloadLog.NotConfigured(_logger, token);
            return false;
        }
        try
        {
            // overwrite:false — the token is 128 bits of fresh randomness, so a pre-existing blob at this name
            // means something is wrong (a token collision, or a replay); refuse rather than clobber it.
            await blob.UploadAsync(BinaryData.FromString(ciphertext), overwrite: false, ct);
            return true;
        }
        catch (RequestFailedException ex)
        {
            // ★ A 403 here is the expected symptom until the container-scoped Contributor grant is live.
            //   Logged with the status so it is diagnosable, WITHOUT the ciphertext or the key.
            SandboxPayloadLog.WriteFailed(_logger, ex.Status, token, ex);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ★ NOT just RequestFailedException. A TokenCredential failure (AuthenticationFailedException,
            //   CredentialUnavailableException) or a socket/timeout error is not a service error, so it would
            //   escape this method, propagate past the already-inserted 'running' row, and 500 the caller
            //   WITHOUT a token — leaving a row nobody can ever poll, holding a concurrency slot for the full
            //   stale window. Three of those and the global cap 429s everyone. Auth failure is the LIKELIER
            //   failure mode while the Contributor grant is still landing, so it must return false (→ the row
            //   is marked failed and the caller gets a clean 503) exactly like a 403 does.
            SandboxPayloadLog.WriteFailedUnexpected(_logger, token, ex);
            return false;
        }
    }

    public async Task<bool> DeleteAsync(string token, CancellationToken ct)
    {
        var blob = Resolve(token);
        if (blob is null) return false;
        try
        {
            // Already-gone is SUCCESS: the sandbox deletes on read, so the overwhelmingly common case for the
            // sweep is "nothing to clean up". Only a real failure returns false.
            await blob.DeleteIfExistsAsync(DeleteSnapshotsOption.None, cancellationToken: ct);
            return true;
        }
        catch (RequestFailedException ex)
        {
            SandboxPayloadLog.DeleteFailed(_logger, ex.Status, token, ex);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same reasoning as WriteAsync: a credential/transport failure must not escape and 500 a poll.
            SandboxPayloadLog.DeleteFailedUnexpected(_logger, token, ex);
            return false;
        }
    }
}

/// <summary>High-performance (CA1848) log delegates. ★ Token + status only — never the payload or key.</summary>
internal static partial class SandboxPayloadLog
{
    [LoggerMessage(EventId = 5110, Level = LogLevel.Error,
        Message = "sandbox payload write failed (status {Status}) for token {Token} — the preview will not start")]
    public static partial void WriteFailed(ILogger logger, int status, string token, Exception ex);

    [LoggerMessage(EventId = 5111, Level = LogLevel.Warning,
        Message = "sandbox payload delete failed (status {Status}) for token {Token} — ciphertext may persist until the lifecycle rule expires it")]
    public static partial void DeleteFailed(ILogger logger, int status, string token, Exception ex);

    [LoggerMessage(EventId = 5112, Level = LogLevel.Error,
        Message = "sandbox payload storage is not configured (SandboxBlob:AccountName / StorageAccountName) — cannot start preview {Token}")]
    public static partial void NotConfigured(ILogger logger, string token);

    [LoggerMessage(EventId = 5113, Level = LogLevel.Error,
        Message = "sandbox payload write failed with a non-service error (credential/transport) for token {Token} — the preview will not start")]
    public static partial void WriteFailedUnexpected(ILogger logger, string token, Exception ex);

    [LoggerMessage(EventId = 5114, Level = LogLevel.Warning,
        Message = "sandbox payload delete failed with a non-service error for token {Token} — ciphertext may persist until the lifecycle rule expires it")]
    public static partial void DeleteFailedUnexpected(ILogger logger, string token, Exception ex);
}
