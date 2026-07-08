using System.Security.Cryptography;
using System.Text;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Shared credential-value crypto (model B) — the .NET mirror of the runner's runner/crypto.ts. The api
/// ENCRYPTS on write; the runner DECRYPTS on read. Both MUST implement this AES-256-GCM contract
/// byte-identically or the runner cannot decrypt what the api stored, and every login monitor fails closed.
///
/// CONTRACT (v1):
///   key    : 32 raw bytes (AES-256). Env CRED_ENC_KEY = base64(32 bytes); base64-decoded both sides; must
///            be exactly 32 bytes or fail-closed (no plaintext fallback).
///   scheme : AES-256-GCM. IV = 12 random bytes per value. Auth tag = 16 bytes. No AAD in v1.
///   stored : "v1:" + base64( IV(12) || ciphertext || tag(16) ). Unknown prefix => fail-closed.
///
/// The SAME known-answer vector is asserted in tests/CredCryptoTests.cs and runner/crypto.test.ts. The key is
/// NEVER logged / returned in a DTO or error (messages name the env var, never its value).
/// </summary>
public static class CredCrypto
{
    public const string Version = "v1";
    private const int IvLen = 12;   // GCM standard nonce
    private const int TagLen = 16;  // GCM auth tag
    private const int KeyLen = 32;  // AES-256

    /// <summary>
    /// Load + validate the AES key from CRED_ENC_KEY (base64 of 32 bytes). FAIL-CLOSED: absent, non-base64,
    /// or wrong length throws — the caller must NOT store plaintext when the key is unusable.
    /// </summary>
    public static byte[] LoadKey(string? credEncKey)
    {
        if (string.IsNullOrEmpty(credEncKey))
            throw new InvalidOperationException("CRED_ENC_KEY is not set — cannot encrypt credential values (fail-closed)");
        byte[] key;
        try { key = Convert.FromBase64String(credEncKey); }
        catch (FormatException) { throw new InvalidOperationException("CRED_ENC_KEY is not valid base64 (fail-closed)"); }
        if (key.Length != KeyLen)
            throw new InvalidOperationException($"CRED_ENC_KEY must decode to {KeyLen} bytes (AES-256); got {key.Length} (fail-closed)");
        return key;
    }

    /// <summary>Load the key from the process env (the deployed CRED_ENC_KEY secret).</summary>
    public static byte[] LoadKeyFromEnv() => LoadKey(Environment.GetEnvironmentVariable("CRED_ENC_KEY"));

    /// <summary>
    /// Encrypt <paramref name="plaintext"/> under <paramref name="key"/> → "v1:" + base64(IV || ct || tag).
    /// A fresh random IV each call. <paramref name="ivOverride"/> is TEST-ONLY (deterministic KAT) — never in prod.
    /// </summary>
    public static string Encrypt(string plaintext, byte[] key, byte[]? ivOverride = null)
    {
        var iv = ivOverride ?? RandomNumberGenerator.GetBytes(IvLen);
        if (iv.Length != IvLen) throw new ArgumentException($"IV must be {IvLen} bytes", nameof(ivOverride));
        var pt = Encoding.UTF8.GetBytes(plaintext);
        var ct = new byte[pt.Length];
        var tag = new byte[TagLen];
        using var gcm = new AesGcm(key, TagLen);
        gcm.Encrypt(iv, pt, ct, tag);

        var envelope = new byte[IvLen + ct.Length + TagLen];
        Buffer.BlockCopy(iv, 0, envelope, 0, IvLen);
        Buffer.BlockCopy(ct, 0, envelope, IvLen, ct.Length);
        Buffer.BlockCopy(tag, 0, envelope, IvLen + ct.Length, TagLen);
        return $"{Version}:{Convert.ToBase64String(envelope)}";
    }

    /// <summary>
    /// Decrypt a "v1:"-prefixed value produced by <see cref="Encrypt"/> (or the runner mirror). FAIL-CLOSED
    /// (throws) on: unknown/absent version, malformed base64, too-short envelope, or a failing auth tag
    /// (tampered ciphertext / wrong key). A throw means the value is unusable — never treat it as plaintext.
    /// </summary>
    public static string Decrypt(string stored, byte[] key)
    {
        var sep = stored.IndexOf(':');
        var version = sep == -1 ? "" : stored[..sep];
        if (version != Version)
            throw new InvalidOperationException($"unsupported credential-crypto version \"{version}\" (expected {Version})");

        byte[] buf;
        try { buf = Convert.FromBase64String(stored[(sep + 1)..]); }
        catch (FormatException) { throw new InvalidOperationException("credential ciphertext is not valid base64 (fail-closed)"); }
        if (buf.Length < IvLen + TagLen)
            throw new InvalidOperationException("credential ciphertext too short / malformed (fail-closed)");

        var ctLen = buf.Length - IvLen - TagLen;
        var iv = new byte[IvLen];
        var ct = new byte[ctLen];
        var tag = new byte[TagLen];
        Buffer.BlockCopy(buf, 0, iv, 0, IvLen);
        Buffer.BlockCopy(buf, IvLen, ct, 0, ctLen);
        Buffer.BlockCopy(buf, IvLen + ctLen, tag, 0, TagLen);

        var pt = new byte[ctLen];
        using var gcm = new AesGcm(key, TagLen);
        gcm.Decrypt(iv, ct, tag, pt); // throws AuthenticationTagMismatchException on tamper / wrong key
        return Encoding.UTF8.GetString(pt);
    }
}
