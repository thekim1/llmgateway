using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi;

public sealed record NetworkSettingsRequest(
    [property: Required, MaxLength(NetworkRanges.MaxEntries)] List<string> TrustedProxies) : AdminRequest, IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (TrustedProxies is null)
        {
            yield break;
        }

        if (NetworkRanges.Normalize(TrustedProxies, out var canonical) is { } error)
        {
            yield return new ValidationResult(error, [nameof(TrustedProxies)]);
        }
        else if (canonical.Any(e => NetworkRanges.TryParse(e, out var n, out _) && NetworkRanges.IsEverything(n)))
        {
            // Every caller could then claim any address in X-Forwarded-For and walk past key address restrictions.
            yield return new ValidationResult("A network that covers every address (/0) cannot be a trusted proxy. Enter the proxy's own addresses.", [nameof(TrustedProxies)]);
        }
    }
}

public sealed record RetentionSettingsRequest(
    [property: Range(RetentionPolicy.MinimumDays, RetentionPolicy.MaximumDays)] int UsageRetentionDays,
    [property: Range(RetentionPolicy.MinimumDays, RetentionPolicy.MaximumDays)] int? AuditRetentionDays) : AdminRequest;

/// <summary>
/// Retention settings and job state. <c>UsageRetainedFrom</c>: the oldest instant usage records cover (before it only monthly
/// totals exist; null until the first removal). <c>NextUsageCutoff</c>: usage, refused-credential and alert rows before it go
/// at the next run with today's settings. <c>NextAuditCutoff</c>: the same for audit entries; null when they are kept for ever.
/// </summary>
public sealed record RetentionSettingsDto(
    int UsageRetentionDays, int? AuditRetentionDays, DateTimeOffset? UsageRetainedFrom, DateTimeOffset NextUsageCutoff, DateTimeOffset? NextAuditCutoff,
    bool Enabled, DateTimeOffset? NextRunAt, bool Running, DateTimeOffset? LastRunAt, long? LastRunDeleted, string? LastRunError);

/// <summary>
/// The connection the admin API saw for this request, to help find the proxy's address: behind the same proxy as the
/// gateway, <paramref name="RemoteAddress"/> is the proxy and <paramref name="ForwardedFor"/> what it added.
/// </summary>
public sealed record ObservedConnectionDto(string? RemoteAddress, string? ForwardedFor);

public sealed record NetworkSettingsDto(IReadOnlyList<string> TrustedProxies, DateTimeOffset? UpdatedAt, ObservedConnectionDto Observed);

/// <summary>
/// Turns logging of every request on or off. <c>NoTimeLimit</c>: when switched on, it stays on until it is turned off;
/// otherwise it switches itself off after <see cref="GatewaySettingsService.RequestLogDuration"/>. Ignored when switching off.
/// </summary>
public sealed record RequestLogSettingsRequest(bool LogAllRequests, bool NoTimeLimit) : AdminRequest;

/// <summary>
/// Whether the gateways log every request. <c>Active</c>: logging is on right now (switched on and not expired).
/// <c>Until</c>: when it switches itself off; null with <c>Active</c> = no time limit.
/// </summary>
public sealed record RequestLogSettingsDto(bool Active, DateTimeOffset? Until, DateTimeOffset? UpdatedAt);

/// <summary>Gateway-wide settings (the single <see cref="GatewaySettings"/> row). Gateway admins only.</summary>
public sealed class GatewaySettingsService(AdminContext ctx, RetentionService retention, AdminClientAddresses addresses)
{
    /// <summary>How long logging every request stays on when switched on with a time limit.</summary>
    public static readonly TimeSpan RequestLogDuration = TimeSpan.FromHours(24);

    public async Task<RequestLogSettingsDto> GetRequestLogAsync(CancellationToken ct) =>
        RequestLogDto(await ctx.Db.Settings.AsNoTracking().SingleOrDefaultAsync(ct) ?? new GatewaySettings());

    public async Task<RequestLogSettingsDto> SetRequestLogAsync(RequestLogSettingsRequest input, CancellationToken ct)
    {
        var settings = await ctx.Db.Settings.SingleOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new GatewaySettings();
            ctx.Db.Settings.Add(settings);
        }

        var before = RequestLogSettingsAudit.Of(settings);
        settings.LogAllRequests = input.LogAllRequests;
        settings.LogAllRequestsUntil = input.LogAllRequests && !input.NoTimeLimit ? ctx.Now + RequestLogDuration : null;
        settings.UpdatedAt = ctx.Now;
        try
        {
            // Config: the gateways read the switch with the catalogue.
            await ctx.SaveAsync(AuditActions.Update, AuditEntities.Settings, GatewaySettings.SingletonId, before, RequestLogSettingsAudit.Of(settings), InvalidationKind.Config, ct);
        }
        catch (DbUpdateException)
        {
            throw new ApiFaultException(409, "Someone else changed the settings at the same time. Reload the page and try again.");
        }

        return RequestLogDto(settings);
    }

    private RequestLogSettingsDto RequestLogDto(GatewaySettings s)
    {
        var active = s.LogsAllRequestsAt(ctx.Now);
        return new RequestLogSettingsDto(active, active ? s.LogAllRequestsUntil : null, s.UpdatedAt);
    }

    public async Task<RetentionSettingsDto> GetRetentionAsync(CancellationToken ct) =>
        RetentionDto(await ctx.Db.Settings.AsNoTracking().SingleOrDefaultAsync(ct) ?? new GatewaySettings());

    public async Task<RetentionSettingsDto> SetRetentionAsync(RetentionSettingsRequest input, CancellationToken ct)
    {
        var settings = await ctx.Db.Settings.SingleOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new GatewaySettings();
            ctx.Db.Settings.Add(settings);
        }

        var before = RetentionSettingsAudit.Of(settings);
        settings.UsageRetentionDays = input.UsageRetentionDays;
        settings.AuditRetentionDays = input.AuditRetentionDays;
        settings.UpdatedAt = ctx.Now;
        try
        {
            // No gateway cache holds these; budget spend reads UsageRetainedFrom, which only the retention job moves.
            await ctx.SaveAsync(AuditActions.Update, AuditEntities.Settings, GatewaySettings.SingletonId, before, RetentionSettingsAudit.Of(settings), [], ct);
        }
        catch (DbUpdateException)
        {
            throw new ApiFaultException(409, "Someone else changed the settings at the same time. Reload the page and try again.");
        }

        return RetentionDto(settings);
    }

    public async Task<RetentionSettingsDto> RunRetentionAsync(CancellationToken ct)
    {
        if (!retention.TryTrigger())
        {
            throw new ApiFaultException(409, "The clean-up is already running.");
        }

        await ctx.AuditAsync(AuditActions.Run, AuditEntities.Settings, GatewaySettings.SingletonId, new { Retention = "run" }, ct);
        return await GetRetentionAsync(ct);
    }

    private RetentionSettingsDto RetentionDto(GatewaySettings s)
    {
        var now = ctx.Now;
        var cutoff = RetentionPolicy.UsageCutoff(now, s.UsageRetentionDays);
        return new RetentionSettingsDto(s.UsageRetentionDays, s.AuditRetentionDays, s.UsageRetainedFrom,
            s.UsageRetainedFrom is { } kept && kept > cutoff ? kept : cutoff,
            s.AuditRetentionDays is { } days ? now.AddDays(-days) : null,
            retention.Enabled, retention.NextRunAt(now), retention.IsRunning, s.RetentionLastRunAt, s.RetentionLastRunDeleted, s.RetentionLastRunError);
    }

    public async Task<NetworkSettingsDto> GetNetworkAsync(HttpContext http, CancellationToken ct)
    {
        var settings = await ctx.Db.Settings.AsNoTracking().SingleOrDefaultAsync(ct) ?? new GatewaySettings();
        return Dto(settings, http);
    }

    public async Task<NetworkSettingsDto> SetNetworkAsync(HttpContext http, NetworkSettingsRequest input, CancellationToken ct)
    {
        NetworkRanges.Normalize(input.TrustedProxies, out var proxies);
        var settings = await ctx.Db.Settings.SingleOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new GatewaySettings();
            ctx.Db.Settings.Add(settings);
        }

        var before = NetworkSettingsAudit.Of(settings);
        settings.TrustedProxies = proxies;
        settings.UpdatedAt = ctx.Now;
        try
        {
            // Config: the gateways read trusted proxies with the catalogue.
            await ctx.SaveAsync(AuditActions.Update, AuditEntities.Settings, GatewaySettings.SingletonId, before, NetworkSettingsAudit.Of(settings), InvalidationKind.Config, ct);
            addresses.Invalidate();
        }
        catch (DbUpdateException)
        {
            // Two admins saving the very first settings at once: one insert wins.
            throw new ApiFaultException(409, "Someone else changed the settings at the same time. Reload the page and try again.");
        }

        return Dto(settings, http);
    }

    private static NetworkSettingsDto Dto(GatewaySettings settings, HttpContext http)
    {
        var forwarded = http.Request.Headers["X-Forwarded-For"].ToString();
        return new NetworkSettingsDto(settings.TrustedProxies, settings.UpdatedAt,
            new ObservedConnectionDto(http.Connection.RemoteIpAddress is { } remote ? NetworkRanges.Normalize(remote).ToString() : null,
                string.IsNullOrWhiteSpace(forwarded) ? null : forwarded[..Math.Min(forwarded.Length, 500)]));
    }
}

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
