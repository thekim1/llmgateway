namespace Ume.LlmGateway.AdminApi;

public static class AdminServices
{
    /// <summary>The admin API's per-request services. Endpoints stay thin and call these; they never call each other.</summary>
    public static IServiceCollection AddAdminServices(this IServiceCollection services)
    {
        services.AddScoped<AdminContext>();
        services.AddScoped<OwnerScopeResolver>();
        services.AddScoped<RoutingRuleService>();
        services.AddScoped<ProviderCatalogService>();
        services.AddScoped<BudgetAdminService>();
        services.AddScoped<ConfigTransferService>();
        return services;
    }
}
