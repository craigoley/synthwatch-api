using System.Text.Json.Serialization;

namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// A delivery target (runner migration 0023 / #81). The runner owns the table; the API does CRUD.
/// config holds TARGETS only (recipients/URLs) — never the transport secret (ACS connection string
/// stays in runner env). type is constrained to 'email'|'webhook' by a DB CHECK.
/// </summary>
public class Channel
{
    public long Id { get; set; }
    public string Name { get; set; } = null!;
    public string Type { get; set; } = null!;
    public ChannelConfig Config { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Channel delivery config (jsonb). email: <see cref="To"/> + <see cref="From"/>; webhook:
/// <see cref="Url"/> (+ optional <see cref="AuthHeader"/>). Nulls are omitted on write (so an empty
/// config round-trips as {}). NEVER holds a transport secret — write-validation rejects connection
/// strings.
/// </summary>
public class ChannelConfig
{
    [JsonPropertyName("to")] public List<string>? To { get; set; }
    [JsonPropertyName("from")] public string? From { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("authHeader")] public string? AuthHeader { get; set; }
}
