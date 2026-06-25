using System.Security.Cryptography;
using System.Text;

namespace SynthWatch.Api.Infrastructure;

/// <summary>Role names used across auth. Anonymous = no/invalid session (read-only).</summary>
public static class Roles
{
    public const string Admin = "admin";
    public const string Editor = "editor";
    public const string Anonymous = "anonymous";
}

/// <summary>
/// Crypto helpers for the OTP + session flow. Codes and tokens are stored ONLY as their SHA-256 hash —
/// the raw value is shown to the user once and never persisted, so a DB leak can't replay a live code
/// or session. All randomness is from <see cref="RandomNumberGenerator"/>.
/// </summary>
public static class AuthTokens
{
    /// <summary>Lowercase + trim — the canonical form for every email comparison (admin list, editors, OTP).</summary>
    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    /// <summary>Lowercase hex SHA-256 — used to hash both OTP codes and session tokens before storage.</summary>
    public static string Sha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>A uniformly-random 6-digit code (000000–999999), zero-padded.</summary>
    public static string NewNumericCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>An opaque session token: <c>swt_</c> + 256 bits of base64url randomness. Returned once; stored hashed.</summary>
    public static string NewSessionToken()
    {
        var raw = RandomNumberGenerator.GetBytes(32);
        var b64 = Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"swt_{b64}";
    }

    /// <summary>Extracts the bearer token from an <c>Authorization: Bearer &lt;token&gt;</c> header value, or null.</summary>
    public static string? BearerFrom(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
            return null;
        const string prefix = "Bearer ";
        return authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? authorizationHeader[prefix.Length..].Trim() is { Length: > 0 } t ? t : null
            : null;
    }
}
