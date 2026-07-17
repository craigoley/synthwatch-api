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
///   • The spec is DATA: it rides an env override into the SANDBOX job (StartWithEnvOverrideAsync) as base64;
///     the sandbox re-allowlists its child env, so the override can carry no secret.
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
    private readonly ILogger<PreviewFunctions> _logger;

    public PreviewFunctions(
        SynthWatchDbContext db,
        IAuthPrincipal auth,
        IAuditScope audit,
        IRunnerJobTrigger runnerJob,
        IOptions<RunnerJobOptions> jobOptions,
        TokenCredential credential,
        IConfiguration config,
        ILogger<PreviewFunctions> logger)
    {
        _db = db;
        _auth = auth;
        _audit = audit;
        _runnerJob = runnerJob;
        _jobOptions = jobOptions.Value;
        _credential = credential;
        _config = config;
        _logger = logger;
    }

    // ── Bounds — a code-exec job-spawner MUST be bounded at the endpoint, not just the job ────────────────
    private const int MaxPerUserPerHour = 20;   // per-user rate limit
    private const int MaxConcurrentRunning = 3; // global concurrency cap (in-flight sandbox jobs)
    private const int MaxSpecBytes = 256 * 1024; // a spec is small; reject a body used to bloat the env override

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

        var specSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body.Spec))).ToLowerInvariant();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        // ★ INSERT the lifecycle row BEFORE starting the job (so rate/concurrency count it) + the audit trail.
        //   We store the HASH, never the body (the body rides the ephemeral env override; retention is a hash).
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

        // ★ Start the SANDBOX job with a per-run env override carrying the spec as DATA. The override is built
        //   server-side from NON-SECRET values only; the sandbox re-allowlists its child env (runner/sandbox), so
        //   this can smuggle no secret. The uploaded spec is one base64 value — it cannot add env keys.
        var specB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body.Spec));
        var started = await _runnerJob.StartWithEnvOverrideAsync(
            _jobOptions.SandboxJobName,
            _jobOptions.SandboxContainerName,
            new Dictionary<string, string>
            {
                ["SW_SANDBOX_SPEC_B64"] = specB64,
                ["SW_SANDBOX_TARGET_URL"] = targetUrl,
                ["SW_SANDBOX_RESULT_TOKEN"] = token,
            },
            ct);

        if (!started)
        {
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
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "preview/{token}")] HttpRequest req,
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
                row.Status = "timeout";
                row.CompletedAt = DateTimeOffset.UtcNow;
                row.Error = "no result within the sandbox timeout window";
                await _db.SaveChangesAsync(ct);
                return ApiResults.Ok(new PreviewStatusDto(token, "timeout", null));
            }
            return ApiResults.Ok(new PreviewStatusDto(token, "running", null)); // still in flight
        }

        row.Status = "done";
        row.CompletedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ApiResults.Ok(new PreviewStatusDto(token, "done", trace));
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
