namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// Runner-owned catalogue of browser flows (single source of truth for "what flows exist").
/// Maps to the live <c>flow_manifest</c> table; read-only here (the runner populates it).
/// </summary>
public class FlowManifest
{
    public string Name { get; set; } = null!;          // PK
    public string? Description { get; set; }
    public string? EntryUrlHint { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
