using System.Text.RegularExpressions;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// B10-aware redaction for RCA context. The runner already scrubs persisted <c>trace_signals</c> for sensitive
/// monitors, but the NEW RCA context (the failing assertion's error_message/failed_step, mutation URLs extracted
/// from the trace) is NOT pre-scrubbed — so for a <c>sensitive</c> check we apply its declared
/// <c>redact_patterns</c> before any of it reaches the model. Non-sensitive checks pass through unchanged.
/// </summary>
public static class SensitiveRedaction
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);
    private const string Mask = "[redacted]";

    /// <summary>
    /// Apply the check's <c>redact_patterns</c> to <paramref name="text"/> when <paramref name="sensitive"/>.
    /// A malformed pattern is skipped (never throws). Returns the input verbatim for non-sensitive checks or a
    /// null/empty input.
    /// </summary>
    public static string? Redact(string? text, bool sensitive, IReadOnlyList<string>? patterns)
    {
        if (!sensitive || string.IsNullOrEmpty(text) || patterns is null || patterns.Count == 0)
            return text;

        var result = text;
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            try
            {
                result = Regex.Replace(result, pattern, Mask, RegexOptions.None, PatternTimeout);
            }
            catch (RegexParseException) { /* a bad declared pattern must not break RCA — skip it */ }
            catch (RegexMatchTimeoutException) { /* pathological input/pattern — skip, don't hang */ }
        }
        return result;
    }
}
