using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using SynthWatch.Api.Data;
using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;

namespace SynthWatch.Api.Functions;

public class SlaFunctions
{
    private readonly SynthWatchDbContext _db;

    public SlaFunctions(SynthWatchDbContext db) => _db = db;

    // Insufficient-data thresholds. A window's availability is only meaningful when there are
    // enough samples AND the data actually spans (most of) the window. A check younger than the
    // window would otherwise show a precise % over a fraction of the claimed period (e.g. a
    // ~21h-old check showing a 30d number computed over <3% of 30 days).
    private const long MinCompletedRuns = 20;     // need at least this many completed runs
    private const double MinCoverage = 0.8;       // check must have existed for >=80% of the window

    /// <summary>
    /// GET /api/sla?window=24h|7d|30d — per-check availability from the runner-owned SLA views,
    /// plus a run-weighted fleet rollup. Per-check and fleet carry an <c>insufficientData</c> flag
    /// (with a null percentage) when the window lacks enough data to report a trustworthy number.
    /// </summary>
    [Function("GetSla")]
    public async Task<IActionResult> GetSla(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sla")] HttpRequest req,
        CancellationToken ct)
    {
        var window = req.Query["window"].ToString();

        // Map the window to a fixed view name — a closed allowlist, so no SQL injection surface.
        string? sql = window switch
        {
            "" or "24h" => "SELECT * FROM sla_availability_24h",
            "7d" => "SELECT * FROM sla_availability_7d",
            "30d" => "SELECT * FROM sla_availability_30d",
            _ => null
        };

        if (sql is null)
            return ApiResults.BadRequest("window must be one of: 24h, 7d, 30d.");

        var rows = await _db.SlaAvailability.FromSqlRaw(sql).AsNoTracking().ToListAsync(ct);

        // Each check's created_at decides how much of the window it could have covered.
        var createdById = await _db.Checks.AsNoTracking()
            .ToDictionaryAsync(c => c.Id, c => c.CreatedAt, ct);

        var items = new List<SlaDto>(rows.Count);
        double maxCoverage = 0;
        long sumUp = 0, sumDown = 0, sumCompleted = 0;

        foreach (var r in rows)
        {
            var createdAt = createdById.TryGetValue(r.CheckId, out var ca) ? ca : r.WindowFrom;
            var coverage = WindowCoverage(r.WindowFrom, r.WindowTo, createdAt);
            var insufficient = r.CompletedRuns < MinCompletedRuns || coverage < MinCoverage;

            items.Add(new SlaDto(
                r.CheckId, r.CheckName, r.Kind, r.WindowFrom, r.WindowTo,
                r.CompletedRuns, r.UpRuns, r.DownRuns,
                AvailabilityPct: insufficient ? null : r.AvailabilityPct,
                InsufficientData: insufficient));

            sumUp += r.UpRuns;
            sumDown += r.DownRuns;
            sumCompleted += r.CompletedRuns;
            if (coverage > maxCoverage) maxCoverage = coverage;
        }

        // Fleet = run-weighted SUM(up)/SUM(completed). Insufficient when the whole fleet lacks
        // samples, or even the longest-lived check hasn't covered enough of the window.
        var fleetInsufficient = sumCompleted < MinCompletedRuns || maxCoverage < MinCoverage;
        var fleet = new SlaFleetDto(
            CompletedRuns: sumCompleted,
            UpRuns: sumUp,
            DownRuns: sumDown,
            AvailabilityPct: fleetInsufficient || sumCompleted == 0
                ? null
                : Math.Round(100m * sumUp / sumCompleted, 4),
            InsufficientData: fleetInsufficient);

        // SLA aggregates change slowly (windowed availability); cache a bit longer than /checks.
        // Vary on Origin: publicly cacheable + per-origin platform CORS header.
        req.HttpContext.Response.Headers.CacheControl = "public, max-age=30";
        req.HttpContext.Response.Headers["Vary"] = "Origin";
        return ApiResults.Ok(new
        {
            window = string.IsNullOrEmpty(window) ? "24h" : window,
            fleet,
            items
        });
    }

    /// <summary>Fraction (0..1) of [from,to) for which the check has existed.</summary>
    private static double WindowCoverage(DateTimeOffset from, DateTimeOffset to, DateTimeOffset createdAt)
    {
        var total = (to - from).TotalSeconds;
        if (total <= 0) return 0;
        var start = createdAt > from ? createdAt : from;
        var covered = (to - start).TotalSeconds / total;
        return covered < 0 ? 0 : covered > 1 ? 1 : covered;
    }
}
