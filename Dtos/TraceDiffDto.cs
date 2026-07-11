namespace SynthWatch.Api.Dtos;

/// <summary>A console line in a diff (level + site/third-party origin + resource sourceHost + a
/// representative text). Error-diff P1 added SourceHost — part of the per-error fingerprint.</summary>
public sealed record DiffConsoleLine(string Level, string Origin, string SourceHost, string Text);

/// <summary>What differs in the console between two runs' signals — after CANONICALIZATION (per-run ids /
/// query strings / timestamps stripped), so the SAME error with different ids counts as shared, not different.</summary>
public sealed record DiffConsole(
    IReadOnlyList<DiffConsoleLine> OnlyInA,
    IReadOnlyList<DiffConsoleLine> OnlyInB,
    int Shared);

/// <summary>What differs in the network footprint between two runs' signals.</summary>
public sealed record DiffNetwork(
    int TotalRequestsA, int TotalRequestsB,
    long WireKbA, long WireKbB,
    int ThirdPartyCountA, int ThirdPartyCountB,
    int FailedCountA, int FailedCountB,
    IReadOnlyList<string> FailedHostsOnlyInA,
    IReadOnlyList<string> FailedHostsOnlyInB,
    IReadOnlyList<ThirdPartyDto> ThirdPartyOnlyInA,
    IReadOnlyList<ThirdPartyDto> ThirdPartyOnlyInB);

/// <summary>
/// The structured DELTA between two runs' trace signals (e.g. a failing location's run vs a known-good run).
/// The cheap payoff of persisted per-run signals (#114): diff two JSONs, not two 18 MB zips. Labels name the
/// two sides (e.g. "eastus2 (fail)" vs "centralus baseline"). The AI-over-diff layer feeds THIS (the delta),
/// not two full traces, to the model.
/// </summary>
public sealed record TraceDiffDto(string LabelA, string LabelB, DiffConsole Console, DiffNetwork Network);
