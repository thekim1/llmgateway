extern alias Migration;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Migration::Ume.LlmGateway.MigrationService;

namespace Ume.LlmGateway.IntegrationTests;

public sealed class MigrationServiceTests
{
    [Fact]
    public void Key_ring_is_not_loaded_before_the_migration_creates_its_table()
    {
        var services = new ServiceCollection();
        services.AddDataProtection();

        // False means ASP.NET renamed or dropped its startup loader: check KeyRingStartup still applies.
        services.LoadKeyRingOnFirstUse().ShouldBeTrue();
        services.ShouldNotContain(d => d.ServiceType == typeof(IHostedService));
    }
}
