using Microsoft.Extensions.Options;
using Ume.LlmGateway.AdminApi.Catalog;
using Ume.LlmGateway.AdminApi.Operations;
using Ume.LlmGateway.AdminApi.Security;
using Ume.LlmGateway.Domain;

namespace Ume.LlmGateway.AdminApi.Reports;

/// <summary>Usage reports, the audit log and the catalogue for key holders (see <see cref="ReportService"/>).</summary>
public static class ReportEndpoints
{
    public static void MapReports(this RouteGroupBuilder api)
    {
        var reports = api.MapGroup("").RequireAuthorization(AdminPolicies.Read).WithTags("Reports");
        reports.MapGet("/usage/requests", async (DateTimeOffset? from, DateTimeOffset? to, Guid? keyId, Guid? teamId, Guid? departmentId, string? model, string? provider, RequestOutcome? outcome, bool? errors, int? page, int? pageSize, ReportService service, CancellationToken ct) =>
            TypedResults.Ok(await service.RequestsAsync(new UsageRequestQuery(from, to, keyId, teamId, departmentId, model, provider, outcome, errors, page, pageSize), ct)))
            .WithName("ListUsageRequests").WithSummary("Requests in the user's departments, newest first; errors=true keeps only those that did not succeed");
        reports.MapGet("/usage/requests/{requestId}", async (string requestId, ReportService service, CancellationToken ct) =>
            TypedResults.Ok(await service.RequestAsync(requestId, ct)))
            .WithName("GetUsageRequest");
        reports.MapGet("/usage/summary", async (DateTimeOffset? from, DateTimeOffset? to, string? groupBy, Guid? departmentId, Guid? teamId, ReportService service, CancellationToken ct) =>
            TypedResults.Ok(await service.SummaryAsync(from, to, groupBy, departmentId, teamId, ct)))
            .WithName("GetUsageSummary").WithSummary("Totals grouped by department, team, key, model, provider or day");
        reports.MapGet("/usage/metrics", async (DateTimeOffset? from, DateTimeOffset? to, string? bucket, Guid? departmentId, Guid? teamId, Guid? keyId, string? model, string? provider, RequestOutcome? outcome, GatewayEndpoint? endpoint, UsageMetricsService metrics, CancellationToken ct) =>
            TypedResults.Ok(await metrics.GetAsync(new UsageMetricsQuery(from, to, bucket, departmentId, teamId, keyId, model, provider, outcome, endpoint), ct)))
            .WithName("GetUsageMetrics").WithSummary("Dashboard figures per hour or day: requests by outcome, cost in USD, tokens, cache hits, latency, gateway overhead, time to first token and throughput");
        reports.MapGet("/usage/pii-incidents", async (DateTimeOffset? from, DateTimeOffset? to, Guid? keyId, Guid? teamId, Guid? departmentId, string? category, string? action, int? page, int? pageSize, PiiIncidentService pii, CancellationToken ct) =>
            TypedResults.Ok(await pii.ListAsync(new PiiIncidentQuery(from, to, keyId, teamId, departmentId, category, action, page, pageSize), ct)))
            .WithName("ListPiiIncidents").WithSummary("Requests in which PII was detected, newest first, with counts per category and action");
        reports.MapGet("/usage/export.csv", async (DateTimeOffset? from, DateTimeOffset? to, ReportService service, CancellationToken ct) =>
            TypedResults.File(await service.ExportCsvAsync(from, to, ct), "text/csv; charset=utf-8", "anvandning.csv"))
            .WithName("ExportUsageCsv").WithSummary("Cost per cost centre, department, team and key (semicolon-separated, Swedish decimals)");
        api.MapGet("/audit", async (int? page, int? pageSize, string? entityType, ReportService service, CancellationToken ct) =>
            TypedResults.Ok(await service.AuditAsync(page, pageSize, entityType, ct)))
            .RequireAuthorization(AdminPolicies.Admin).WithTags("Reports").WithName("ListAudit");
        api.MapGet("/catalog", async (ProviderCatalogService catalog, IOptions<GatewayLinkOptions> gateway, CancellationToken ct) =>
            TypedResults.Ok(await catalog.PublishedCatalogAsync(gateway.Value.PublicBaseUrl, ct)))
            .WithTags("Reports").WithName("GetCatalog").WithSummary("Enabled models and routes with prices in USD, for key holders");
    }
}
