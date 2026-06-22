namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// One step in a <c>multistep</c> API chain (element of <c>checks.steps</c> JSONB). Mirrors the
/// runner contract (migration 0013 / runner/multistep.ts): a request (the same shape a single http
/// check uses) + per-step assertions + extract rules. <c>url</c>/<c>headers</c>/<c>body</c> may
/// contain <c>{{var}}</c> templates referencing vars extracted by earlier steps; <c>auth</c> is a
/// secret-reference (the <c>*_env</c> model — never plaintext).
/// </summary>
public class ChainStep
{
    public string Name { get; set; } = "";
    public string? Method { get; set; }
    public string Url { get; set; } = "";
    public Dictionary<string, string>? Headers { get; set; }
    public string? Body { get; set; }
    public Dictionary<string, string>? Auth { get; set; }
    public List<Assertion>? Assertions { get; set; }
    public List<ExtractRule>? Extract { get; set; }
}

/// <summary>An extract rule: pull <c>jsonPath</c> from the step's JSON response into a named var.</summary>
public class ExtractRule
{
    public string Var { get; set; } = "";
    public string JsonPath { get; set; } = "";
}
