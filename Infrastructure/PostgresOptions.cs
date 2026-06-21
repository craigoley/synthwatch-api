namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Connection coordinates for the Azure Postgres Flexible Server.
/// NOTE: there is deliberately NO password here. Auth is via managed identity —
/// if the connection string carried a password, the MI token would be ignored.
/// </summary>
public class PostgresOptions
{
    public string Host { get; set; } = "";
    public string Database { get; set; } = "";

    /// <summary>The Entra principal name of the Function App's managed identity (the DB role).</summary>
    public string Username { get; set; } = "";

    /// <summary>Postgres port. Flexible Server uses 5432.</summary>
    public int Port { get; set; } = 5432;
}
