using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.AdminApi.Catalog.Pricing;
using Ume.LlmGateway.AdminApi.Security;

namespace Ume.LlmGateway.AdminApi.Settings;

public static class SettingsEndpoints
{
    public static void MapSettings(this RouteGroupBuilder api)
    {
        var settings = api.MapGroup("/settings").RequireAuthorization(AdminPolicies.Admin).WithTags("Settings");
        settings.MapGet("/version", (ReleaseCheck releases, CancellationToken ct) => releases.GetAsync(ct))
            .WithName("GetVersion").WithSummary("The running version and the newest release on GitHub (checked at most every 6 hours)");
        settings.MapGet("/network", (GatewaySettingsService service, HttpContext http, CancellationToken ct) => service.GetNetworkAsync(http, ct))
            .WithName("GetNetworkSettings").WithSummary("Trusted reverse proxies, and the connection the admin API saw for this request");
        settings.MapPut("/network", (NetworkSettingsRequest input, GatewaySettingsService service, HttpContext http, CancellationToken ct) =>
            service.SetNetworkAsync(http, input, ct))
            .WithName("SetNetworkSettings").WithSummary("Replaces the trusted reverse proxies; the gateways apply them on their next request");
        settings.MapGet("/request-log", (GatewaySettingsService service, CancellationToken ct) => service.GetRequestLogAsync(ct))
            .WithName("GetRequestLogSettings").WithSummary("Whether the gateways log every request, and until when");
        settings.MapPut("/request-log", (RequestLogSettingsRequest input, GatewaySettingsService service, CancellationToken ct) =>
            service.SetRequestLogAsync(input, ct))
            .WithName("SetRequestLogSettings").WithSummary("Switches logging of every request on (for 24 hours or without a time limit) or off; the gateways apply it within seconds");
        settings.MapGet("/retention", (GatewaySettingsService service, CancellationToken ct) => service.GetRetentionAsync(ct))
            .WithName("GetRetentionSettings").WithSummary("How long usage and audit data are kept, and the nightly removal's state");
        settings.MapPut("/retention", (RetentionSettingsRequest input, GatewaySettingsService service, CancellationToken ct) =>
            service.SetRetentionAsync(input, ct))
            .WithName("SetRetentionSettings").WithSummary("Sets how long usage and audit data are kept; applied at the next run");
        settings.MapPost("/retention/run", async (GatewaySettingsService service, CancellationToken ct) =>
            TypedResults.Accepted("/api/settings/retention", await service.RunRetentionAsync(ct)))
            .WithName("RunRetention").WithSummary("Starts the removal of old data now (it runs in the background)");
        settings.MapGet("/model-prices", (ModelListSync sync) => TypedResults.Ok(sync.Status()))
            .WithName("GetModelPriceSync").WithSummary("Whether model prices follow the models.dev list, and this instance's last update");
        settings.MapPost("/model-prices/run", (ModelListSync sync) =>
            sync.TryTrigger()
                ? TypedResults.Accepted("/api/settings/model-prices", sync.Status())
                : throw new ApiFaultException(409, sync.Enabled ? "A price list check is already running." : "Price list updates are turned off (Admin:AutoUpdatePrices, Admin:PriceListUrl)."))
            .WithName("RunModelPriceSync").WithSummary("Downloads the models.dev list and updates prices and empty model details now (in the background)");
    }
}
