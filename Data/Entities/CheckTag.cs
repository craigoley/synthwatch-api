namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// A key:value tag on a check (runner migration 0024 / #84). Normalized table, PK (check_id, key) =>
/// one value per key per check. key may be '' (a bare value); value is non-empty. Both are lowercase,
/// whitespace-free (DB CHECKs + the API/runner normalizer enforce it). FK check_id -> checks CASCADE.
/// </summary>
public class CheckTag
{
    public long CheckId { get; set; }
    public string Key { get; set; } = "";
    public string Value { get; set; } = null!;
}
