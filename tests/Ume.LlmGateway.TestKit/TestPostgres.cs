using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Ume.LlmGateway.Infrastructure.Persistence;

namespace Ume.LlmGateway.TestKit;

/// <summary>The Postgres container every test project runs against.</summary>
public static class TestPostgres
{
    /// <summary>The same major version as the deployment.</summary>
    public const string Image = "postgres:17-alpine";

    public static PostgreSqlBuilder Builder() => new(Image);

    /// <summary>A context with the connection's rights (the database owner for a fresh container), outside any host.</summary>
    public static GatewayDbContext Context(string connectionString) =>
        new(new DbContextOptionsBuilder<GatewayDbContext>().UseNpgsql(connectionString).Options);

    /// <summary>
    /// Applies the migrations before a host is started on the database. A host started first loads the Data Protection key
    /// ring (and other state) from tables that do not exist yet; a key ring load still failing on a background thread then
    /// fails the fixture's own first Protect call with 42P01.
    /// </summary>
    public static async Task MigrateAsync(string connectionString, CancellationToken ct = default)
    {
        await using var db = Context(connectionString);
        await db.Database.MigrateAsync(ct);
    }
}
