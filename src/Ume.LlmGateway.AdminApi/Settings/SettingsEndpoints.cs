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
        settings.MapPut("/branding", (BrandingRequest input, BrandingService service, CancellationToken ct) => service.SetNameAsync(input, ct))
            .WithName("SetBrandingName").WithSummary("Sets the organisation name shown in the admin console's menu and on its sign-in page");
        settings.MapPut("/branding/logo", (LogoRequest input, BrandingService service, CancellationToken ct) => service.SetLogoAsync(input, ct))
            .WithName("SetBrandingLogo").WithSummary("Replaces the admin console's logo (PNG, JPEG, WebP or SVG, base64, at most 256 KB)");
        settings.MapDelete("/branding/logo", (BrandingService service, CancellationToken ct) => service.RemoveLogoAsync(ct))
            .WithName("RemoveBrandingLogo").WithSummary("Removes the logo; the admin console shows its built-in icon again");
    }

    /// <summary>
    /// The branding for every visitor, signed in or not (the sign-in page shows it): name and logo URL, and the logo itself.
    /// Answered from <see cref="BrandingCache"/>, so these anonymous requests reach the database at most twice a minute.
    /// </summary>
    public static void MapBranding(this WebApplication app)
    {
        app.MapGet("/bff/branding", async (BrandingCache cache, CancellationToken ct) => TypedResults.Ok((await cache.GetAsync(ct)).Dto))
            .WithTags("Settings").WithName("GetBranding").WithSummary("The organisation name and logo URL the admin console shows");
        app.MapGet(BrandingCache.Snapshot.LogoPath, async (string? v, BrandingCache cache, HttpContext http, CancellationToken ct) =>
        {
            var branding = await cache.GetAsync(ct);
            if (branding.Logo is not { } logo)
            {
                return Results.NotFound();
            }

            // The URL names the logo's hash, so a matching request can be cached for good; any other gets the current logo uncached.
            http.Response.Headers.CacheControl = v == branding.Version ? "public, max-age=31536000, immutable" : "no-cache";
            // Opened directly, an SVG would be a document on this origin: no scripts, no requests, sandboxed.
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; img-src data:; style-src 'unsafe-inline'; sandbox";
            return Results.Bytes(logo, branding.LogoContentType);
        }).WithTags("Settings").WithName("GetBrandingLogo").WithSummary("The admin console's logo");
    }
}
