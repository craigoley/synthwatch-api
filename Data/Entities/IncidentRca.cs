using System.Text.Json.Serialization;

namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// AI root-cause analysis attached to an incident (<c>incidents.rca</c> JSONB; migration 0015 /
/// runner/rca.ts). Null when RCA is off (the default), when the model/network failed (RCA never
/// blocks incident-open), or for pre-existing incidents. The honesty structure separates
/// <c>observed</c> facts (from the evidence) from <c>inferred</c> hypotheses; <c>confidence</c> is a
/// level ("high"|"medium"|"low"), not a number.
/// </summary>
public class IncidentRca
{
    // One of: real-outage | flaky-transient | selector-drift | environment-regional | perf-regression
    public string? Classification { get; set; }
    public string? Confidence { get; set; }        // "high" | "medium" | "low"
    public List<string>? Observed { get; set; }    // facts taken directly from the evidence
    public List<string>? Inferred { get; set; }    // hypotheses that follow from the observed facts
    public string? Summary { get; set; }           // one or two plain-English sentences for on-call
    public string? Signature { get; set; }         // check_id|error|failed_step — the cache key
    public string? Model { get; set; }             // model that produced it (null if unknown)
    public bool? Cached { get; set; }              // served from the RCA cache for an identical signature

    // Runner writes snake_case for this one key; the rest are single words (camelCase == as-written).
    [JsonPropertyName("generated_at")]
    public string? GeneratedAt { get; set; }
}
