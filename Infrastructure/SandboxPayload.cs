using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// The SANDBOX PAYLOAD — the encrypted {spec, credentials} envelope that replaces the SW_SANDBOX_SPEC_B64
/// ARM env override. The runner mirror is runner/sandbox/sandboxPayload.ts; the FIELD NAMES BELOW ARE THE
/// WIRE CONTRACT and must match it byte-for-byte or the sandbox fails closed and every preview breaks.
///
/// ★ WHY THIS EXISTS: ACA persists a jobs/start env override VERBATIM on the execution resource — OBSERVED:
///     az containerapp job execution list -n synthwatch-sandbox -g synthwatch-rg
///       … "env": [ { "name": "SW_SANDBOX_SPEC_B64", "value": "aW1wb3J0IHsgdGVzdCwg…" } ]
///   readable by ANY Reader on the resource group. Execution history is bounded to the most recent 100
///   executions for SCHEDULED and EVENT jobs; synthwatch-sandbox is triggerType:'Manual', which that bound
///   does not cover — so retention is unbounded-by-contract. A typed password on that channel would sit in
///   Azure indefinitely, in plaintext, for a far wider audience than the vault holding CRED_ENC_KEY.
///
/// ── THE SPLIT-SECRET CHANNEL (neither half is sufficient) ────────────────────────────────────────────────
///   ARM env → SW_SANDBOX_CRED_KEY, a PER-RUN RANDOM AES-256 key. ★ This half leaks to execution history
///             BY DESIGN and that is fine: a key with no ciphertext decrypts nothing.
///   Blob    → {token}.payload, the AES-256-GCM ciphertext. Private container, MI-only, deleted on read by
///             the sandbox. Never appears in execution history at all.
///
/// ★ CRED_ENC_KEY IS NEVER INVOLVED. This reuses <see cref="CredCrypto"/>'s v1 envelope as a FORMAT ONLY —
///   note <see cref="Seal"/> generates its own random key and never calls CredCrypto.LoadKey/LoadKeyFromEnv.
///   The sandbox job has no secrets block and never receives CRED_ENC_KEY, so a hostile spec cannot decrypt
///   the fleet's stored monitor credentials — only the one the user just typed, for the run they asked for.
/// </summary>
public static class SandboxPayload
{
    /// <summary>AES-256 — the key length runner/crypto.ts validates (fail-closed on anything else).</summary>
    private const int KeyLen = 32;

    /// <summary>
    /// The user's OWN credentials for ONE preview run. Ephemeral by construction: they exist in the
    /// ciphertext blob (deleted on read), the sandbox process's memory, and the spec's child env — never in
    /// Postgres, never in the ARM body, never in audit_log (which records the actor + the spec HASH only).
    ///
    /// ★ <c>BypassToken</c> is the Vercel protection-bypass token, PASTED BY THE USER per-run — deliberately
    /// NOT server-injected from the platform's own VERCEL_BYPASS_TOKEN. Server-injecting a SHARED platform
    /// secret would let a hostile spec dump it, and would require deleting VERCEL_BYPASS_TOKEN from the
    /// runner's PROD_SECRET_ENV_NAMES — i.e. removing a currently-passing assertion
    /// (sandboxIsolation.test.ts) to make room for exactly the thing it catches.
    /// </summary>
    public sealed record Credentials(
        [property: JsonPropertyName("username")] string? Username,
        [property: JsonPropertyName("password")] string? Password,
        [property: JsonPropertyName("bypassToken")] string? BypassToken);

    /// <summary>The sealed envelope's plaintext shape. Mirrors runner sandboxPayload.ts SandboxPayload.</summary>
    public sealed record Envelope(
        [property: JsonPropertyName("spec")] string Spec,
        [property: JsonPropertyName("credentials")] Credentials? Credentials);

    /// <summary>Serialize with the explicit JsonPropertyName casing above — never the ambient policy.</summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // Absent credential fields are omitted rather than emitted as null; the runner treats both the same,
        // but a smaller envelope means less to hold in memory and nothing to misread.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>A sealed payload: the ciphertext for the blob, and the base64 key for the ARM env.</summary>
    /// <param name="Ciphertext">"v1:" + base64(IV ‖ ct ‖ tag) — the body of {token}.payload.</param>
    /// <param name="KeyBase64">base64 of the 32-byte key — the value of SW_SANDBOX_CRED_KEY.</param>
    public readonly record struct Sealed(string Ciphertext, string KeyBase64);

    /// <summary>
    /// Seal {spec, credentials} under a FRESH per-run AES-256 key.
    ///
    /// ★ The key is generated HERE, per call, from <see cref="RandomNumberGenerator"/> — it is NOT
    /// CRED_ENC_KEY and is never derived from it, stored, reused across runs, or written to the database.
    /// It exists only in this method's return value, the ARM start body, and that execution's history.
    /// </summary>
    public static Sealed Seal(string spec, Credentials? credentials)
    {
        var key = RandomNumberGenerator.GetBytes(KeyLen);
        try
        {
            var json = JsonSerializer.Serialize(new Envelope(spec, credentials), SerializerOptions);
            return new Sealed(CredCrypto.Encrypt(json, key), Convert.ToBase64String(key));
        }
        finally
        {
            // Clears the raw byte array only. ★ It does NOT scrub key material generally, and the comment
            // should not imply otherwise: Convert.ToBase64String already minted an immutable managed string
            // holding the key (which we return by design), and the JSON plaintext string containing the
            // password cannot be zeroed at all. This wipes the one copy that is cheap to wipe; the security
            // of this design rests on the split channel and delete-on-read, not on memory hygiene.
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>True iff the caller supplied ANY credential — the flag that makes a preview `sensitive`
    /// in the runner (which registers each value with makeRedactor as a knownValue).</summary>
    public static bool HasAny(Credentials? c) =>
        !string.IsNullOrEmpty(c?.Username) || !string.IsNullOrEmpty(c?.Password) || !string.IsNullOrEmpty(c?.BypassToken);
}
