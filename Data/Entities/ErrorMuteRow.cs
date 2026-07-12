namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// One operator MUTE of an error fingerprint for a check (runner migration 0076, error-diff P4). The error-diff
/// read (<c>GET /api/checks/{id}/error-diff</c>) moves a muted fingerprint OUT of <c>new[]</c> into a separate
/// <c>muted[]</c> bucket (never silently dropped). DASHBOARD-managed operator config — the API has full CRUD
/// (SELECT/INSERT/DELETE; no UPDATE — notes are set at mute time, unmute + re-mute to change one). The runner
/// never reads it; a check purge cascades its mutes away (FK ON DELETE CASCADE).
/// </summary>
public class ErrorMuteRow
{
    public long Id { get; set; }
    public long CheckId { get; set; }
    public string Fingerprint { get; set; } = null!;
    public DateTimeOffset MutedAt { get; set; }
    public string? MutedBy { get; set; }
    public string? Note { get; set; }
}
