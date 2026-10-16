namespace Ume.LlmGateway.AdminApi;

public static class AdminServices
{
    /// <summary>The admin API's per-request services. Endpoints stay thin and call these; they never call each other.</summary>
    public static IServiceCollection AddAdminServices(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<AdminContext>();
        services.AddScoped<OwnerScopeResolver>();
        services.AddScoped<RoutingRuleService>();
        services.AddScoped<ProviderCatalogService>();
        services.AddScoped<ProviderStabilityService>();
        services.AddScoped<UsageMetricsService>();
        services.AddScoped<BudgetAdminService>();
        services.AddScoped<ConfigTransferService>();
        services.AddScoped<PiiIncidentService>();
        services.AddScoped<DataAccessService>();
        services.AddScoped<GatewaySettingsService>();
        return services;
    }
}
