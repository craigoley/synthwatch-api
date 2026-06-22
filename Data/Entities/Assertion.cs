using System.Text.Json;

namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// One no-code assertion (element of <c>checks.assertions</c> JSONB). Mirrors the runner's
/// contract (migration 0008 / runner/assertions.ts):
/// <c>{ source, comparison, target?, expected? }</c>.
/// </summary>
public class Assertion
{
    /// <summary>status | response_time | header | body | json_path | size</summary>
    public string Source { get; set; } = "";

    /// <summary>eq | ne | lt | gt | gte | lte | contains | not_contains | matches | exists | one_of</summary>
    public string Comparison { get; set; } = "";

    /// <summary>Header name (source=header) or JSONPath expr (source=json_path). Required for those.</summary>
    public string? Target { get; set; }

    /// <summary>Expected value (scalar, or an array for one_of; ignored for exists). Kept verbatim.</summary>
    public JsonElement? Expected { get; set; }
}
