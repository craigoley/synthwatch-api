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

// Channel test-send transport (POST /api/channels/{id}/test): ACS email + webhook POST, reading the
// same ACS env the runner uses. Email is inert until ACS_EMAIL_CONNECTION_STRING / ALERT_EMAIL_FROM are
// set on the API Function App (a Craig step — see the PR); the webhook path needs no env.
builder.Services.AddHttpClient();
builder.Services.AddSingleton<SynthWatch.Api.Infrastructure.IChannelTestTransport,
    SynthWatch.Api.Infrastructure.AcsChannelTestTransport>();

// Read-mostly EF Core context over the runner-owned schema (no migrations).
builder.Services.AddDbContext<SynthWatchDbContext>((sp, options) =>
{
    var dataSource = sp.GetRequiredService<NpgsqlDataSource>();
    options.UseNpgsql(dataSource);
});

// Worker middleware (outermost first): request logging (times whole pipeline + final status),
// then exception shielding (innermost). CORS is platform-level (see above).
builder.UseMiddleware<RequestLoggingMiddleware>();
builder.UseMiddleware<ExceptionHandlingMiddleware>();

builder.Build().Run();
