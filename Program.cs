using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using SynthWatch.Api.Data;
using SynthWatch.Api.Infrastructure;

var builder = FunctionsApplication.CreateBuilder(args);

// ASP.NET Core integration model: real HTTP routing + IActionResult/JSON.
builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// Options bound from app settings (Postgres__*).
builder.Services.Configure<PostgresOptions>(builder.Configuration.GetSection("Postgres"));
// NOTE: CORS is handled by the PLATFORM (Function App siteConfig.cors), NOT app code. The host
// answers the OPTIONS preflight itself before the worker is ever invoked, so app-level CORS
// middleware/functions cannot intercept preflight. See infra/main.bicep (siteConfig.cors).

// Single managed-identity-authenticated Npgsql data source for the whole app.
builder.Services.AddSingleton(PostgresDataSourceFactory.Create);

// Managed-identity credential for reading trace blobs from the artifacts storage account
// (the trace-download proxy). Same DefaultAzureCredential family the DB token uses.
builder.Services.AddSingleton<Azure.Core.TokenCredential>(_ => new Azure.Identity.DefaultAzureCredential());

// The ONE place we fetch runner-written artifact blobs (traces/screenshots): host-allowlist + MI BlobClient +
// 404/non-404 classification. Shared by the artifact proxy (ArtifactsFunctions) and the AI-insights endpoint.
builder.Services.AddSingleton<SynthWatch.Api.Infrastructure.IArtifactReader, SynthWatch.Api.Infrastructure.ArtifactReader>();

// Mints short-TTL, read-only, single-blob user-delegation SAS URLs so the browser fetches large traces
// (124 MB+) DIRECTLY from Blob instead of streaming through the Vercel serverless proxy (which cuts off at
// its ~15 s maxDuration). Uses the SAME DefaultAzureCredential (the MI needs Storage Blob Delegator).
builder.Services.AddSingleton<SynthWatch.Api.Infrastructure.IBlobSasMinter, SynthWatch.Api.Infrastructure.BlobSasMinter>();

// Auth (Phase 12 slice 1): OTP / access-request emails send via ACS using the SAME managed-identity
// credential (preferred; connection-string fallback) — see AcsEmailSender. This slice is purely additive:
// it mints/verifies sessions, but NOTHING enforces them yet (the authz gate is slice 2).
builder.Services.AddSingleton<IEmailSender, AcsEmailSender>();

// Channel test-send (POST /api/channels/{id}/test, Option A): the API enqueues a test_send_requests row
// and starts the RUNNER Container App Job on-demand via ARM, so the runner's REAL dispatch path sends the
// [TEST] alert (no C# ACS replica). RunnerJob__* config overrides the resource coordinates; the ARM token
// uses the SAME DefaultAzureCredential the DB/blob tokens use (registered above).
builder.Services.AddHttpClient();
builder.Services.Configure<RunnerJobOptions>(builder.Configuration.GetSection("RunnerJob"));
builder.Services.AddSingleton<IRunnerJobTrigger, ArmRunnerJobTrigger>();

// Trace AI Insights (slice 2): a typed HttpClient for the AOAI chat-completions REST call. Reuses the
// SAME DefaultAzureCredential (registered above) for the cognitive-services token. INERT until AZURE_OPENAI_*
// is configured (IsConfigured=false → the endpoint returns "not configured", never a 500).
builder.Services.AddHttpClient<IAoaiClient, AoaiClient>();

// Read-mostly EF Core context over the runner-owned schema (no migrations).
builder.Services.AddDbContext<SynthWatchDbContext>((sp, options) =>
{
    var dataSource = sp.GetRequiredService<NpgsqlDataSource>();
    options.UseNpgsql(dataSource);
});

// Phase 12 slice 2 — the authz gate + audit. The principal resolver (bearer → session → live role) is the
// ONE source of truth shared by the middleware; the audit scope is the per-request handler-diff channel.
builder.Services.AddScoped<IAuthPrincipal, AuthPrincipalService>();
builder.Services.AddScoped<IAuditScope, AuditScope>();

// Worker middleware (outermost first): request logging (times whole pipeline + final status), then
// exception shielding, then the authorization gate — INSIDE shielding so a session-lookup error becomes a
// shielded 500 = DENIED (fail-closed). The gate ENFORCES by default — inert only when
// AUTH_ENFORCEMENT_ENABLED is EXPLICITLY "false"/"0" (fail-closed: #161 runtime default ON-when-unset, #173 bicep default true).
builder.UseMiddleware<RequestLoggingMiddleware>();
builder.UseMiddleware<ExceptionHandlingMiddleware>();
builder.UseMiddleware<AuthorizationMiddleware>();

var host = builder.Build();

// ★ Enforcement is fail-closed (ON unless AUTH_ENFORCEMENT_ENABLED is explicitly "false"/"0"). Turning it
// OFF opens every write, all three paid-AOAI endpoints, and reconcile/apply — legitimate only as a temporary
// deploy-safety measure, so make that state impossible to miss in the logs.
if (!AuthorizationMiddleware.EnforcementEnabled())
{
    StartupLog.EnforcementOff(host.Services
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("SynthWatch.Api.Startup"));
}

host.Run();

/// <summary>High-performance (CA1848) startup log delegates — same idiom as RequestLog/ExceptionLog.</summary>
internal static partial class StartupLog
{
    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning,
        Message = "AUTH_ENFORCEMENT_ENABLED is explicitly OFF — every write, all paid-AOAI endpoints, and reconcile/apply are OPEN to anonymous callers. This is a temporary deploy-safety state only; remove the setting (or set it true) to enforce.")]
    public static partial void EnforcementOff(ILogger logger);
}
