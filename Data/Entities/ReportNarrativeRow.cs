namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// Keyless projection of a runner-written report_narratives row (Reporting Layer 3). The RUNNER's
/// narrative job generates these (it owns the AOAI plumbing + the table); the API only SERVES the latest
/// row read-only — no generation here. highlights + fact_pack are read as raw jsonb TEXT (cast ::text in
/// the query) and re-emitted verbatim by the handler, so the cited numbers pass through unchanged.
/// NOTE: built to the Layer-3 design contract ahead of the runner migration — reconcile column names if
/// the landed migration differs.
/// </summary>
public class ReportNarrativeRow
{
    public string ScopeType { get; set; } = "";
    public string ScopeKey { get; set; } = "";
    public string Window { get; set; } = "";
    public DateTimeOffset GeneratedAt { get; set; }
    public string Headline { get; set; } = "";
    public string Body { get; set; } = "";
    public string Highlights { get; set; } = "[]"; // jsonb::text — a string[]
    public string FactPack { get; set; } = "{}";   // jsonb::text — the cited-numbers fact pack
    public string? Model { get; set; }
}
