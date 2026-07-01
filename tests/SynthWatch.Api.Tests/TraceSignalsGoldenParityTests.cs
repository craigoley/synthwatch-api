using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>
/// ★ The API half of the trace-signals CROSS-REPO parity guard. traceSignals.ts (runner) and
/// TraceExtractor.cs (here) are a hand-ported pair whose output MUST stay byte-identical (the persisted
/// runs.trace_signals JSON is read as the C# TraceSignalsDto shape). They already drifted once (the
/// `mutations` signal, reconciled in the runner's #169). The RUNNER owns the shared golden fixture
/// (runner/test-fixtures/trace-signals-golden/); the trace-parity workflow checks out that repo and points
/// RUNNER_GOLDEN_DIR at it, and this test asserts C# TraceExtractor.FromZip reproduces the SAME expected.json
/// the runner's traceSignals.test.ts asserts. A divergence in EITHER extractor fails ITS repo's CI.
///
/// [SkippableFact]: the normal test job (no runner checkout) SKIPS this; only the cross-repo trace-parity job
/// (which sets RUNNER_GOLDEN_DIR) runs it. Mirrors pg-grant-coverage checking out the runner as source of truth.
/// </summary>
public class TraceSignalsGoldenParityTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Resolve the runner's golden dir: RUNNER_GOLDEN_DIR (CI sets it to the checked-out runner repo)
    /// → a sibling ./synthwatch checkout found by walking up from the test binary (local dev). Null = skip.</summary>
    private static string? GoldenDir()
    {
        var env = Environment.GetEnvironmentVariable("RUNNER_GOLDEN_DIR");
        if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "expected.json"))) return env;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "synthwatch", "runner", "test-fixtures", "trace-signals-golden");
            if (File.Exists(Path.Combine(c, "expected.json"))) return c;
        }
        return null;
    }

    [SkippableFact]
    public void FromZip_golden_input_matches_expected_json()
    {
        var dir = GoldenDir();
        Skip.If(dir is null,
            "runner golden fixtures not available — set RUNNER_GOLDEN_DIR (the trace-parity CI job does) or check out craigoley/synthwatch as a sibling.");

        // Build the trace zip from the shared golden input (the same two NDJSON streams the runner zips).
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in new[] { "trace.network", "trace.trace" })
            {
                var entry = zip.CreateEntry(name);
                using var es = entry.Open();
                var bytes = File.ReadAllBytes(Path.Combine(dir!, name));
                es.Write(bytes, 0, bytes.Length);
            }
        }
        ms.Position = 0;

        // targetHost = the HOST (FromZip takes the host directly; the runner derives it from the target URL).
        var actual = TraceExtractor.FromZip(ms, "www.wegmans.com");
        var actualJson = JsonNode.Parse(JsonSerializer.Serialize(actual, Web));
        var expectedJson = JsonNode.Parse(File.ReadAllText(Path.Combine(dir!, "expected.json")));

        Assert.True(
            JsonNode.DeepEquals(actualJson, expectedJson),
            "C# TraceExtractor.FromZip diverged from the shared golden — the runner/API extractors are out of sync.\n" +
            $"expected: {expectedJson?.ToJsonString()}\nactual:   {actualJson?.ToJsonString()}");
    }
}
