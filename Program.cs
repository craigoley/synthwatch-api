using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
// shielded 500 = DENIED (fail-closed). The gate is inert unless AUTH_ENFORCEMENT_ENABLED (default OFF).
builder.UseMiddleware<RequestLoggingMiddleware>();
builder.UseMiddleware<ExceptionHandlingMiddleware>();
builder.UseMiddleware<AuthorizationMiddleware>();

builder.Build().Run();
