using System.Security.Cryptography;
using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>
/// The load-bearing cross-repo test: the KAT vector here MUST be byte-identical to runner/crypto.test.ts
/// (same key+IV+plaintext → the same "v1:…" envelope). If the api and the runner AES-256-GCM disagree on any
/// detail, the runner can't decrypt the api's ciphertext and login monitors fail closed in prod. Change the
/// vector in lockstep across both repos or not at all.
/// </summary>
public class CredCryptoTests
{
    // ── KNOWN-ANSWER VECTOR (identical to runner/crypto.test.ts) ──
    private const string KeyB64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="; // 32 bytes 0x00..0x1f
    private const string IvB64 = "AAECAwQFBgcICQoL";                               // 12 bytes 0x00..0x0b
    private const string Plaintext = "correct horse battery staple";
    private const string Stored = "v1:AAECAwQFBgcICQoLJG2kaaCGtjvlLuX41MkaDPei4kaJWywIWReJ4O6At8GgxQlkvg7OPAnd3D8=";

    private static byte[] Key => Convert.FromBase64String(KeyB64);
    private static byte[] Iv => Convert.FromBase64String(IvB64);

    [Fact]
    public void KAT_encrypt_matches_the_cross_repo_vector()
    {
        Assert.Equal(Stored, CredCrypto.Encrypt(Plaintext, Key, Iv));
    }

    [Fact]
    public void KAT_decrypt_the_fixed_envelope_returns_plaintext()
    {
        Assert.Equal(Plaintext, CredCrypto.Decrypt(Stored, Key));
    }

    [Fact]
    public void RoundTrip_random_iv_differs_but_both_decrypt()
    {
        var a = CredCrypto.Encrypt("s3cr3t-p@ss word!", Key);
        var b = CredCrypto.Encrypt("s3cr3t-p@ss word!", Key);
        Assert.NotEqual(a, b); // random IV → distinct ciphertexts
        Assert.Equal("s3cr3t-p@ss word!", CredCrypto.Decrypt(a, Key));
        Assert.Equal("s3cr3t-p@ss word!", CredCrypto.Decrypt(b, Key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("café ☕ π")]
    public void RoundTrip_unicode_and_empty(string v)
    {
        Assert.Equal(v, CredCrypto.Decrypt(CredCrypto.Encrypt(v, Key), Key));
    }

    [Fact]
    public void Decrypt_fails_on_tampered_tag()
    {
        var stored = CredCrypto.Encrypt("do-not-forge", Key);
        var body = Convert.FromBase64String(stored[3..]);
        body[^1] ^= 0xff; // flip a tag byte
        var tampered = "v1:" + Convert.ToBase64String(body);
        Assert.ThrowsAny<CryptographicException>(() => CredCrypto.Decrypt(tampered, Key));
    }

    [Fact]
    public void Decrypt_fails_with_wrong_key()
    {
        var stored = CredCrypto.Encrypt("secret", Key);
        var wrong = new byte[32];
        Array.Fill(wrong, (byte)0xAA);
        Assert.ThrowsAny<CryptographicException>(() => CredCrypto.Decrypt(stored, wrong));
    }

    [Fact]
    public void Decrypt_fails_on_unknown_or_absent_version()
    {
        Assert.Throws<InvalidOperationException>(() => CredCrypto.Decrypt("v2:AAAA", Key));
        Assert.Throws<InvalidOperationException>(() => CredCrypto.Decrypt("plaintext-no-prefix", Key));
    }

    [Fact]
    public void LoadKey_fail_closed_and_never_echoes_the_value()
    {
        Assert.Throws<InvalidOperationException>(() => CredCrypto.LoadKey(""));
        Assert.Throws<InvalidOperationException>(() => CredCrypto.LoadKey(null));
        Assert.Throws<InvalidOperationException>(() => CredCrypto.LoadKey(Convert.ToBase64String(new byte[16])));
        Assert.Equal(32, CredCrypto.LoadKey(KeyB64).Length);
        var ex = Assert.Throws<InvalidOperationException>(() => CredCrypto.LoadKey("SHORTKEYVALUE"));
        Assert.DoesNotContain("SHORTKEYVALUE", ex.Message);
    }
}
