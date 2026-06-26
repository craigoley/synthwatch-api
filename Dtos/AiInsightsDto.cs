namespace SynthWatch.Api.Dtos;

/// <summary>One actionable insight: what + why, severity/confidence-ranked, with the specific trace signal
/// it's grounded in.</summary>
public sealed record AiInsightDto(string Title, string Detail, string Severity, string Confidence, string? Evidence);

/// <summary>
/// The structured, categorized AI insights for a run's trace (slice 2). Categories mirror what the trace
/// supports: performance, network efficiency, the site's real console errors, and Lighthouse-STYLE suggestions.
/// <c>Configured</c> is false (with a <c>Note</c>) when AOAI isn't set up yet — the endpoint stays inert, never
/// 500s, until the deploy prereq is done.
/// </summary>
public sealed record AiInsightsDto(
    bool Configured,
    string? Summary,
    IReadOnlyList<AiInsightDto> Performance,
    IReadOnlyList<AiInsightDto> Network,
    IReadOnlyList<AiInsightDto> Errors,
    IReadOnlyList<AiInsightDto> Suggestions,
    IReadOnlyList<string> Caveats,
    string? Note)
{
    /// <summary>The inert response when AZURE_OPENAI_* isn't configured (the deploy prereq is pending).</summary>
    public static readonly AiInsightsDto NotConfigured = new(
        false, null, [], [], [], [], [],
        "AI insights are not configured for this environment yet.");

    /// <summary>AOAI configured but the model/parse was unavailable — non-fatal, try again.</summary>
    public static AiInsightsDto Unavailable(string note) => new(true, null, [], [], [], [], [], note);
}
