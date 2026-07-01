namespace SynthWatch.Api.Dtos;

/// <summary>
/// GET /api/reports/deploys?host=&amp;window= — auto-detected deploy markers for a host over the window, for
/// overlaying ReferenceLines on the existing time-series charts (deploy-markers v1). Property-safe: exposes the
/// commit sha (when detected), the marker source, and when — not the raw etag/fingerprint internals.
/// </summary>
public record DeploysReportDto(
    string Host,
    string Window,
    IReadOnlyList<DeployMarkerDto> Deploys);

/// <summary>One deploy marker. sha is null when the marker isn't a git commit (etag / build-id) — the UI then
/// labels it "deploy detected (no commit id)" honestly, never a fake sha. isSha drives that labeling.</summary>
public record DeployMarkerDto(
    string? Sha,
    bool IsSha,
    string Source,
    DateTimeOffset DeployedAt);
