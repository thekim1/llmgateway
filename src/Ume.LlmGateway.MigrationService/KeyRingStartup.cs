namespace Ume.LlmGateway.MigrationService;

/// <summary>
/// The Data Protection key ring lives in the <c>DataProtectionKeys</c> table, which this service creates. ASP.NET
/// loads the key ring in a hosted service at startup, before <see cref="MigrationWorker"/> has migrated, so a fresh
/// database logged a 42P01 error for it. Without that hosted service the key ring loads on first use, after migrating.
/// </summary>
public static class KeyRingStartup
{
    internal const string HostedServiceTypeName = "Microsoft.AspNetCore.DataProtection.Internal.DataProtectionHostedService";

    /// <summary>Removes the startup key ring load; returns whether it was registered (a test pins that it is).</summary>
    public static bool LoadKeyRingOnFirstUse(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var loader = services.FirstOrDefault(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType?.FullName == HostedServiceTypeName);
        return loader is not null && services.Remove(loader);
    }
}
