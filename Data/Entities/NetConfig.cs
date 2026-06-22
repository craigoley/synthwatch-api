namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// Per-kind config for network checks (dns/tcp/ping), stored in <c>checks.net_config</c> JSONB.
/// Union of fields mirroring the runner's contract (migration 0011 / runner/netChecks.ts); the
/// host comes from <c>target_url</c>:
///   dns  → { recordType: A|AAAA|CNAME|MX|TXT|NS (default A), expectedValue? }
///   tcp  → { port }            (or host:port in target_url)
///   ping → { port }            (TCP-reachability port; runner default 443)
/// </summary>
public class NetConfig
{
    public string? RecordType { get; set; }
    public string? ExpectedValue { get; set; }
    public int? Port { get; set; }
}
