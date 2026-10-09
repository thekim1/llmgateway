using System.Text;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Persistence;

namespace Ume.LlmGateway.Benchmarks;

/// <summary>
/// Prepares a standalone gateway database for the platform comparison (<c>benchmarks/compare</c>): applies the
/// migrations and seeds the same data as <see cref="GatewayHost"/>, with both providers pointing at one upstream URL.
/// Prints the virtual key so the load generator can use it.
/// </summary>
public static class CompareSeed
{
    public static async Task<int> RunAsync(string connectionString, string pepper, string upstreamBaseUrl)
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new GatewayDbContext(options);
        await db.Database.MigrateAsync();
        if (await db.VirtualKeys.AnyAsync())
        {
            await Console.Error.WriteLineAsync("Database already seeded; recreate it to get a new key.");
            return 1;
        }

        var hasher = new VirtualKeyHasher(Encoding.UTF8.GetBytes(pepper));
        // Credentials stay empty: the fake upstream needs none, so no Data Protection key ring is involved.
        Console.WriteLine(await GatewayHost.SeedAsync(db, hasher, _ => upstreamBaseUrl, "fake", encryptedCredential: null));
        return 0;
    }
}
