using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using SynthWatch.Api.Data;
using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;

namespace SynthWatch.Api.Functions;

/// <summary>
/// PUT /api/checks/{id}/credentials — model B: SET a monitor's secret_headers / login_credentials VALUES.
/// Editor-gated automatically (PUT is a mutating verb → the AuthorizationMiddleware verb-gate requires an
/// editor/admin session — see AuthGate). On write each plaintext value is ENCRYPTED with CRED_ENC_KEY
/// (CredCrypto v1 — the SAME scheme the runner decrypts) before store; the DB never holds plaintext.
///
/// ★ WRITE-ONLY: the response (and every read DTO) returns MASKED slots ({ key -> "set" }), never the
/// plaintext OR the ciphertext (CredMask). A GET after this PUT cannot round-trip the value.
///
/// Semantics: each provided map REPLACES that column (send the full desired set — add/edit/remove = the new
/// map). A null/omitted map leaves that column unchanged; an EMPTY map clears it. 503 (fail-closed) if
/// CRED_ENC_KEY is absent on the app — never store plaintext when it can't encrypt.
/// </summary>
public class CredWriteFunctions
{
    private readonly SynthWatchDbContext _db;

    public CredWriteFunctions(SynthWatchDbContext db) => _db = db;

    [Function("PutCheckCredentials")]
    public async Task<IActionResult> PutCheckCredentials(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "checks/{id:long}/credentials")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        var (body, bodyError) = await RequestJson.ReadAsync<PutCredentialsRequest>(req, ct);
        if (bodyError is not null) return bodyError;
        if (body is null) return ApiResults.BadRequest("Request body is required.");
        if (body.SecretHeaders is null && body.LoginCredentials is null)
            return ApiResults.BadRequest("Provide secretHeaders and/or loginCredentials.");

        var check = await _db.Checks.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (check is null) return ApiResults.NotFound($"Check {id} not found.");

        byte[] key;
        try { key = CredCrypto.LoadKeyFromEnv(); }
        catch (InvalidOperationException)
        {
            // Fail-closed: never store plaintext when the app can't encrypt. Message names no secret.
            return new ObjectResult(new { error = "CRED_ENC_KEY not configured — cannot store credentials" }) { StatusCode = 503 };
        }

        // Each provided map REPLACES the column, each value ENCRYPTED. Empty map -> null (clears the column).
        if (body.SecretHeaders is not null) check.SecretHeaders = EncryptAll(body.SecretHeaders, key);
        if (body.LoginCredentials is not null) check.LoginCredentials = EncryptAll(body.LoginCredentials, key);

        await _db.SaveChangesAsync(ct);

        // WRITE-ONLY echo: masked slots only, never the value/ciphertext.
        return ApiResults.Ok(new
        {
            secretHeaders = CredMask.Of(check.SecretHeaders),
            loginCredentials = CredMask.Of(check.LoginCredentials),
        });
    }

    private static Dictionary<string, string>? EncryptAll(IReadOnlyDictionary<string, string> plain, byte[] key)
    {
        if (plain.Count == 0) return null; // empty map = clear the column
        var enc = new Dictionary<string, string>(plain.Count);
        foreach (var (k, v) in plain) enc[k] = CredCrypto.Encrypt(v, key);
        return enc;
    }
}

/// <summary>Body for PUT /checks/{id}/credentials — PLAINTEXT values in, encrypted before store. A null map
/// leaves that column unchanged; an empty map clears it.</summary>
public record PutCredentialsRequest(
    IReadOnlyDictionary<string, string>? SecretHeaders,
    IReadOnlyDictionary<string, string>? LoginCredentials);
