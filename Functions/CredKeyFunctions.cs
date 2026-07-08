using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using SynthWatch.Api.Infrastructure;

namespace SynthWatch.Api.Functions;

/// <summary>
/// GET /api/cred-key/fingerprint — a NON-SECRET fingerprint of the deployed CRED_ENC_KEY, for deploy-time
/// drift detection. The runner's deploy.sh computes the SAME fingerprint from ~/.synthwatch.env's
/// CRED_ENC_KEY and asserts it matches this — so a runner/api key divergence is caught at deploy time instead
/// of as a silent prod decrypt failure. Returns ONLY the 16-hex fingerprint (a domain-separated, truncated
/// sha256 — never the key). Anonymous: it reveals nothing about the key. 503 (fail-closed) if the key is
/// absent/invalid on the app — the deploy check treats that as a hard fail, never a fingerprint of garbage.
/// </summary>
public class CredKeyFunctions
{
    [Function("GetCredKeyFingerprint")]
    public static IActionResult GetCredKeyFingerprint(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "cred-key/fingerprint")] HttpRequest req)
    {
        string fingerprint;
        try
        {
            fingerprint = CredCrypto.Fingerprint(Environment.GetEnvironmentVariable("CRED_ENC_KEY"));
        }
        catch (InvalidOperationException)
        {
            // Key absent/invalid on the app — fail-closed. Message is generic (never the key / its reason detail).
            return new ObjectResult(new { error = "CRED_ENC_KEY not configured or invalid" }) { StatusCode = 503 };
        }
        return ApiResults.Ok(new { fingerprint });
    }
}
