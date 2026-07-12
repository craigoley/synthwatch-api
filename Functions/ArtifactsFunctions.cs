using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using SynthWatch.Api.Data;
using SynthWatch.Api.Infrastructure;

namespace SynthWatch.Api.Functions;

/// <summary>
/// Runner-written artifact proxying — Playwright traces, the per-monitor success-trace baseline, failure
/// screenshots, and the server-side trace-signals extraction. All read blobs from the artifacts account
/// through the API's managed identity (the account blocks public access), via the shared <see cref="IArtifactReader"/>
/// (one blob-host allowlist + download + 404/non-404 classification, previously duplicated 3×). Split out of
/// RunsFunctions so that file holds only non-artifact run responsibilities.
///
/// ★ SECURITY — these four endpoints serve RAW FORENSIC DATA (trace zips with request/response bodies +
/// console text, failure screenshots, extracted signals) and BYPASS the B10 redaction apparatus that protects
/// sensitive=true monitors. Run/check ids are sequential bigints, so left open they let the whole fleet's
/// forensic history be anonymously enumerated. Unlike the status/read-open endpoints (correct for aggregate
/// status data), the forensic-artifact CLASS requires a valid EDITOR/ADMIN session — see
/// <see cref="RequireSessionAsync"/>. Flag-gated on AUTH_ENFORCEMENT_ENABLED (like the write-gate): inert when
/// off (deploy-safe), enforces in prod where it's true. The paired dashboard trace-proxy PR must forward the
/// session bearer or the viewer 401s for logged-in users.
/// </summary>
public class ArtifactsFunctions
{
    private readonly SynthWatchDbContext _db;
    private readonly IArtifactReader _artifacts;
    private readonly IBlobSasMinter _sasMinter;
    private readonly IAuthPrincipal _auth;

    // Deserialize the persisted trace_signals (runner-written camelCase JSON) into the same DTO FromZip returns,
    // so both the persisted and re-extracted paths serialize identically to the client.
    private static readonly System.Text.Json.JsonSerializerOptions WebJson =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    public ArtifactsFunctions(SynthWatchDbContext db, IArtifactReader artifacts, IBlobSasMinter sasMinter, IAuthPrincipal auth)
    {
        _db = db;
        _artifacts = artifacts;
        _sasMinter = sasMinter;
        _auth = auth;
    }

    /// <summary>
    /// Forensic-artifact auth gate. Requires a valid EDITOR or ADMIN session (NOT admin-only; logged-in users
    /// legitimately need traces to debug). 401 problem+json when there is no valid session; 403 when the
    /// session is valid but its LIVE role is neither editor nor admin — i.e. a REVOKED editor whose session
    /// hasn't expired. That role floor mirrors the write-gate (<see cref="AuthGate.Decide"/>: a removed editor
    /// resolves to <see cref="Roles.Anonymous"/> and is denied), so a removed editor loses forensic access at
    /// the same instant they lose write access — never "can't write but can still pull sensitive traces".
    /// (The login flow only ever mints a session for an editor/admin — <c>AuthFunctions.Verify</c> rejects an
    /// anonymous role — so this floor bites ONLY the post-mint revocation case; defense in depth.)
    ///
    /// ★ Flag-gated on AUTH_ENFORCEMENT_ENABLED — the SAME switch the write-gate uses — so it's deploy-safe
    /// (inert when off, today's behavior) and actually rejects in prod (where enforcement is true). The
    /// middleware's verb-gate can't cover this: a GET is always Allow there (reads are open by default), so
    /// these forensic reads must self-guard. Resolves the caller from the bearer via the same
    /// <see cref="IAuthPrincipal"/> the middleware uses — role derived from DB/env, never trusted from a header.
    /// </summary>
    private Task<IActionResult?> RequireSessionAsync(HttpRequest req, CancellationToken ct) =>
        SessionReadGate.RequireSessionAsync(_auth, req, ct); // the shared #154 gate (extracted; same semantics)

    /// <summary>
    /// GET /api/runs/{id}/trace — streams the run's Playwright trace.zip from Blob (the API proxies
    /// it with its managed identity; the trace blob is never publicly exposed). 404 when the run
    /// has no trace. Keeps trace access behind the same API auth model as everything else.
    /// </summary>
    [Function("GetRunTrace")]
    public async Task<IActionResult> GetRunTrace(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "runs/{id:long}/trace")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        if (await RequireSessionAsync(req, ct) is { } denied) return denied;
        var url = await _db.Runs.AsNoTracking()
            .Where(r => r.Id == id).Select(r => r.TraceUrl).FirstOrDefaultAsync(ct);
        return await ProxyAsync(req, id, $"run {id}", url, "trace", "application/zip",
            $"attachment; filename=\"trace-run-{id}.zip\"", ct);
    }

    /// <summary>
    /// GET /api/runs/{id}/trace-sas — mint a SHORT-TTL, READ-ONLY, SINGLE-BLOB user-delegation SAS for the
    /// run's trace zip and return its URL as JSON, so the browser fetches the (up to 124 MB+) blob DIRECTLY —
    /// off the Vercel serverless proxy that terminates a streamed multi-tens-of-MB transfer at its ~15 s
    /// maxDuration. This is the PRIMARY trace path; /trace stays as a same-model streaming fallback. Behind the
    /// SAME <see cref="RequireSessionAsync"/> forensic gate as /trace — no session, no SAS. 404 semantics match
    /// /trace (no run / no trace_url → 404); the caller can never mint a SAS for a run the gate denies.
    /// </summary>
    [Function("GetRunTraceSas")]
    public async Task<IActionResult> GetRunTraceSas(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "runs/{id:long}/trace-sas")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        if (await RequireSessionAsync(req, ct) is { } denied) return denied;
        var url = await _db.Runs.AsNoTracking()
            .Where(r => r.Id == id).Select(r => r.TraceUrl).FirstOrDefaultAsync(ct);
        return await MintTraceSasAsync(req, $"run {id}", url, "trace", ct);
    }

    /// <summary>
    /// GET /api/checks/{id}/success-trace-sas — the per-MONITOR mirror of /runs/{id}/trace-sas: a short-TTL
    /// read-only single-blob SAS for the check's last-known-good success-trace zip. Same gate, same scope, same
    /// 404-until-baseline semantics as /checks/{id}/success-trace.
    /// </summary>
    [Function("GetCheckSuccessTraceSas")]
    public async Task<IActionResult> GetCheckSuccessTraceSas(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "checks/{id:long}/success-trace-sas")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        if (await RequireSessionAsync(req, ct) is { } denied) return denied;
        var url = await _db.Checks.AsNoTracking()
            .Where(c => c.Id == id).Select(c => c.SuccessTraceUrl).FirstOrDefaultAsync(ct);
        return await MintTraceSasAsync(req, $"monitor {id}", url, "success trace", ct);
    }

    /// <summary>Mint a read SAS for a resolved artifact blob url and return it as JSON (no-store — it's a
    /// short-lived credential). 404 for a missing url/host, a clean 503 for a delegation/signing error.</summary>
    private async Task<IActionResult> MintTraceSasAsync(
        HttpRequest req, string subject, string? blobUrl, string artifact, CancellationToken ct)
    {
        var sas = await _sasMinter.MintReadSasAsync(blobUrl, ct);
        switch (sas.Status)
        {
            case SasStatus.Missing:
                return ApiResults.NotFound($"No {artifact} for {subject}.");
            case SasStatus.Unavailable:
                return ApiResults.ServiceUnavailable($"the {artifact} for {subject} is temporarily unavailable.");
            default:
                req.HttpContext.Response.Headers.CacheControl = "no-store"; // never cache a bearer-in-URL
                return ApiResults.Ok(new Dtos.TraceSasDto(sas.Url!, sas.ExpiresOn!.Value));
        }
    }

    /// <summary>
    /// GET /api/checks/{id}/success-trace — streams a MONITOR's last-known-good (most-recent-success)
    /// Playwright trace.zip from Blob, via the same managed-identity proxy as run traces. This is the
    /// per-monitor baseline at the stable, purge-exempt `success-latest/check-&lt;id&gt;.zip` key
    /// (checks.success_trace_url), overwritten on each success — NOT a per-run trace. 404 until the
    /// monitor has had a success with capture enabled. Lets the dashboard embed it like any trace.
    /// </summary>
    [Function("GetCheckSuccessTrace")]
    public async Task<IActionResult> GetCheckSuccessTrace(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "checks/{id:long}/success-trace")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        if (await RequireSessionAsync(req, ct) is { } denied) return denied;
        var url = await _db.Checks.AsNoTracking()
            .Where(c => c.Id == id).Select(c => c.SuccessTraceUrl).FirstOrDefaultAsync(ct);
        return await ProxyAsync(req, id, $"monitor {id}", url, "success trace", "application/zip",
            $"attachment; filename=\"success-trace-check-{id}.zip\"", ct);
    }

    /// <summary>
    /// GET /api/runs/{id}/screenshot — streams the run's failure screenshot from Blob via the same
    /// proxy as traces (the artifacts account blocks public access, so the raw blob URL 409s). 404
    /// when the run has no screenshot. Served inline so the dashboard &lt;img&gt; renders it.
    /// </summary>
    [Function("GetRunScreenshot")]
    public async Task<IActionResult> GetRunScreenshot(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "runs/{id:long}/screenshot")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        if (await RequireSessionAsync(req, ct) is { } denied) return denied;
        var url = await _db.Runs.AsNoTracking()
            .Where(r => r.Id == id).Select(r => r.ScreenshotUrl).FirstOrDefaultAsync(ct);
        return await ProxyAsync(req, id, $"run {id}", url, "screenshot", "image/png",
            $"inline; filename=\"screenshot-run-{id}.png\"", ct);
    }

    /// <summary>
    /// GET /api/runs/{id}/trace-signals — the compact, FILTERED summary extracted from the run's Playwright
    /// trace (network waterfall + real site console errors), NOT the ~18 MB trace itself. The API reads the
    /// trace blob with its managed identity (same proxy posture as /trace) and parses it server-side. A GET
    /// (read-only). Non-fatal: no trace → 404; an unparseable trace → a 200 empty summary, never a 500.
    /// </summary>
    [Function("GetTraceSignals")]
    public async Task<IActionResult> GetTraceSignals(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "runs/{id:long}/trace-signals")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        if (await RequireSessionAsync(req, ct) is { } denied) return denied;
        var row = await _db.Runs.AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new { r.TraceUrl, r.TraceSignals, Target = r.Check!.TargetUrl })
            .FirstOrDefaultAsync(ct);

        if (row is null) return ApiResults.NotFound($"Run {id} not found.");

        // ★ Prefer the PERSISTED trace_signals (runner-extracted at capture, #114 — and ALREADY REDACTED for a
        // sensitive monitor). It's available even when there is no downloadable trace_url: a sensitive monitor's
        // GREEN run stores no zip (B10) but does persist signals. Serving it here means such a run shows its
        // redacted summary instead of 404ing as "no trace" — and it avoids a blob download for every other run
        // too. Only fall back to re-extracting from the trace zip when the column is empty (legacy/pre-#114 rows).
        if (!string.IsNullOrEmpty(row.TraceSignals))
        {
            // Guarded like the sibling reader (LocationDiffFunctions.ResolveSignalsAsync): jsonb guarantees
            // syntactic validity but NOT that the stored shape still matches TraceSignalsDto (a runner-side
            // schema drift — the very risk TraceSignalsGoldenParityTests exists for — would throw). Honor this
            // endpoint's "unparseable → never a 500" contract: on drift, fall through to zip re-extraction / 404.
            try
            {
                var persisted = System.Text.Json.JsonSerializer.Deserialize<Dtos.TraceSignalsDto>(row.TraceSignals, WebJson);
                if (persisted is not null) return ApiResults.Ok(persisted);
            }
            catch (System.Text.Json.JsonException) { /* drifted persisted JSON → fall through, never a 500 */ }
        }
        if (string.IsNullOrEmpty(row.TraceUrl)) return ApiResults.NotFound($"No trace for run {id}.");

        var targetHost = !string.IsNullOrEmpty(row.Target) && Uri.TryCreate(row.Target, UriKind.Absolute, out var tu)
            ? tu.Host : null;

        var blob = await _artifacts.DownloadToMemoryAsync(row.TraceUrl, "trace", id, ct);
        switch (blob.Status)
        {
            case ArtifactStatus.Missing:
                return ApiResults.NotFound($"No trace for run {id}.");
            case ArtifactStatus.Gone:
                return ApiResults.NotFound($"trace blob for run {id} is no longer available.");
            case ArtifactStatus.Unavailable:
                return ApiResults.ServiceUnavailable($"the trace for run {id} is temporarily unavailable.");
            default:
                using (blob.Content!)
                    return ApiResults.Ok(TraceExtractor.FromZip(blob.Content!, targetHost)); // bad zip → empty summary
        }
    }

    /// <summary>Stream a runner-written artifact blob to the client through the shared reader. 404 for a
    /// missing url/blob, a clean 503 for a transient blob error (NOT a 500), else the file stream.</summary>
    private async Task<IActionResult> ProxyAsync(
        HttpRequest req, long id, string subject, string? blobUrl, string artifact, string contentType,
        string contentDisposition, CancellationToken ct)
    {
        var blob = await _artifacts.OpenStreamAsync(blobUrl, artifact, id, ct);
        switch (blob.Status)
        {
            case ArtifactStatus.Missing:
                return ApiResults.NotFound($"No {artifact} for {subject}.");
            case ArtifactStatus.Gone:
                return ApiResults.NotFound($"{artifact} blob for {subject} is no longer available.");
            case ArtifactStatus.Unavailable:
                return ApiResults.ServiceUnavailable($"the {artifact} for {subject} is temporarily unavailable.");
            default:
                // Set Content-Disposition explicitly (inline for images, attachment for downloads); do NOT use
                // FileStreamResult.FileDownloadName, which would force attachment.
                req.HttpContext.Response.Headers.ContentDisposition = contentDisposition;
                return new FileStreamResult(blob.Content!, contentType);
        }
    }
}
