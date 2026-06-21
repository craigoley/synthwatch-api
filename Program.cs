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

// Options bound from app settings (Postgres__*, Cors__*).
builder.Services.Configure<PostgresOptions>(builder.Configuration.GetSection("Postgres"));
builder.Services.Configure<CorsOptions>(builder.Configuration.GetSection("Cors"));

// Single managed-identity-authenticated Npgsql data source for the whole app.
builder.Services.AddSingleton(PostgresDataSourceFactory.Create);

// Read-mostly EF Core context over the runner-owned schema (no migrations).
builder.Services.AddDbContext<SynthWatchDbContext>((sp, options) =>
{
    var dataSource = sp.GetRequiredService<NpgsqlDataSource>();
    options.UseNpgsql(dataSource);
});

// Worker middleware: CORS (outer, decorates every response) then exception shielding (inner).
builder.UseMiddleware<CorsMiddleware>();
builder.UseMiddleware<ExceptionHandlingMiddleware>();

builder.Build().Run();
