using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ume.LlmGateway.Infrastructure.Persistence;

/// <summary>Used only by <c>dotnet ef</c> at design time; no real database connection is made.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<GatewayDbContext>
{
    public GatewayDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql("Host=localhost;Database=gatewaydb;Username=design;Password=design")
            .Options;
        return new GatewayDbContext(options);
    }
}
