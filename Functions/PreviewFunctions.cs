using System.Security.Cryptography;
using System.Text;

using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Azure.Functions.Worker;

using SynthWatch.Api.Data;
using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;

namespace SynthWatch.Api.Functions;

/// <summary>
/// Spec preview-run (the "Tests" area). POST /api/preview compiles+runs an UPLOADED spec in the LOW-PRIVILEGE
/// synthwatch-sandbox ACA job (a separate secret-free identity — see infra/main.bicep + runner/sandbox) and
/// returns a token; GET /api/preview/{token} polls the sandbox-artifacts blob for the trace. It NEVER writes a
/// check / the fleet / spec_cache — the only path to a real monitor stays the repo PR.
///
/// ★ A CODE-EXECUTION SURFACE. Everything the safety of this depends on is baked in here, not bolted on:
///   • HARD auth gate — editor/admin, resolved from the bearer session (not merely the by-verb middleware).
///   • A NON-OPTIONAL audit row (who / what-hash / when) on every trigger.
///   • Rate limit + concurrency cap + a per-job hard timeout — an unbounded logged-in job-spawner is a DoS on
///     the Azure bill.
///   • The spec is DATA: it travels as CIPHERTEXT in the {token}.payload blob, NOT in the ARM env override.
///     ACA persists a jobs/start override verbatim in job execution history (readable by any Reader on the
///     RG, unbounded-by-contract for a Manual-trigger job), so the env now carries only the per-run AES key —
///     worthless without the blob. See Infrastructure/SandboxPayload.cs for the full rationale.
///   • OPTIONAL user-typed credentials ride the SAME sealed payload and are NEVER persisted: not in
///     sandbox_preview (spec_sha256 only), not in audit_log ({specSha256, targetUrl} only), not in the ARM
///     body, not in any log or exception path.
/// </summary>
public class PreviewFunctions
{
    private readonly SynthWatchDbContext _db;
    private readonly IAuthPrincipal _auth;
    private readonly IAuditScope _audit;
    private readonly IRunnerJobTrigger _runnerJob;
    private readonly RunnerJobOptions _jobOptions;
    private readonly TokenCredential _credential;
    private readonly IConfiguration _config;
    private readonly ISandboxPayloadStore _payloads;
    private readonly ILogger<PreviewFunctions> _logger;

    public PreviewFunctions(
        SynthWatchDbContext db,
        IAuthPrincipal auth,
        IAuditScope audit,
        IRunnerJobTrigger runnerJob,
        IOptions<RunnerJobOptions> jobOptions,
        TokenCredential credential,
        IConfiguration config,
        ISandboxPayloadStore payloads,
        ILogger<PreviewFunctions> logger)
    {
        _db = db;
        _auth = auth;
        _audit = audit;
        _runnerJob = runnerJob;
        _jobOptions = jobOptions.Value;
        _credential = credential;
        _config = config;
        _payloads = payloads;
        _logger = logger;
    }

    // ── Bounds — a code-exec job-spawner MUST be bounded at the endpoint, not just the job ────────────────
    private const int MaxPerUserPerHour = 20;   // per-user rate limit
    private const int MaxConcurrentRunning = 3; // global concurrency cap (in-flight sandbox jobs)
    private const int MaxSpecBytes = 256 * 1024; // a spec is small; reject a body used to bloat the env override
    // A password / username / bypass token is short. Bounding them keeps the sealed payload small and stops a
    // caller using the credential fields as an unbounded side-channel now that the spec itself is capped.
    private const int MaxCredentialFieldChars = 1024;
    // How many abandoned rows one request may sweep — bounds the create path's added work.
    private const int MaxSweepPerRequest = 10;

    /// <summary>Trim + drop empty credential fields; return null when nothing usable remains, so an
    /// uncredentialed preview carries NO credentials node at all — not empty strings the runner would then
    /// have to normalize away. (It is NOT "byte-identical to the pre-feature behaviour": this PR moves the
    /// spec off SW_SANDBOX_SPEC_B64 for every preview, credentialed or not. What is unchanged is the
    /// user-visible behaviour and the runner's non-sensitive treatment — raw trace, screenshot kept.)</summary>
    private static SandboxPayload.Credentials? NormalizeCredentials(CreatePreviewCredentials? c)
    {
        if (c is null) return null;
        var username = string.IsNullOrWhiteSpace(c.Username) ? null : c.Username.Trim();
        // ★ Password / token are NOT trimmed — leading or trailing whitespace can be significant in a real
        //   secret, and silently "fixing" it would make a correct credential fail authentication mysteriously.
        //   But an ENTIRELY-whitespace value is not a credential, and accepting one is actively harmful: it
        //   makes the run `sensitive` (suppressing the screenshot for a credential that authenticates
        //   nothing) AND registers e.g. "   " as a redactor knownValue — whose only guard is a <3-char skip,
        //   which three spaces clears — so every run of three spaces in the trace becomes <redacted> and the
        //   diagnostic is shredded. Reject whitespace-only; preserve whitespace INSIDE a real value.
        var password = string.IsNullOrWhiteSpace(c.Password) ? null : c.Password;
        var bypassToken = string.IsNullOrWhiteSpace(c.VercelBypassToken) ? null : c.VercelBypassToken;
        if (username is null && password is null && bypassToken is null) return null;
        return new SandboxPayload.Credentials(username, password, bypassToken);
    }

    /// <summary>
    /// Flip abandoned 'running' rows to 'timeout' and delete their payload ciphertext.
    ///
    /// ★ Called from the CREATE path, not just the poll, because an abandoned preview by definition has
    /// nobody polling it. A row only reaches here once (the status flip is what excludes it next time), and
    /// the delete is idempotent (DeleteIfExists), so a repeated sweep is cheap. Best-effort throughout: a
    /// failed delete leaves the lifecycle rule as the backstop and must never block a new preview.
    /// </summary>
    private async Task SweepOrphanedPayloadsAsync(DateTimeOffset staleBefore, CancellationToken ct)
    {
        var abandoned = await _db.SandboxPreviews
            .Where(p => p.Status == "running" && p.RequestedAt < staleBefore)
            .OrderBy(p => p.RequestedAt)
            .Take(MaxSweepPerRequest)
            .ToListAsync(ct);
        if (abandoned.Count == 0) return;
        foreach (var row in abandoned)
        {
            await _payloads.DeleteAsync(row.Token, ct);
            row.Status = "timeout";
            row.CompletedAt = DateTimeOffset.UtcNow;
            row.Error = "no result within the sandbox timeout window";
        }
        await _db.SaveChangesAsync(ct);
    }

    private static bool CredentialsWithinBounds(SandboxPayload.Credentials c) =>
        (c.Username?.Length ?? 0) <= MaxCredentialFieldChars
        && (c.Password?.Length ?? 0) <= MaxCredentialFieldChars
        && (c.BypassToken?.Length ?? 0) <= MaxCredentialFieldChars;

    // ★ A 'running' row is only presumed IN-FLIGHT within this window. The sandbox job's hard replicaTimeout is
    //   180s, so a row still 'running' after this is abandoned (closed tab, job died without writing the blob) —
    //   it MUST NOT count toward the concurrency cap, or 3 stuck rows permanently 429 the feature for everyone
    //   with no recovery path. The concurrency query bounds by this window; GET lazily sweeps stale rows.
    private static readonly TimeSpan RunningStaleAfter = TimeSpan.FromMinutes(5);

    /// <summary>POST /api/preview { spec, targetUrl? } — enqueue + start a sandbox preview. 202 { token }.</summary>
    [Function("CreatePreview")]
    public async Task<IActionResult> CreatePreview(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "preview")] HttpRequest req,
        CancellationToken ct)
    {
        // ★ HARD auth gate — resolve the session and require editor/admin HERE (belt over the by-verb middleware):
        //   a code-exec trigger must be unreachable unauthenticated even if the middleware is ever misconfigured.
        var principal = await _auth.FromBearerAsync(req.Headers.Authorization, ct);
        if (principal is null) return ApiResults.Unauthorized("Authentication required.");
        if (!principal.CanWrite) return ApiResults.Forbidden("You do not have permission to run a preview.");

        var (body, bodyError) = await RequestJson.ReadAsync<CreatePreviewRequest>(req, ct);
        if (bodyError is not null) return bodyError;
        if (body is null || string.IsNullOrWhiteSpace(body.Spec))
            return ApiResults.BadRequest("A spec is required.");
        if (Encoding.UTF8.GetByteCount(body.Spec) > MaxSpecBytes)
            return ApiResults.BadRequest($"Spec too large (max {MaxSpecBytes / 1024} KB).");

        // Target: a caller-supplied non-prod / public URL, or the safe default. (Tier-3 authed-against-staging
        // is a SEPARATE gated capability — pass 1 is unauthenticated against a public/non-prod target.)
        var targetUrl = string.IsNullOrWhiteSpace(body.TargetUrl) ? "https://example.com" : body.TargetUrl!.Trim();
        // ★ SSRF-adjacent: the target drives the sandbox's outbound fetch. Require an ABSOLUTE http(s) URI — no
        //   file:/gopher:/relative that could steer the fetch somewhere unintended. (The sandbox's own isolation
        //   + non-prod default bound the blast radius; this is defense-in-depth at the boundary.)
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            return ApiResults.BadRequest("targetUrl must be an absolute http(s) URL.");

        // ★ OPTIONAL per-run credentials. Normalized to null when nothing usable was sent, so an uncredentialed
        //   POST sends NO credentials node — the runner keys its whole `sensitive` treatment off "did any
        //   credential arrive?", and an empty-string field would read as "yes" and silently make the run
        //   sensitive (screenshot suppressed) for a credential that does not exist.
        var credentials = NormalizeCredentials(body.Credentials);
        if (credentials is not null && !CredentialsWithinBounds(credentials))
            return ApiResults.BadRequest($"Each credential field must be at most {MaxCredentialFieldChars} characters.");

        var now = DateTimeOffset.UtcNow;
        // ★ Rate limit (per-user) + concurrency cap (global) — read from sandbox_preview, the audit source itself.
        var sinceHour = now.AddHours(-1);
        var recent = await _db.SandboxPreviews.CountAsync(p => p.ActorEmail == principal.Email && p.RequestedAt >= sinceHour, ct);
        if (recent >= MaxPerUserPerHour)
            return ApiResults.TooManyRequests($"Preview rate limit reached ({MaxPerUserPerHour}/hour). Try again later.");
        // ★ Count only RECENTLY-'running' rows (within RunningStaleAfter) — an abandoned/crashed preview that
        //   never left 'running' must not permanently hold a concurrency slot and 429 everyone (self-DoS).
        var staleBefore = now - RunningStaleAfter;
        var running = await _db.SandboxPreviews.CountAsync(p => p.Status == "running" && p.RequestedAt >= staleBefore, ct);
        if (running >= MaxConcurrentRunning)
            return ApiResults.TooManyRequests($"Too many previews running ({MaxConcurrentRunning}). Try again shortly.");

        // ★ ORPHAN SWEEP ON THE CREATE PATH — the one that actually covers the abandoned case.
        //   The sweep in GetPreview only fires IF THE CLIENT POLLS, so it cannot clean up the very scenario
        //   it names: a closed tab means nobody polls, nothing sweeps, and the ciphertext survives to the
        //   ~1-day lifecycle floor while its key sits in ACA execution history permanently — both halves of
        //   the split reachable for ~24h. Sweeping here means the NEXT preview by ANY user cleans up earlier
        //   orphans, so cleanup no longer depends on the abandoning client coming back.
        //   Bounded to a handful of rows so a create never turns into a long scan.
        await SweepOrphanedPayloadsAsync(staleBefore, ct);

        var specSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body.Spec))).ToLowerInvariant();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        // ★ INSERT the lifecycle row BEFORE starting the job (so rate/concurrency count it) + the audit trail.
        //   ★ We store the spec HASH and NOTHING ELSE of the request body. The spec travels as ciphertext in
        //   the ephemeral payload blob; the credentials travel with it and are NEVER written here, never in the
        //   audit `after` object below, and never in row.Error (whose messages are all fixed strings).
        var row = new SandboxPreview
        {
            Token = token,
            ActorEmail = principal.Email,
            ActorIp = req.HttpContext.Connection.RemoteIpAddress?.ToString(),
            SpecSha256 = specSha,
            TargetUrl = targetUrl,
            Status = "running",
        };
        _db.SandboxPreviews.Add(row);
        await _db.SaveChangesAsync(ct);
        _audit.Record("sandbox_preview", token, before: null, after: new { specSha256 = specSha, targetUrl }, note: "preview-run");

        // ★ SEAL the spec + any typed credentials under a FRESH per-run AES-256 key, and write the ciphertext
        //   to the private {token}.payload blob. The key never touches the DB and is not CRED_ENC_KEY.
        var sealedPayload = SandboxPayload.Seal(body.Spec, credentials);
        if (!await _payloads.WriteAsync(token, sealedPayload.Ciphertext, ct))
        {
            // ★ FAIL BEFORE STARTING. Starting the job now would run it with a key but no ciphertext, and the
            //   sandbox fails closed on exactly that — so this would burn a job execution to reach the same
            //   outcome, minus the clear error. The message is generic: it must not hint at credential content.
            row.Status = "failed";
            row.CompletedAt = DateTimeOffset.UtcNow;
            row.Error = "could not stage the preview payload";
            await _db.SaveChangesAsync(ct);
            return ApiResults.ServiceUnavailable("Could not stage the preview — try again.");
        }

        // ★ Start the SANDBOX job. The env override now carries ONLY the per-run key + the token + the target —
        //   NO SW_SANDBOX_SPEC_B64, and no credential. Everything here is safe to see in ACA execution history:
        //   the key decrypts nothing without the blob (which the sandbox deletes on read), the token is a random
        //   handle already in audit_log, and the target is the user's own non-prod URL.
        //   ★ NON-WIDENING, UNCHANGED: the caller still passes LITERAL keys with values only. The container's
        //   image / command / args / resources still come from the GET'd job template (ArmRunnerJobTrigger), so
        //   this cannot alter what runs — only what it reads.
        var started = await _runnerJob.StartWithEnvOverrideAsync(
            _jobOptions.SandboxJobName,
            _jobOptions.SandboxContainerName,
            new Dictionary<string, string>
            {
                ["SW_SANDBOX_CRED_KEY"] = sealedPayload.KeyBase64,
                ["SW_SANDBOX_TARGET_URL"] = targetUrl,
                ["SW_SANDBOX_RESULT_TOKEN"] = token,
            },
            ct);

        if (!started)
        {
            // ★ The job never started, so nothing will ever read (and therefore delete) the payload. Clean it up
            //   NOW rather than leaving ciphertext to the ~1-day lifecycle floor while its key sits in this
            //   failed execution's history. Best-effort: a failed delete still leaves the lifecycle backstop.
            await _payloads.DeleteAsync(token, ct);
            row.Status = "failed";
            row.CompletedAt = DateTimeOffset.UtcNow;
            row.Error = "could not start the sandbox job";
            await _db.SaveChangesAsync(ct);
            return ApiResults.ServiceUnavailable("Could not start the preview job — try again.");
        }

        return ApiResults.Accepted(new CreatePreviewAcceptedDto(token));
    }

    /// <summary>GET /api/preview/{token} — poll for the trace. 'running' until the sandbox writes its result.</summary>
    [Function("GetPreview")]
    public async Task<IActionResult> GetPreview(
        // ★ Constrain {token} to its real 32-hex shape — else the literal `preview/quota` route is SHADOWED by
        //   this parameter route (Functions does not give the literal precedence), and GET /preview/quota 404s
        //   as "Preview quota not found". length(32) also rejects junk tokens before a DB lookup.
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "preview/{token:length(32)}")] HttpRequest req,
        string token,
        CancellationToken ct)
    {
        // ★ Editor/admin only — a preview trace is an editor capability (the uploaded spec's output). A viewer
        //   has no business polling it, even with the token. Same hard gate as the POST.
        var principal = await _auth.FromBearerAsync(req.Headers.Authorization, ct);
        if (principal is null) return ApiResults.Unauthorized("Authentication required.");
        if (!principal.CanWrite) return ApiResults.Forbidden("You do not have permission to view a preview.");

        // ★ The trace can carry uploaded-spec output — never cache it (mirrors the #218 forensic-read no-store).
        req.HttpContext.Response.Headers.CacheControl = "no-store";

        var row = await _db.SandboxPreviews.FirstOrDefaultAsync(p => p.Token == token, ct);
        if (row is null) return ApiResults.NotFound($"Preview {token} not found.");

        // Terminal already → return the stored status (the trace, once fetched, is not re-polled).
        if (row.Status != "running")
            return ApiResults.Ok(new PreviewStatusDto(token, row.Status, null));

        // ★ Poll ONLY the sandbox container (the one the sandbox MI can write) — NEVER a prod-traces container.
        var trace = await TryReadSandboxTraceAsync(token, ct);
        if (trace is null)
        {
            // ★ Lazy sweep — a row still 'running' past the sandbox's hard timeout (with no blob) is abandoned
            //   (closed tab / job died). Flip it to 'timeout' so it stops holding a concurrency slot forever.
            if (DateTimeOffset.UtcNow - row.RequestedAt > RunningStaleAfter)
            {
                // ★ ORPHANED-PAYLOAD SWEEP (the polled case). The sandbox deletes {token}.payload on read, so
                //   normally there is nothing here. A preview that DIED between our upload and that read
                //   (replica eviction, image-pull failure, the 180s replicaTimeout) leaves ciphertext behind,
                //   and the only other cleanup is the lifecycle rule, whose floor is ~1 DAY
                //   (daysAfterCreationGreaterThan is typed Integer — no fractional days — and a policy edit
                //   takes up to 24h to take effect) while the run's key sits in execution history permanently.
                //   ★ THIS BRANCH ONLY FIRES IF THE CLIENT POLLS, so on its own it does NOT cover an abandoned
                //   tab — which is the likeliest way a preview is orphaned. SweepOrphanedPayloadsAsync on the
                //   CREATE path is what actually closes that; this is the fast path for a client still
                //   watching. Best-effort: a failed delete leaves the lifecycle backstop and must not block
                //   the status transition.
                await _payloads.DeleteAsync(token, ct);
                row.Status = "timeout";
                row.CompletedAt = DateTimeOffset.UtcNow;
                row.Error = "no result within the sandbox timeout window";
                await _db.SaveChangesAsync(ct);
                return ApiResults.Ok(new PreviewStatusDto(token, "timeout", null));
            }
            return ApiResults.Ok(new PreviewStatusDto(token, "running", null)); // still in flight
        }

        // Terminal-success sweep too: the sandbox should already have deleted the payload on read, so this is
        // a belt-and-braces no-op that costs one idempotent DeleteIfExists and closes the case where the job
        // wrote its result but died before (or while) deleting.
        await _payloads.DeleteAsync(token, ct);
        row.Status = "done";
        row.CompletedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ApiResults.Ok(new PreviewStatusDto(token, "done", trace));
    }

    /// <summary>GET /api/preview/quota — the caller's live bounds (running/hourly + the caps) so the UI can show
    /// "N of M" and explain a 429 instead of it being a mystery. Editor/admin only (same gate as run/poll); the
    /// counts are the caller's own. These are the EXACT queries the POST enforces — one source of truth.</summary>
    [Function("GetPreviewQuota")]
    public async Task<IActionResult> GetPreviewQuota(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "preview/quota")] HttpRequest req,
        CancellationToken ct)
    {
        var principal = await _auth.FromBearerAsync(req.Headers.Authorization, ct);
        if (principal is null) return ApiResults.Unauthorized("Authentication required.");
        if (!principal.CanWrite) return ApiResults.Forbidden("You do not have permission to view preview quota.");

        req.HttpContext.Response.Headers.CacheControl = "no-store";
        var now = DateTimeOffset.UtcNow;
        var sinceHour = now.AddHours(-1);
        var staleBefore = now - RunningStaleAfter;
        var hourly = await _db.SandboxPreviews.CountAsync(p => p.ActorEmail == principal.Email && p.RequestedAt >= sinceHour, ct);
        var running = await _db.SandboxPreviews.CountAsync(p => p.Status == "running" && p.RequestedAt >= staleBefore, ct);
        return ApiResults.Ok(new PreviewQuotaDto(running, MaxConcurrentRunning, hourly, MaxPerUserPerHour));
    }

    /// <summary>GET /api/preview/{token}/screenshot — STREAM the failure screenshot through the API (the sandbox
    /// container is private; the API MI reads it and proxies the bytes — no SAS, no widening, no raw URL). Editor
    /// only; no-store (uploaded-spec output).</summary>
    [Function("GetPreviewScreenshot")]
    public async Task<IActionResult> GetPreviewScreenshot(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "preview/{token:length(32)}/screenshot")] HttpRequest req,
        string token,
        CancellationToken ct)
        => await StreamSandboxArtifactAsync(req, token, "screenshot.png", "image/png", attachment: false, ct);

    /// <summary>GET /api/preview/{token}/trace — STREAM the Playwright trace.zip through the API (same private-
    /// container, MI-read, no-SAS/no-widening proxy as the screenshot). Editor only; no-store.</summary>
    [Function("GetPreviewTrace")]
    public async Task<IActionResult> GetPreviewTrace(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "preview/{token:length(32)}/trace")] HttpRequest req,
        string token,
        CancellationToken ct)
        => await StreamSandboxArtifactAsync(req, token, "trace.zip", "application/zip", attachment: true, ct);

    /// <summary>Editor-gated stream-through of a sandbox artifact blob (`{token}/{name}`) via the API MI. Keeps the
    /// container private (no SAS ⇒ no Storage Blob Delegator ⇒ no widening; the MI stays Reader-on-the-sandbox-
    /// container only). 404 → not written (still running / no such artifact); other blob errors → 503.</summary>
    private async Task<IActionResult> StreamSandboxArtifactAsync(
        HttpRequest req, string token, string name, string contentType, bool attachment, CancellationToken ct)
    {
        var principal = await _auth.FromBearerAsync(req.Headers.Authorization, ct);
        if (principal is null) return ApiResults.Unauthorized("Authentication required.");
        if (!principal.CanWrite) return ApiResults.Forbidden("You do not have permission to view a preview.");
        req.HttpContext.Response.Headers.CacheControl = "no-store";

        var account = _config["SandboxBlob:AccountName"] ?? _config["StorageAccountName"];
        var container = _config["SandboxBlob:Container"] ?? _jobOptions.SandboxContainerName;
        if (string.IsNullOrWhiteSpace(account)) return ApiResults.NotFound($"Preview {name} not found.");
        try
        {
            var uri = new Uri($"https://{account}.blob.core.windows.net/{container}/{token}/{name}");
            var blob = new BlobClient(uri, _credential);
            var resp = await blob.DownloadStreamingAsync(cancellationToken: ct);
            if (attachment)
                req.HttpContext.Response.Headers.ContentDisposition = $"attachment; filename=\"preview-{token}-{name}\"";
            return new FileStreamResult(resp.Value.Content, contentType); // FileStreamResult disposes the stream
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return ApiResults.NotFound($"Preview {name} not found (the run is still in flight, or produced no {name}).");
        }
        catch (RequestFailedException ex)
        {
            PreviewLog.BlobReadFailed(_logger, ex.Status, token, ex);
            return ApiResults.ServiceUnavailable($"Preview {name} is temporarily unavailable.");
        }
    }

    /// <summary>Read the sandbox job's trace result from the DEDICATED sandbox container only. null = not yet
    /// written (still running). Uses the API MI (needs Blob Data Reader on the sandbox container — infra).</summary>
    private async Task<string?> TryReadSandboxTraceAsync(string token, CancellationToken ct)
    {
        var account = _config["SandboxBlob:AccountName"] ?? _config["StorageAccountName"];
        var container = _config["SandboxBlob:Container"] ?? _jobOptions.SandboxContainerName;
        if (string.IsNullOrWhiteSpace(account)) return null;
        try
        {
            var uri = new Uri($"https://{account}.blob.core.windows.net/{container}/{token}.json");
            var blob = new BlobClient(uri, _credential);
            var resp = await blob.DownloadContentAsync(ct);
            return resp.Value.Content.ToString();
        }
        catch (RequestFailedException ex)
        {
            // 404 = not written yet (still running). ★ ANY other blob failure — a 403 (the API-MI Blob-Reader
            //   grant on #332 not yet live) or a transient throttle — must DEGRADE to "still polling", never a
            //   shielded 500 on every GET. Log it so a persistent 403 is visible, then treat as not-ready.
            if (ex.Status != 404)
                PreviewLog.BlobReadFailed(_logger, ex.Status, token, ex);
            return null;
        }
    }
}

/// <summary>High-performance (CA1848) log delegate for the preview blob-read path.</summary>
internal static partial class PreviewLog
{
    [LoggerMessage(EventId = 5101, Level = LogLevel.Warning,
        Message = "preview blob read failed (status {Status}) for token {Token} — degrading to still-polling")]
    public static partial void BlobReadFailed(ILogger logger, int status, string token, Exception ex);
}
