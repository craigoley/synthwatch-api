using System.Text.Json;

using Azure.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using SynthWatch.Api.Dtos;
using SynthWatch.Api.Functions;
using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>
/// ★★ The preview CREDENTIAL path. Two properties carry this feature, and both are asserted here:
///   1. A typed credential is NEVER PERSISTED — not in sandbox_preview, not in the audit diff, not in the
///      ARM start body (which ACA copies verbatim into job execution history), not in any error string.
///   2. The ciphertext blob is written before the job starts and GONE after the run resolves.
///
/// The pure-crypto tests below run anywhere; the endpoint tests need the Testcontainers Postgres and skip
/// without Docker (same gate as IntegrationTests).
/// </summary>
[Collection("postgres")]
public class PreviewCredentialsTests
{
    private readonly PostgresFixture _pg;
    public PreviewCredentialsTests(PostgresFixture pg) => _pg = pg;

    private const string Sentinel = "SENTINEL_PW_9f3a17c4d2";
    private const string SentinelUser = "SENTINEL_USER_be21";
    private const string SentinelBypass = "SENTINEL_BYPASS_77aa";

    // ── The sealed-payload contract (no DB, no Azure) ────────────────────────────────────────────────────

    [Fact]
    public void Seal_produces_a_v1_envelope_the_runner_can_decrypt_with_the_returned_key()
    {
        var creds = new SandboxPayload.Credentials(SentinelUser, Sentinel, SentinelBypass);
        var sealedPayload = SandboxPayload.Seal("export const x = 1;", creds);

        Assert.StartsWith("v1:", sealedPayload.Ciphertext, StringComparison.Ordinal);
        // The key is what rides SW_SANDBOX_CRED_KEY — runner/crypto.ts fail-closes on anything but 32 bytes.
        Assert.Equal(32, Convert.FromBase64String(sealedPayload.KeyBase64).Length);

        // Round-trip through the SAME v1 contract the runner implements.
        var json = CredCrypto.Decrypt(sealedPayload.Ciphertext, Convert.FromBase64String(sealedPayload.KeyBase64));
        using var doc = JsonDocument.Parse(json);
        // ★ WIRE CONTRACT — these exact names are what runner/sandbox/sandboxPayload.ts reads. A rename here
        //   without a matching runner change fails the sandbox closed and breaks every preview.
        Assert.Equal("export const x = 1;", doc.RootElement.GetProperty("spec").GetString());
        var c = doc.RootElement.GetProperty("credentials");
        Assert.Equal(SentinelUser, c.GetProperty("username").GetString());
        Assert.Equal(Sentinel, c.GetProperty("password").GetString());
        Assert.Equal(SentinelBypass, c.GetProperty("bypassToken").GetString());
    }

    [Fact]
    public void Seal_mints_a_FRESH_key_per_run_and_is_not_CRED_ENC_KEY()
    {
        // ★ The whole split-secret argument rests on per-run keys: if two runs shared a key, a hostile spec
        //   that read a NEIGHBOUR's ciphertext could decrypt it with its own key.
        var a = SandboxPayload.Seal("s", null);
        var b = SandboxPayload.Seal("s", null);
        Assert.NotEqual(a.KeyBase64, b.KeyBase64);
        Assert.NotEqual(a.Ciphertext, b.Ciphertext); // fresh IV too

        // ★ And it must not be the fleet key. Seal works with CRED_ENC_KEY entirely absent — proving it never
        //   reads it (if it did, this would throw fail-closed).
        var prior = Environment.GetEnvironmentVariable("CRED_ENC_KEY");
        try
        {
            Environment.SetEnvironmentVariable("CRED_ENC_KEY", null);
            var c = SandboxPayload.Seal("s", new SandboxPayload.Credentials(null, Sentinel, null));
            Assert.StartsWith("v1:", c.Ciphertext, StringComparison.Ordinal);
        }
        finally { Environment.SetEnvironmentVariable("CRED_ENC_KEY", prior); }
    }

    [Fact]
    public void Seal_omits_the_credentials_node_entirely_when_there_are_none()
    {
        var s = SandboxPayload.Seal("spec", null);
        var json = CredCrypto.Decrypt(s.Ciphertext, Convert.FromBase64String(s.KeyBase64));
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("credentials", out _));
        Assert.False(SandboxPayload.HasAny(null));
        Assert.False(SandboxPayload.HasAny(new SandboxPayload.Credentials(null, null, null)));
        Assert.True(SandboxPayload.HasAny(new SandboxPayload.Credentials(null, Sentinel, null)));
    }

    // ── The endpoint: persistence, the ARM body, and the payload lifecycle ───────────────────────────────

    /// <summary>Records the payload blob lifecycle so the test can assert ordering + eventual absence.</summary>
    private sealed class FakePayloadStore : ISandboxPayloadStore
    {
        public readonly List<string> Events = [];
        public readonly Dictionary<string, string> Live = [];
        public bool WriteResult = true;

        public Task<bool> WriteAsync(string token, string ciphertext, CancellationToken ct)
        {
            if (!WriteResult) { Events.Add("write:FAILED"); return Task.FromResult(false); }
            Events.Add("write");
            Live[token] = ciphertext;
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAsync(string token, CancellationToken ct)
        {
            Events.Add("delete");
            Live.Remove(token);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeRunnerJob : IRunnerJobTrigger
    {
        public bool Result = true;
        public IReadOnlyDictionary<string, string>? LastEnv;
        public Task<bool> StartAsync(CancellationToken ct) => Task.FromResult(Result);
        public Task<bool> StartAsync(string jobName, CancellationToken ct) => Task.FromResult(Result);
        public Task<bool> StartWithEnvOverrideAsync(string jobName, string containerName, IReadOnlyDictionary<string, string> env, CancellationToken ct)
        {
            LastEnv = env;
            return Task.FromResult(Result);
        }
    }

    private static HttpRequest AuthJsonReq(string token, object body)
    {
        var ctx = new DefaultHttpContext();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentType = "application/json";
        ctx.Request.ContentLength = bytes.Length;
        ctx.Request.Headers.Authorization = $"Bearer {token}";
        return ctx.Request;
    }

    private static PreviewFunctions Fn(
        SynthWatch.Api.Data.SynthWatchDbContext db, IAuditScope audit, FakeRunnerJob job, FakePayloadStore store) =>
        new(db, new AuthPrincipalService(db), audit, job,
            Options.Create(new RunnerJobOptions()), new DefaultAzureCredential(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(),
            store, NullLogger<PreviewFunctions>.Instance);

    private async Task SeedEditorAsync(SynthWatch.Api.Data.SynthWatchDbContext db, string token, string email)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO editors (email, added_by) VALUES ({email}, 'system') ON CONFLICT DO NOTHING");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO sessions (token_hash, email, expires_at) VALUES ({AuthTokens.Sha256Hex(token)}, {email}, now() + interval '1 hour')");
    }

    [SkippableFact]
    public async Task Credentialed_preview_persists_no_credential_anywhere_and_keeps_the_spec_out_of_the_ARM_body()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        await using var db = _pg.NewDbContext();
        const string tok = "swt_preview_cred_editor", email = "cred@preview.test";
        await SeedEditorAsync(db, tok, email);
        try
        {
            var audit = new AuditScope();
            var job = new FakeRunnerJob();
            var store = new FakePayloadStore();
            var fn = Fn(db, audit, job, store);

            var spec = $"import {{ test }} from '../../lib/flow'; // {Sentinel} must not leak\ntest('t', async () => {{}});";
            var result = await fn.CreatePreview(AuthJsonReq(tok, new
            {
                spec,
                targetUrl = "https://example.com",
                credentials = new { username = SentinelUser, password = Sentinel, vercelBypassToken = SentinelBypass },
            }), default);

            var accepted = Assert.IsType<AcceptedResult>(result);
            var token = Assert.IsType<CreatePreviewAcceptedDto>(accepted.Value).Token;

            // ── 1. THE ARM BODY. This is what ACA copies verbatim into job execution history. ──
            Assert.NotNull(job.LastEnv);
            // ★ The spec no longer travels here at all.
            Assert.False(job.LastEnv!.ContainsKey("SW_SANDBOX_SPEC_B64"));
            Assert.Equal(new[] { "SW_SANDBOX_CRED_KEY", "SW_SANDBOX_RESULT_TOKEN", "SW_SANDBOX_TARGET_URL" },
                job.LastEnv.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            // ★ No credential and no spec byte in any VALUE either (a renamed key would still leak).
            foreach (var v in job.LastEnv.Values)
            {
                Assert.DoesNotContain(Sentinel, v, StringComparison.Ordinal);
                Assert.DoesNotContain(SentinelUser, v, StringComparison.Ordinal);
                Assert.DoesNotContain(SentinelBypass, v, StringComparison.Ordinal);
                Assert.DoesNotContain("lib/flow", v, StringComparison.Ordinal);
            }

            // ── 2. THE DB ROW. spec_sha256 only — no spec body, no credential. ──
            var row = await db.SandboxPreviews.AsNoTracking().FirstAsync(p => p.Token == token);
            var rowJson = JsonSerializer.Serialize(row);
            Assert.DoesNotContain(Sentinel, rowJson, StringComparison.Ordinal);
            Assert.DoesNotContain(SentinelUser, rowJson, StringComparison.Ordinal);
            Assert.DoesNotContain(SentinelBypass, rowJson, StringComparison.Ordinal);
            Assert.Equal(64, row.SpecSha256.Length); // the hash IS retained — non-vacuity for the scan above

            // ── 3. THE AUDIT DIFF. {specSha256, targetUrl} and nothing else. ──
            Assert.NotNull(audit.Diff);
            var afterJson = JsonSerializer.Serialize(audit.Diff!.After);
            Assert.DoesNotContain(Sentinel, afterJson, StringComparison.Ordinal);
            Assert.DoesNotContain(SentinelUser, afterJson, StringComparison.Ordinal);
            Assert.DoesNotContain(SentinelBypass, afterJson, StringComparison.Ordinal);
            Assert.Contains(row.SpecSha256, afterJson, StringComparison.Ordinal);

            // ── 4. THE PAYLOAD IS THE ONLY CARRIER — and it is CIPHERTEXT, not plaintext. ──
            Assert.Equal(["write"], store.Events);
            var ciphertext = store.Live[token];
            Assert.StartsWith("v1:", ciphertext, StringComparison.Ordinal);
            Assert.DoesNotContain(Sentinel, ciphertext, StringComparison.Ordinal);
            // ★ NON-VACUITY: the credential really IS in there — under the key from the ARM env, and only that
            //   key. Proves the scans above pass because of encryption, not because we dropped the credential.
            var plaintext = CredCrypto.Decrypt(ciphertext, Convert.FromBase64String(job.LastEnv["SW_SANDBOX_CRED_KEY"]));
            Assert.Contains(Sentinel, plaintext, StringComparison.Ordinal);
            Assert.Contains(SentinelBypass, plaintext, StringComparison.Ordinal);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM sandbox_preview WHERE actor_email LIKE '%@preview.test'; " +
                "DELETE FROM sessions WHERE email LIKE '%@preview.test'; DELETE FROM editors WHERE email LIKE '%@preview.test';");
        }
    }

    [SkippableFact]
    public async Task Payload_blob_is_written_then_swept_when_the_run_resolves()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        await using var db = _pg.NewDbContext();
        const string tok = "swt_preview_sweep_editor", email = "sweep@preview.test";
        await SeedEditorAsync(db, tok, email);
        try
        {
            var job = new FakeRunnerJob();
            var store = new FakePayloadStore();
            var fn = Fn(db, new AuditScope(), job, store);

            var accepted = Assert.IsType<AcceptedResult>(await fn.CreatePreview(
                AuthJsonReq(tok, new { spec = "test('t', async () => {});", credentials = new { password = Sentinel } }), default));
            var token = Assert.IsType<CreatePreviewAcceptedDto>(accepted.Value).Token;
            Assert.True(store.Live.ContainsKey(token), "the ciphertext must exist while the run is in flight");

            // Age the row past RunningStaleAfter so the GET takes the stale-sweep branch (the crash case: the
            // job died before reading, so nothing ever deleted the payload).
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE sandbox_preview SET requested_at = now() - interval '10 minutes' WHERE token = {token}");
            db.ChangeTracker.Clear();

            var polled = Assert.IsType<OkObjectResult>(await fn.GetPreview(AuthJsonReq(tok, new { }), token, default));
            Assert.Equal("timeout", Assert.IsType<PreviewStatusDto>(polled.Value).Status);

            // ★ THE SWEEP: the orphaned ciphertext is gone — not left to the ~1-day lifecycle floor while its
            //   key sits in ACA execution history permanently.
            Assert.False(store.Live.ContainsKey(token), "the orphaned payload must be swept on the stale transition");
            Assert.Equal(["write", "delete"], store.Events);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM sandbox_preview WHERE actor_email LIKE '%@preview.test'; " +
                "DELETE FROM sessions WHERE email LIKE '%@preview.test'; DELETE FROM editors WHERE email LIKE '%@preview.test';");
        }
    }

    [SkippableFact]
    public async Task A_failed_payload_write_does_not_start_the_job_and_leaks_nothing_in_the_error()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        await using var db = _pg.NewDbContext();
        const string tok = "swt_preview_failwrite_editor", email = "failwrite@preview.test";
        await SeedEditorAsync(db, tok, email);
        try
        {
            var job = new FakeRunnerJob();
            var store = new FakePayloadStore { WriteResult = false };
            var fn = Fn(db, new AuditScope(), job, store);

            var result = await fn.CreatePreview(
                AuthJsonReq(tok, new { spec = "test('t', async () => {});", credentials = new { password = Sentinel } }), default);

            Assert.IsType<ObjectResult>(result);
            Assert.Equal(503, ((ObjectResult)result).StatusCode);
            // ★ The job is never started with a key whose ciphertext does not exist.
            Assert.Null(job.LastEnv);
            // ★ And the failure surfaces nothing about the credential — the stored + returned messages are
            //   fixed strings.
            var row = await db.SandboxPreviews.AsNoTracking().FirstAsync(p => p.ActorEmail == email);
            Assert.Equal("failed", row.Status);
            Assert.DoesNotContain(Sentinel, row.Error ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain(Sentinel, JsonSerializer.Serialize(((ObjectResult)result).Value), StringComparison.Ordinal);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM sandbox_preview WHERE actor_email LIKE '%@preview.test'; " +
                "DELETE FROM sessions WHERE email LIKE '%@preview.test'; DELETE FROM editors WHERE email LIKE '%@preview.test';");
        }
    }

    [SkippableFact]
    public async Task A_failed_job_start_deletes_the_staged_payload()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        await using var db = _pg.NewDbContext();
        const string tok = "swt_preview_failstart_editor", email = "failstart@preview.test";
        await SeedEditorAsync(db, tok, email);
        try
        {
            // ★ This path had NO coverage: FakeRunnerJob.Result was settable and nothing ever set it false,
            //   so the cleanup line could be deleted and the whole suite stayed green.
            var job = new FakeRunnerJob { Result = false };
            var store = new FakePayloadStore();
            var fn = Fn(db, new AuditScope(), job, store);

            var result = await fn.CreatePreview(
                AuthJsonReq(tok, new { spec = "test('t', async () => {});", credentials = new { password = Sentinel } }), default);

            Assert.Equal(503, Assert.IsType<ObjectResult>(result).StatusCode);
            // ★ The job never started, so nothing will ever read (and delete) the payload. It must be cleaned
            //   up NOW rather than left to the ~1-day lifecycle floor while its key sits in the failed
            //   execution's ARM history.
            Assert.Equal(["write", "delete"], store.Events);
            Assert.Empty(store.Live);
            var row = await db.SandboxPreviews.AsNoTracking().FirstAsync(p => p.ActorEmail == email);
            Assert.Equal("failed", row.Status);
            Assert.DoesNotContain(Sentinel, row.Error ?? "", StringComparison.Ordinal);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM sandbox_preview WHERE actor_email LIKE '%@preview.test'; " +
                "DELETE FROM sessions WHERE email LIKE '%@preview.test'; DELETE FROM editors WHERE email LIKE '%@preview.test';");
        }
    }

    [SkippableFact]
    public async Task An_abandoned_preview_is_swept_by_the_NEXT_preview_not_only_by_a_poll()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        await using var db = _pg.NewDbContext();
        const string tok = "swt_preview_sweepcreate_editor", email = "sweepcreate@preview.test";
        await SeedEditorAsync(db, tok, email);
        try
        {
            var job = new FakeRunnerJob();
            var store = new FakePayloadStore();
            var fn = Fn(db, new AuditScope(), job, store);

            // Run one preview, then ABANDON it — no polling at all, which is what a closed tab looks like.
            var first = Assert.IsType<AcceptedResult>(await fn.CreatePreview(
                AuthJsonReq(tok, new { spec = "test('a', async () => {});", credentials = new { password = Sentinel } }), default));
            var abandoned = Assert.IsType<CreatePreviewAcceptedDto>(first.Value).Token;
            Assert.True(store.Live.ContainsKey(abandoned));

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE sandbox_preview SET requested_at = now() - interval '10 minutes' WHERE token = {abandoned}");
            db.ChangeTracker.Clear();

            // ★ A DIFFERENT preview starts. Nobody ever polled the first one — if cleanup lived only in
            //   GetPreview, that ciphertext would sit until the ~1-day lifecycle floor.
            await fn.CreatePreview(AuthJsonReq(tok, new { spec = "test('b', async () => {});" }), default);

            Assert.False(store.Live.ContainsKey(abandoned), "the abandoned payload must be swept by the next create");
            var swept = await db.SandboxPreviews.AsNoTracking().FirstAsync(p => p.Token == abandoned);
            Assert.Equal("timeout", swept.Status);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM sandbox_preview WHERE actor_email LIKE '%@preview.test'; " +
                "DELETE FROM sessions WHERE email LIKE '%@preview.test'; DELETE FROM editors WHERE email LIKE '%@preview.test';");
        }
    }

    [Fact]
    public void Whitespace_only_credentials_are_rejected_so_they_cannot_poison_the_redactor()
    {
        // ★ "   " used to survive: IsNullOrEmpty accepts it, so the run became `sensitive` (screenshot
        //   suppressed for a credential that authenticates nothing) AND "   " was registered as a redactor
        //   knownValue — whose only guard is a <3-char skip, which three spaces clears — shredding every run
        //   of three spaces in the trace into <redacted>.
        Assert.Null(NormalizeForTest(new CreatePreviewCredentials("  ", "   ", "\t")));

        // ★ But whitespace INSIDE a real value is preserved — the reason password/token are never Trim()'d.
        //   A secret whose leading space is silently eaten fails authentication for a mysterious reason.
        var kept = NormalizeForTest(new CreatePreviewCredentials("user", " p a s s ", null));
        Assert.NotNull(kept);
        Assert.Equal(" p a s s ", kept!.Password);
        Assert.Equal("user", kept.Username); // username IS trimmed — it is an identifier, not a secret
        Assert.Null(kept.BypassToken);
    }

    /// <summary>Reaches NormalizeCredentials via the same public shape the endpoint uses.</summary>
    private static SandboxPayload.Credentials? NormalizeForTest(CreatePreviewCredentials c)
    {
        var m = typeof(PreviewFunctions).GetMethod("NormalizeCredentials",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (SandboxPayload.Credentials?)m.Invoke(null, [c]);
    }

    [SkippableFact]
    // ★ Named for what it actually asserts. The previous name ("behaves as before the feature") was
    //   contradicted by its own first assertion — SW_SANDBOX_SPEC_B64 is gone for EVERY preview now, which is
    //   precisely not-as-before. What holds is that no credentials node is sent, so the runner keeps its
    //   non-sensitive treatment.
    public async Task Uncredentialed_preview_sends_no_credentials_node_and_stays_non_sensitive()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        await using var db = _pg.NewDbContext();
        const string tok = "swt_preview_plain_editor", email = "plain@preview.test";
        await SeedEditorAsync(db, tok, email);
        try
        {
            var job = new FakeRunnerJob();
            var store = new FakePayloadStore();
            var fn = Fn(db, new AuditScope(), job, store);

            var accepted = Assert.IsType<AcceptedResult>(await fn.CreatePreview(
                AuthJsonReq(tok, new { spec = "test('t', async () => {});" }), default));
            var token = Assert.IsType<CreatePreviewAcceptedDto>(accepted.Value).Token;

            // Same three env keys, same bounds, same 202 — the only difference from a credentialed run is what
            // is inside the ciphertext, and here it carries NO credentials node at all (so the runner keeps
            // IDENTITY_REDACTOR and the raw trace + screenshot, exactly as today).
            Assert.NotNull(job.LastEnv);
            Assert.False(job.LastEnv!.ContainsKey("SW_SANDBOX_SPEC_B64"));
            var plaintext = CredCrypto.Decrypt(store.Live[token], Convert.FromBase64String(job.LastEnv["SW_SANDBOX_CRED_KEY"]));
            using var doc = JsonDocument.Parse(plaintext);
            Assert.False(doc.RootElement.TryGetProperty("credentials", out _));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM sandbox_preview WHERE actor_email LIKE '%@preview.test'; " +
                "DELETE FROM sessions WHERE email LIKE '%@preview.test'; DELETE FROM editors WHERE email LIKE '%@preview.test';");
        }
    }
}
