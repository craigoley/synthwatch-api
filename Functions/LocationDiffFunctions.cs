using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SynthWatch.Api.Data;
using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;

namespace SynthWatch.Api.Functions;

/// <summary>
/// "Why does this run fail when the last-known-good baseline passed?" — POST /api/runs/{runId}/baseline-diff
/// resolves the failing run's trace signals (persisted by #114, else extracted on-demand) + the check's
/// success-trace baseline signals (always on-demand — there are no persisted baseline signals), runs the
/// canonicalizing TraceSignalsDiff, and feeds the DELTA (not two full traces) to gpt-5-mini for a categorized
/// regional-cause comparison. A POST → gated editor/admin (spends AOAI). INERT until AZURE_OPENAI_* is set (the
/// diff is still returned; the insight is null). Reuses ArtifactReader + TraceExtractor + AoaiClient + the
/// ai-insights outcome handling (#96/#104).
/// </summary>
public class LocationDiffFunctions
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly SynthWatchDbContext _db;
    private readonly IArtifactReader _artifacts;
    private readonly IAoaiClient _aoai;
    private readonly ILogger<LocationDiffFunctions>? _logger;

    public LocationDiffFunctions(SynthWatchDbContext db, IArtifactReader artifacts, IAoaiClient aoai,
        ILogger<LocationDiffFunctions>? logger = null)
    {
        _db = db;
        _artifacts = artifacts;
        _aoai = aoai;
        _logger = logger;
    }

    [Function("GetBaselineDiff")]
    public async Task<IActionResult> GetBaselineDiff(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "runs/{runId:long}/baseline-diff")] HttpRequest req,
        long runId,
        CancellationToken ct)
    {
        var row = await _db.Runs.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => new
            {
                r.Status, r.Location, r.TraceUrl, r.TraceSignals,
                // ★ The FAILED ASSERTION — the RCA's primary signal, previously never fed to the model.
                r.ErrorMessage, r.FailedStep,
                Target = r.Check!.TargetUrl, BaselineUrl = r.Check!.SuccessTraceUrl, BaselineAt = r.Check!.SuccessTraceAt,
                // B10: redact the new assertion/URL context for sensitive monitors before it reaches the model.
                Sensitive = r.Check!.Sensitive, RedactPatterns = r.Check!.RedactPatterns,
            })
            .FirstOrDefaultAsync(ct);

        if (row is null) return ApiResults.NotFound($"Run {runId} not found.");

        var targetHost = !string.IsNullOrEmpty(row.Target) && Uri.TryCreate(row.Target, UriKind.Absolute, out var tu)
            ? tu.Host : null;

        // The failing run's signals. Non-sensitive: ZIP-FIRST so we capture mutations (the action under test).
        // ★ Sensitive (B10): persisted-ONLY — the runner already redacted the persisted signals; re-extracting the
        // RAW zip would bypass that redaction. (Sensitive monitors also skip the trace upload, so there's usually
        // no zip anyway — but the guard makes the no-leak guarantee explicit, not incidental.)
        var failing = await ResolveSignalsAsync(row.TraceSignals, row.TraceUrl, targetHost, runId, allowZip: !row.Sensitive, ct);
        if (failing is null)
            return ApiResults.NotFound($"No trace to analyze for run {runId}.");

        // The baseline: ALWAYS on-demand (no persisted baseline signals exist).
        if (string.IsNullOrEmpty(row.BaselineUrl))
            return ApiResults.NotFound($"No known-good baseline yet for this monitor to compare against.");
        var baselineBlob = await _artifacts.DownloadToMemoryAsync(row.BaselineUrl, "success trace", runId, ct);
        if (baselineBlob.Status == ArtifactStatus.Unavailable)
            return ApiResults.ServiceUnavailable("Couldn't read the baseline trace right now — please try again.");
        if (baselineBlob.Status != ArtifactStatus.Ok)
            return ApiResults.NotFound($"The baseline trace for this monitor is no longer available.");
        TraceSignalsDto baseline;
        using (baselineBlob.Content!) baseline = TraceExtractor.FromZip(baselineBlob.Content!, targetHost);

        var failingLabel = $"this run ({row.Location ?? "default"}, {row.Status})";
        const string baselineLabel = "the monitor's last-known-good baseline";
        var diff = TraceSignalsDiff.Diff(failing, baseline, failingLabel, baselineLabel);

        var fRef = new DiffRunRef(runId, row.Location ?? "default", row.Status);
        var bRef = new DiffBaselineRef("success-baseline", row.BaselineAt, null);

        // INERT until configured — still return the diff (the data), just no AI insight.
        if (!_aoai.IsConfigured)
            return ApiResults.Ok(LocationDiffDto.NotConfigured(fRef, bRef, diff));

        // B10: redact the assertion text + each mutation's URL for sensitive monitors (trace_signals are already
        // runner-scrubbed, but error_message/failed_step + zip-extracted mutation URLs are not).
        bool sensitive = row.Sensitive;
        var patterns = row.RedactPatterns;
        string? failedStep = SensitiveRedaction.Redact(row.FailedStep, sensitive, patterns);
        string? assertionError = SensitiveRedaction.Redact(row.ErrorMessage, sensitive, patterns);
        var failingNetwork = sensitive && failing.Network.Mutations.Count > 0
            ? failing.Network with
            {
                Mutations = failing.Network.Mutations
                    .Select(m => m with { Url = SensitiveRedaction.Redact(m.Url, true, patterns)! }).ToList(),
            }
            : failing.Network;

        var user = LocationDiffInsight.BuildUser(
            failingLabel, baselineLabel, row.Status, failedStep, assertionError, failingNetwork, diff);
        var result = await _aoai.ChatJsonAsync(LocationDiffInsight.SystemPrompt, user, ct);
        if (result.Outcome != AoaiOutcome.Ok)
        {
            // Reuse the ai-insights honest messages (transient vs deterministic) for the note + retryable flag.
            var mapped = AiInsightsFunctions.MapFailure(result);
            return ApiResults.Ok(LocationDiffDto.Unavailable(fRef, bRef, diff, mapped.Note!, mapped.Retryable));
        }

        var insight = LocationDiffInsight.Parse(result.Content!);
        return insight is null
            ? ApiResults.Ok(LocationDiffDto.Unavailable(fRef, bRef, diff,
                "The AI returned an unexpected response format. Re-running is unlikely to help.", retryable: false))
            : ApiResults.Ok(LocationDiffDto.Ok(fRef, bRef, diff, insight));
    }

    /// <summary>
    /// Signals for the FAILING run, ZIP-FIRST: re-extract from the trace zip so we capture MUTATIONS (the action
    /// under test — the persisted #114 slice predates mutation capture and the runner does not persist them).
    /// Fall back to the persisted JSON when the zip is gone (its counts still drive the baseline diff), else null.
    /// The extra zip read is acceptable on this gated, on-demand, AOAI-spending endpoint.
    /// </summary>
    private async Task<TraceSignalsDto?> ResolveSignalsAsync(
        string? persisted, string? traceUrl, string? targetHost, long id, bool allowZip, CancellationToken ct)
    {
        if (allowZip)
        {
            var blob = await _artifacts.DownloadToMemoryAsync(traceUrl, "trace", id, ct);
            if (blob.Status == ArtifactStatus.Ok)
            {
                using (blob.Content!) return TraceExtractor.FromZip(blob.Content!, targetHost);
            }
        }

        if (!string.IsNullOrWhiteSpace(persisted))
        {
            try
            {
                var dto = JsonSerializer.Deserialize<TraceSignalsDto>(persisted, Web);
                if (dto is not null) return dto;
            }
            catch (JsonException)
            {
                // Malformed persisted JSON → serve no signals (unchanged); log so a corrupt stored row is visible.
                if (_logger is not null) LocationDiffLog.MalformedPersistedSignals(_logger, id);
            }
        }
        return null;
    }
}

/// <summary>High-performance (CA1848) log delegates for LocationDiffFunctions tolerance paths.</summary>
internal static partial class LocationDiffLog
{
    [LoggerMessage(EventId = 7002, Level = LogLevel.Information,
        Message = "malformed persisted trace-signals JSON for run {RunId} — serving no signals (corrupt stored row)")]
    public static partial void MalformedPersistedSignals(ILogger logger, long runId);
}
