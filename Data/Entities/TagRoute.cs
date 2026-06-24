namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// A tag-routing rule (runner migration 0025 / #85): a channel that fires for any check carrying the
/// tag tag_key:tag_value. One of the three ADDITIVE routing dimensions (severity ∪ per-check ∪ tag-rules,
/// unioned by the runner at dispatch). UNIQUE (tag_key, tag_value, channel_id); normalized CHECKs match
/// check_tags (lowercase, whitespace-free, non-empty value); channel_id FK -> channels CASCADE.
/// </summary>
public class TagRoute
{
    public long Id { get; set; }
    public string TagKey { get; set; } = "";
    public string TagValue { get; set; } = null!;
    public long ChannelId { get; set; }
}
