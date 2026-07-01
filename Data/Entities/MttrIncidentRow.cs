namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// Keyless projection for GET /api/reports/mttr — one row per incident opened in the window (resolved OR
/// open), joined to its check. The pure <c>MttrReportProjection</c> derives MTTR (resolved−opened) over the
/// RESOLVED ones, the classification breakdown, and the trend. <see cref="ResolvedAt"/> is null for an open
/// incident (excluded from MTTR, counted separately). <see cref="Classification"/> is coalesced to
/// "unclassified" so incidents with no RCA are shown, never dropped (the P6 lesson).
/// </summary>
public class MttrIncidentRow
{
    public long CheckId { get; set; }
    public string CheckName { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Status { get; set; } = "";           // 'open' | 'resolved'
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }     // null while open
    public string Classification { get; set; } = "";    // rca.classification, coalesced to "unclassified"
    public int ConsecutiveFailures { get; set; }        // failing runs before open — MTTD proxy input
    public int IntervalSeconds { get; set; }            // check cadence — MTTD proxy = failures × interval
}
