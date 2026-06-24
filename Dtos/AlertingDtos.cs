using System.Text.Json.Serialization;
using SynthWatch.Api.Data.Entities;

namespace SynthWatch.Api.Dtos;

/// <summary>A delivery channel on the wire: { id, name, type, config, enabled }.</summary>
public record ChannelDto(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("config")] ChannelConfig Config,
    [property: JsonPropertyName("enabled")] bool Enabled)
{
    public static ChannelDto From(Channel c) => new(c.Id, c.Name, c.Type, c.Config, c.Enabled);
}

/// <summary>
/// POST /api/channels/{id}/test response (202 Accepted). The API only enqueues a test_send_request; the
/// RUNNER does the real send. The dashboard polls GET .../test/status?requestId={requestId} for the outcome.
/// </summary>
public record ChannelTestAcceptedDto(
    [property: JsonPropertyName("requestId")] long RequestId);

/// <summary>
/// GET /api/channels/{id}/test/status response — the runner-owned lifecycle of one test-send request.
/// status is 'pending'|'sending'|'delivered'|'failed'; detail/completedAt are null until the runner sets them.
/// </summary>
public record ChannelTestStatusDto(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("requestedAt")] DateTimeOffset RequestedAt,
    [property: JsonPropertyName("completedAt")] DateTimeOffset? CompletedAt);

/// <summary>Body for POST /api/channels and PUT /api/channels/{id} (full replace of mutable fields).</summary>
public class ChannelWriteRequest
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("config")] public ChannelConfig? Config { get; set; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
}

/// <summary>A set of channel ids ({ channelIds: [...] }) for one routing scope.</summary>
public record ChannelIdsDto(
    [property: JsonPropertyName("channelIds")] IReadOnlyList<long> ChannelIds);

/// <summary>One tag-routing rule (#85): a channel fires for checks carrying tagKey:tagValue. ADDITIVE.</summary>
public record TagRuleDto(
    [property: JsonPropertyName("tagKey")] string TagKey,
    [property: JsonPropertyName("tagValue")] string TagValue,
    [property: JsonPropertyName("channelId")] long ChannelId);

/// <summary>
/// GET/PUT /api/routing shape — the THREE all-additive routing dimensions (#85): severity defaults
/// (critical/warning) + per-check + tag-rules, each a set of channels. The runner UNIONs them at
/// dispatch ("hit any criterion → alerted"); the API only CRUDs the rule sets. On GET, a dimension is
/// null when it has no rows. On PUT each dimension is independent: ABSENT (null) leaves it untouched;
/// PRESENT (even empty) REPLACES it exactly.
/// </summary>
public class RoutingDto
{
    [JsonPropertyName("severity")] public Dictionary<string, ChannelIdsDto>? Severity { get; set; }
    [JsonPropertyName("perCheck")] public Dictionary<string, ChannelIdsDto>? PerCheck { get; set; }
    [JsonPropertyName("tagRules")] public List<TagRuleDto>? TagRules { get; set; }
}

/// <summary>
/// GET /api/notifications/health — alerting deliverability readiness, reporting ONLY what the API can
/// verify. channelsConfigured + routingConfigured are read from the DB (the API owns that state).
/// transportConfigured is the ACS email transport (ACS_EMAIL_CONNECTION_STRING + ALERT_EMAIL_FROM),
/// which lives in RUNNER env — so it is true only if those vars are present ON THE API, otherwise null
/// = UNKNOWN. The API never asserts a transport state it can't see.
/// </summary>
public record NotificationsReadinessDto(
    [property: JsonPropertyName("channelsConfigured")] bool ChannelsConfigured,
    [property: JsonPropertyName("routingConfigured")] bool RoutingConfigured,
    [property: JsonPropertyName("transportConfigured")] bool? TransportConfigured,
    [property: JsonPropertyName("detail")] string Detail);
