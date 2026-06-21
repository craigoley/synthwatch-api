using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Builds the singleton <see cref="NpgsqlDataSource"/> that authenticates to Azure Postgres
/// with a managed-identity Entra token used as the password. The token is fetched lazily via a
/// periodic password provider so it refreshes well before its ~1h lifetime expires.
/// </summary>
public static class PostgresDataSourceFactory
{
    // Entra token audience for Azure Database for PostgreSQL.
    private const string OssRdbmsScope = "https://ossrdbms-aad.database.windows.net/.default";

    public static NpgsqlDataSource Create(IServiceProvider sp)
    {
        var pg = sp.GetRequiredService<IOptions<PostgresOptions>>().Value;

        if (string.IsNullOrWhiteSpace(pg.Host) ||
            string.IsNullOrWhiteSpace(pg.Database) ||
            string.IsNullOrWhiteSpace(pg.Username))
        {
            throw new InvalidOperationException(
                "Postgres Host/Database/Username must be configured (app settings Postgres__Host, " +
                "Postgres__Database, Postgres__Username). No password is used — auth is managed identity.");
        }

        // Connection string carries host/db/username ONLY — no password. A password here would
        // cause Npgsql to skip the token provider and break managed-identity auth.
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = pg.Host,
            Port = pg.Port,
            Database = pg.Database,
            Username = pg.Username,
            SslMode = SslMode.Require,
            // Pooling defaults are fine for Flex Consumption; keep a modest ceiling.
            MaxPoolSize = 20,
        }.ConnectionString;

        var credential = new DefaultAzureCredential();
        var builder = new NpgsqlDataSourceBuilder(connectionString);

        builder.UsePeriodicPasswordProvider(
            async (_, ct) =>
            {
                var token = await credential
                    .GetTokenAsync(new TokenRequestContext(new[] { OssRdbmsScope }), ct)
                    .ConfigureAwait(false);
                return token.Token;
            },
            successRefreshInterval: TimeSpan.FromMinutes(50),
            failureRefreshInterval: TimeSpan.FromSeconds(5));

        return builder.Build();
    }
}
