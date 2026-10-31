using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.AdminApi.Auditing;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.AdminApi.Security;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Networking;
using Ume.LlmGateway.Domain.Usage;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi.Settings;

/// <summary>Gateway-wide settings (the single <see cref="GatewaySettings"/> row). Gateway admins only.</summary>
public sealed class GatewaySettingsService(AdminContext ctx, RetentionService retention, AdminClientAddresses addresses)
{
    /// <summary>How long logging every request stays on when switched on with a time limit.</summary>
    public static readonly TimeSpan RequestLogDuration = TimeSpan.FromHours(24);

    public async Task<RequestLogSettingsDto> GetRequestLogAsync(CancellationToken ct) => RequestLogDto(await ReadAsync(ct));

    public async Task<RequestLogSettingsDto> SetRequestLogAsync(RequestLogSettingsRequest input, CancellationToken ct)
    {
        var settings = await ChangeAsync(RequestLogSettingsAudit.Of, s =>
        {
            s.LogAllRequests = input.LogAllRequests;
            s.LogAllRequestsUntil = input.LogAllRequests && !input.NoTimeLimit ? ctx.Now + RequestLogDuration : null;
        }, [InvalidationKind.Config], ct); // the gateways read the switch with the catalogue
        return RequestLogDto(settings);
    }

    private RequestLogSettingsDto RequestLogDto(GatewaySettings s)
    {
        var active = s.LogsAllRequestsAt(ctx.Now);
        return new RequestLogSettingsDto(active, active ? s.LogAllRequestsUntil : null, s.UpdatedAt);
    }

    public async Task<RetentionSettingsDto> GetRetentionAsync(CancellationToken ct) => RetentionDto(await ReadAsync(ct));

    public async Task<RetentionSettingsDto> SetRetentionAsync(RetentionSettingsRequest input, CancellationToken ct)
    {
        var settings = await ChangeAsync(RetentionSettingsAudit.Of, s =>
        {
            s.UsageRetentionDays = input.UsageRetentionDays;
            s.AuditRetentionDays = input.AuditRetentionDays;
        }, [], ct); // no gateway cache holds these; budget spend reads UsageRetainedFrom, which only the retention job moves
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

    public async Task<NetworkSettingsDto> GetNetworkAsync(HttpContext http, CancellationToken ct) => NetworkDto(await ReadAsync(ct), http);

    public async Task<NetworkSettingsDto> SetNetworkAsync(HttpContext http, NetworkSettingsRequest input, CancellationToken ct)
    {
        NetworkRanges.Normalize(input.TrustedProxies, out var proxies);
        var settings = await ChangeAsync(NetworkSettingsAudit.Of, s => s.TrustedProxies = proxies, [InvalidationKind.Config], ct); // the gateways read trusted proxies with the catalogue
        addresses.Invalidate();
        return NetworkDto(settings, http);
    }

    private static NetworkSettingsDto NetworkDto(GatewaySettings settings, HttpContext http)
    {
        var forwarded = http.Request.Headers["X-Forwarded-For"].ToString();
        return new NetworkSettingsDto(settings.TrustedProxies, settings.UpdatedAt,
            new ObservedConnectionDto(http.Connection.RemoteIpAddress is { } remote ? NetworkRanges.Normalize(remote).ToString() : null,
                string.IsNullOrWhiteSpace(forwarded) ? null : forwarded[..Math.Min(forwarded.Length, 500)]));
    }

    /// <summary>The settings row, or the defaults while none has been saved.</summary>
    private async Task<GatewaySettings> ReadAsync(CancellationToken ct) =>
        await ctx.Db.Settings.AsNoTracking().SingleOrDefaultAsync(ct) ?? new GatewaySettings();

    /// <summary>
    /// Applies <paramref name="change"/> to the settings row (created on the first change) and saves it with an audit entry
    /// of the <paramref name="snapshot"/> before and after.
    /// </summary>
    private async Task<GatewaySettings> ChangeAsync<TAudit>(Func<GatewaySettings, TAudit> snapshot, Action<GatewaySettings> change,
        IReadOnlyCollection<InvalidationKind> kinds, CancellationToken ct)
    {
        var settings = await ctx.Db.Settings.SingleOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new GatewaySettings();
            ctx.Db.Settings.Add(settings);
        }

        var before = snapshot(settings);
        change(settings);
        settings.UpdatedAt = ctx.Now;
        try
        {
            await ctx.SaveAsync(AuditActions.Update, AuditEntities.Settings, GatewaySettings.SingletonId, before, snapshot(settings), kinds, ct);
        }
        catch (DbUpdateException)
        {
            // Two admins saving the very first settings at once: one insert wins.
            throw new ApiFaultException(409, "Someone else changed the settings at the same time. Reload the page and try again.");
        }

        return settings;
    }
}
