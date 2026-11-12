using Ume.LlmGateway.AdminApi.Security;
using Ume.LlmGateway.Domain.Keys;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.AdminApi.Integrations;

/// <summary>
/// <c>/integration/v1</c> (docs/integrations.md): for an integration's own services, authenticated with one of its credential
/// keys that holds the scope; no session, no CSRF (read-only), its own per-key rate limit. Every read is logged as
/// security event 2006 <c>admin.data.read</c> with the actor <c>integration:&lt;name&gt;</c>.
/// </summary>
public static class IntegrationApiEndpoints
{
    public static void MapIntegrationApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var api = app.MapGroup("/integration/v1").RequireRateLimiting(AdminPolicies.PerIntegrationKeyRateLimit).WithTags("Integration API")
            .AddEndpointFilter(async (context, next) =>
            {
                var result = await next(context);
                var http = context.HttpContext;
                var logger = SecurityEvents.CreateLogger(http.RequestServices.GetRequiredService<ILoggerFactory>());
                SecurityEvents.AdminDataRead(logger, http.User.Identity?.Name ?? "integration", http.Request.Path.Value ?? "",
                    SecurityEvents.Printable(http.Request.QueryString.Value ?? ""), http.Items[AdminClientAddresses.ItemKey] as string);
                return result;
            })
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound);
        var usage = IntegrationKeyHandler.Policy(IntegrationScopes.UsageRead);

        api.MapGet("/usage", async (DateOnly from, DateOnly to, string? granularity, string? groupBy, Guid? keyId, string? runId, HttpContext http,
                IntegrationApiService service, CancellationToken ct) =>
            TypedResults.Ok(await service.UsageAsync(IntegrationKeyHandler.Caller(http.User).IntegrationId, from, to, granularity, groupBy, keyId, runId, ct)))
            .RequireAuthorization(usage).WithName("IntegrationUsage")
            .WithSummary("Requests, tokens and cost of the integration's keys per day or month, by key, model and step (scope usage.read)");
        api.MapGet("/budgets", async (Guid keyId, HttpContext http, IntegrationApiService service, CancellationToken ct) =>
            TypedResults.Ok(await service.BudgetsAsync(IntegrationKeyHandler.Caller(http.User).IntegrationId, keyId, ct)))
            .RequireAuthorization(usage).WithName("IntegrationBudgets")
            .WithSummary("The budgets that apply to one of the integration's keys, with what each used this period (scope usage.read)");
    }
}
