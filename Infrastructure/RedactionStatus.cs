namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// B10 redaction STATUS, derived from a check's DB row alone (sensitive + redact_patterns). Read-only visibility
/// so a redaction failure can't hide: the June-29 B10 bug went undetected because nothing surfaced whether a
/// sensitive monitor is actually redacted — finding "0/9 checks redacted" took a manual DB query.
///
/// ★ What this CAN and CANNOT see: it reflects the DB-ACTUAL state. It catches a check that is flagged sensitive
/// but carries NO patterns ("misconfigured" — the leak risk the #114 enable gate now blocks at enable-time). It
/// CANNOT catch "the manifest declares sensitive:true but the DB row has sensitive=false" — that needs the
/// manifest, which only reconcile sees, and reconcile_drift does not record a sensitivity-sync drift today (that
/// is the separate B10-reconcile-fix). Exposing `sensitive`/`hasRedactPatterns` still turns the manual
/// "0/N redacted" query into a queryable field so that gap is detectable downstream.
/// </summary>
public static class RedactionStatus
{
    /// <summary>Sensitive AND ≥1 declared pattern — redaction is wired.</summary>
    public const string Ok = "ok";
    /// <summary>Sensitive but NO patterns — runs UNREDACTED; a real leak risk.</summary>
    public const string Misconfigured = "misconfigured";
    /// <summary>Not flagged sensitive — redaction is not expected.</summary>
    public const string NotApplicable = "n/a";

    public static bool HasPatterns(IReadOnlyList<string>? redactPatterns) => redactPatterns is { Count: > 0 };

    public static string Health(bool sensitive, IReadOnlyList<string>? redactPatterns) =>
        !sensitive ? NotApplicable
        : HasPatterns(redactPatterns) ? Ok
        : Misconfigured;
}
