using Microsoft.Extensions.Options;
using Ume.LlmGateway.Domain.ContentGuard;
using Ume.LlmGateway.Gateway.Authentication;
using Ume.LlmGateway.Gateway.Budgets;
using Ume.LlmGateway.Gateway.Requests;
using Ume.LlmGateway.Gateway.Routing;
using Ume.LlmGateway.Gateway.Usage;

namespace Ume.LlmGateway.Gateway;

public static class GatewayServices
{
    /// <summary>
    /// The gateway's options and services, by feature. All are singletons: the request path holds no per-request scope.
    /// Hosted services are registered in the order they start; they stop in reverse.
    /// </summary>
    public static IServiceCollection AddGatewayServices(this IServiceCollection services)
    {
        services.AddOptions<GatewayOptions>()
            .BindConfiguration(GatewayOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<KeyAuthenticator>();
        services.AddSingleton<AuthFailureThrottle>();

        services.AddSingleton<GatewayCatalog>();
        services.AddSingleton<DeploymentLoad>();
        services.AddSingleton<IRouteResolver, RouteResolver>();

        services.AddSingleton(sp =>
        {
            var guard = sp.GetRequiredService<IOptions<GatewayOptions>>().Value.ContentGuard;
            return new ContentGuardRules(guard.PromptInjectionRules, guard.BlockedPatterns);
        });
        services.AddSingleton<BudgetService>();
        services.AddSingleton<GatewayMetrics>();

        // Registered before the usage writer so it stops after it: alerts raised while the writer drains are still sent.
        services.AddSingleton<AlertNotifier>();
        services.AddHostedService(sp => sp.GetRequiredService<AlertNotifier>());
        services.AddSingleton<UsageWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<UsageWriter>());
        services.AddSingleton<AuthFailureRecorder>();
        services.AddHostedService(sp => sp.GetRequiredService<AuthFailureRecorder>());

        services.AddSingleton<GatewayRequestHandler>();
        // One short attempt per alert: the standard resilience handler's retries would POST the same alert several times.
        services.AddHttpClient(AlertNotifier.HttpClientName, c => c.Timeout = AlertNotifier.SendTimeout).RemoveStandardResilienceHandler();
        return services;
    }
}
