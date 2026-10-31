using Ume.LlmGateway.AdminApi.Budgets;
using Ume.LlmGateway.AdminApi.Catalog;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.AdminApi.DataAccess;
using Ume.LlmGateway.AdminApi.Keys;
using Ume.LlmGateway.AdminApi.Operations;
using Ume.LlmGateway.AdminApi.Organisation;
using Ume.LlmGateway.AdminApi.Reports;
using Ume.LlmGateway.AdminApi.RoutingRules;
using Ume.LlmGateway.AdminApi.Security;
using Ume.LlmGateway.AdminApi.Settings;

namespace Ume.LlmGateway.AdminApi;

public static class AdminEndpoints
{
    /// <summary>
    /// <c>/api</c>: every feature's endpoints, for signed-in users only, rate limited per user, with <see cref="AdminRequest"/>
    /// bodies validated before the handler runs. Each feature adds the role it needs.
    /// </summary>
    public static void MapAdminApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().RequireRateLimiting(AdminPolicies.PerUserRateLimit).AddEndpointFilter<AdminValidationFilter>()
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        api.MapOrganisation();
        api.MapKeys();
        api.MapCatalog();
        api.MapBudgets();
        api.MapSettings();
        api.MapRoutingRules();
        api.MapReports();
        api.MapOperations();
        api.MapDataAccess();
    }
}
