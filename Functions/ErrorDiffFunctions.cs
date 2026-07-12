using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using SynthWatch.Api.Data;
using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;

namespace SynthWatch.Api.Functions;

/// <summary>
/// Error-diff (P2): GET /api/checks/{id}/error-diff — the errors THIS run has that are NEW / PERSISTENT /
/// RESOLVED vs the last-N settled runs. Computed entirely from persisted <c>runs.trace_signals</c> (no zip
/// re-parse); the pure diff lives in <see cref="ErrorDiff"/>. Same forensic auth gate as the trace-signals
/// endpoint (it surfaces the same error text) — <see cref="SessionReadGate"/> (#154).
/// </summary>
public class ErrorDiffFunctions
{
    private readonly SynthWatchDbContext _db;
    private readonly IAuthPrincipal _auth;

    // Runner-written trace_signals is camelCase JSON — same options GetTraceSignals uses.
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public ErrorDiffFunctions(SynthWatchDbContext db, IAuthPrincipal auth)
    {
        _db = db;
        _auth = auth;
    }

    [Function("GetCheckErrorDiff")]
    public async Task<IActionResult> GetCheckErrorDiff(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "checks/{id:long}/error-diff")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        // Forensic gate: error text is the same class of data as /runs/{id}/trace-signals → require a session.
        if (await SessionReadGate.RequireSessionAsync(_auth, req, ct) is { } denied) return denied;

        // ?baseline=N overrides the anti-flap window (clamped); default = the named constant.
        var n = int.TryParse(req.Query["baseline"], out var bn) ? Math.Clamp(bn, 1, 10) : ErrorDiff.BaselineRuns;

        // Target: an explicit ?runId (any run of THIS check), else the latest SETTLED run WITH signals.
        Run? target;
        if (long.TryParse(req.Query["runId"], out var rid))
            target = await _db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rid && r.CheckId == id, ct);
        else
            target = await _db.Runs.AsNoTracking()
                .Where(r => r.CheckId == id && r.Status != "running" && r.TraceSignals != null)
                .OrderByDescending(r => r.StartedAt)
                .FirstOrDefaultAsync(ct);

        if (target is null)
            return ApiResults.NotFound($"No settled run with trace signals for check {id}.");

        // Baseline: the N settled runs WITH signals, SAME LOCATION, strictly before the target, newest first.
        // (Same-location keeps it apples-to-apples for a multi-location check; sandbox runs are INCLUDED — a
        // paused monitor's on-demand validations are real captures, unlike the SLO rollups that exclude them.)
        var q = _db.Runs.AsNoTracking()
            .Where(r => r.CheckId == id && r.Status != "running" && r.TraceSignals != null
                        && r.StartedAt < target.StartedAt && r.Id != target.Id);
        q = target.Location is null ? q.Where(r => r.Location == null) : q.Where(r => r.Location == target.Location);
        var baselineRuns = await q.OrderByDescending(r => r.StartedAt).Take(n).ToListAsync(ct);

        // ★ P4 MUTE: the fingerprints the operator has muted for this check → the diff diverts would-be-NEW
        // matches into its muted[] bucket (never silently dropped). One indexed read (WHERE check_id = $1).
        var mutedFps = (await _db.ErrorMutes.AsNoTracking()
            .Where(m => m.CheckId == id).Select(m => m.Fingerprint).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var dto = ErrorDiff.Compute(
            id, target.Id, target.StartedAt, target.Location,
            ToSignals(target), baselineRuns.Select(ToSignals).ToList(),
            mutedFps);

        // ★ P4 DEPLOY CORRELATION: every NEW error debuted in THIS run, so it first appeared in the window
        // (previous settled run, this run]. A deploy that landed in that window on the check's host is the
        // correlation — the SAME deploy for every NEW item (they share the window). ONE batched, indexed query
        // (no N+1); attach it to each NEW item. Bounded below by the newest baseline run (baselineRuns is
        // newest-first); with no baseline run there's no lower bound to attribute against → leave it null.
        if (dto.New.Count > 0 && baselineRuns.Count > 0)
        {
            var targetUrl = await _db.Checks.AsNoTracking()
                .Where(c => c.Id == id).Select(c => c.TargetUrl).FirstOrDefaultAsync(ct);
            var deploy = await FirstSeenDeployAsync(targetUrl, baselineRuns[0].StartedAt, target.StartedAt, ct);
            if (deploy is not null)
                dto = dto with { New = dto.New.Select(i => i with { FirstSeenAfterDeploy = deploy }).ToList() };
        }

        // Session-gated forensic data → NEVER publicly cacheable (a shared cache keyed on Origin, not
        // Authorization, could serve the authed 200 to a later anonymous caller — CWE-525). Matches the
        // sibling reconcile/trace-signals reads.
        req.HttpContext.Response.Headers.CacheControl = "no-store";
        return ApiResults.Ok(dto);
    }

    /// <summary>The deploy a NEW error first appeared after: the most recent deploy on the check's host whose
    /// <c>deployed_at</c> falls in the window (windowStart, windowEnd] — i.e. between the previous settled run and
    /// this one. Host-matched with the SAME normalization the incident deploy-proximity uses (lowercase + strip a
    /// leading "www." on both sides), so an apex-host check matches a www deploy. Tolerant of the deploys table
    /// not being migrated in this env (→ null, never a 500). Correlation, never causation.</summary>
    private async Task<FirstSeenAfterDeployDto?> FirstSeenDeployAsync(
        string? targetUrl, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct)
    {
        var host = NormalizeHost(targetUrl);
        if (host.Length == 0) return null;

        try
        {
            // Indexed by deploys_host_time_idx (target_host, deployed_at DESC). regexp_replace strips a leading
            // "www." from the stored host so apex ↔ www match. LIMIT 1 = the most recent deploy in the window.
            var d = await _db.Deploys.FromSql(
                $@"SELECT d.target_host, d.sha, d.fingerprint, d.is_sha, d.source, d.deployed_at
                   FROM deploys d
                   WHERE regexp_replace(lower(d.target_host), '^www\.', '') = {host}
                     AND d.deployed_at > {windowStart} AND d.deployed_at <= {windowEnd}
                   ORDER BY d.deployed_at DESC
                   LIMIT 1").AsNoTracking().FirstOrDefaultAsync(ct);

            return d is null
                ? null
                : new FirstSeenAfterDeployDto(
                    Sha: d.IsSha ? (d.Sha ?? "") : "", // empty unless it's a real SHA (mirrors DeployMarkerDto)
                    DeployedAt: d.DeployedAt,
                    TargetHost: d.TargetHost);
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42P01")
        {
            return null; // deploys not migrated in this env — serve null, never a 500.
        }
    }

    /// <summary>Host for deploy matching: the URL's host (or the raw value when it isn't an absolute URL),
    /// lowercased with a single leading "www." stripped — the SAME normalization the SQL applies to the stored
    /// target_host, so apex and www variants match. (Mirrors IncidentsFunctions.NormalizeHost.)</summary>
    private static string NormalizeHost(string? targetUrl)
    {
        if (string.IsNullOrWhiteSpace(targetUrl)) return "";
        var host = Uri.TryCreate(targetUrl, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host)
            ? u.Host
            : targetUrl;
        host = host.Trim().ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host["www.".Length..] : host;
    }

    // Parse a run's persisted signals (drifted/absent JSON → no signals, never a 500 — same tolerance as
    // GetTraceSignals) and compute its truncation flag.
    private static ErrorDiff.RunSignals ToSignals(Run r)
    {
        TraceSignalsDto? sig = null;
        if (!string.IsNullOrEmpty(r.TraceSignals))
        {
            try { sig = JsonSerializer.Deserialize<TraceSignalsDto>(r.TraceSignals, WebJson); }
            catch (JsonException) { sig = null; }
        }
        return new ErrorDiff.RunSignals(r.Id, sig, ErrorDiff.IsTruncated(sig));
    }
}
