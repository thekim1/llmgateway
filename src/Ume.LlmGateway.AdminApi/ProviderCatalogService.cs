using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Security;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi;

/// <summary>Providers, model deployments, their prices, route aliases and the exchange rate.</summary>
public sealed class ProviderCatalogService(AdminContext ctx, RoutingRuleService rules, CredentialProtector protector)
{
    public static ProviderCapabilities[] Capabilities(ProviderCapabilities value) =>
        [.. Enum.GetValues<ProviderCapabilities>().Where(c => c != ProviderCapabilities.None && value.HasFlag(c))];

    // ---- providers ---------------------------------------------------------------------------------------

    public async Task<List<ProviderDto>> ProvidersAsync(CancellationToken ct) =>
        [.. (await ctx.Db.ProviderAccounts.Include(p => p.Deployments).OrderBy(p => p.Name).ToListAsync(ct)).Select(ProviderDto.Of)];

    public async Task<ProviderAccount> ProviderAsync(Guid id, CancellationToken ct) =>
        await ctx.Db.ProviderAccounts.Include(p => p.Deployments).SingleOrDefaultAsync(p => p.Id == id, ct)
        ?? throw new ApiFaultException(404, "Leverantören finns inte.");

    public async Task<ProviderDto> CreateProviderAsync(ClaimsPrincipal user, ProviderRequest input, CancellationToken ct)
    {
        var p = new ProviderAccount { Name = input.Name, BaseUrl = input.BaseUrl, CreatedAt = ctx.Now };
        Apply(p, input);
        ctx.Db.ProviderAccounts.Add(p);
        await ctx.SaveAsync(user, AuditActions.Create, AuditEntities.Provider, p.Id, null, ProviderAudit.Of(p), InvalidationKind.Config, ct);
        return ProviderDto.Of(p);
    }

    public async Task<ProviderDto> UpdateProviderAsync(ClaimsPrincipal user, Guid id, ProviderRequest input, CancellationToken ct)
    {
        var p = await ProviderAsync(id, ct);
        var before = ProviderAudit.Of(p);
        Apply(p, input);
        await ctx.SaveAsync(user, AuditActions.Update, AuditEntities.Provider, id, before, ProviderAudit.Of(p), InvalidationKind.Config, ct);
        return ProviderDto.Of(p);
    }

    public async Task<ProviderDto> DrainAsync(ClaimsPrincipal user, Guid id, bool drained, CancellationToken ct)
    {
        var p = await ProviderAsync(id, ct);
        var before = ProviderAudit.Of(p);
        p.IsDrained = drained;
        await ctx.SaveAsync(user, AuditActions.Drain, AuditEntities.Provider, id, before, ProviderAudit.Of(p), InvalidationKind.Config, ct);
        return ProviderDto.Of(p);
    }

    public async Task DeleteProviderAsync(ClaimsPrincipal user, Guid id, CancellationToken ct)
    {
        var p = await ProviderAsync(id, ct);
        if (p.Deployments.Count > 0)
        {
            throw new ApiFaultException(409, "Leverantören har modeller och kan inte tas bort.");
        }

        ctx.Db.ProviderAccounts.Remove(p);
        await ctx.SaveAsync(user, AuditActions.Delete, AuditEntities.Provider, id, ProviderAudit.Of(p), null, InvalidationKind.Config, ct);
    }

    /// <summary>Copies the request onto the provider. A <c>null</c> credential keeps the stored one; an empty one removes it.</summary>
    public void Apply(ProviderAccount p, ProviderRequest input)
    {
        p.Name = input.Name; p.DisplayName = input.DisplayName; p.BaseUrl = input.BaseUrl.TrimEnd('/'); p.Type = input.Type; p.AuthMode = input.AuthMode;
        p.Residency = input.Residency; p.Capabilities = input.Capabilities.Aggregate(ProviderCapabilities.None, (a, b) => a | b); p.TimeoutSeconds = input.TimeoutSeconds; p.IsEnabled = input.IsEnabled;
        if (input.Credential is not null) { p.EncryptedCredential = input.Credential.Length == 0 ? null : protector.Protect(input.Credential); }
    }

    // ---- models and prices -------------------------------------------------------------------------------

    public async Task<List<ModelDto>> ModelsAsync(CancellationToken ct) =>
        [.. (await ctx.Db.ModelDeployments.Include(m => m.ProviderAccount).Include(m => m.Prices).OrderBy(m => m.Name).ToListAsync(ct)).Select(m => ModelDto.Of(m, ctx.Now))];

    public async Task<ModelDeployment> ModelAsync(Guid id, CancellationToken ct) =>
        await ctx.Db.ModelDeployments.Include(m => m.ProviderAccount).Include(m => m.Prices).SingleOrDefaultAsync(m => m.Id == id, ct)
        ?? throw new ApiFaultException(404, "Modellen finns inte.");

    public async Task<ModelDto> CreateModelAsync(ClaimsPrincipal user, ModelRequest input, CancellationToken ct)
    {
        var provider = await ProviderAsync(input.ProviderId, ct);
        var m = new ModelDeployment { Name = input.Name.Trim(), UpstreamModel = input.UpstreamModel, ProviderAccount = provider, ProviderAccountId = provider.Id };
        Apply(m, input);
        if (input.Price is { } price) { m.Prices.Add(NewPrice(price)); }
        ctx.Db.ModelDeployments.Add(m);
        await ctx.SaveAsync(user, AuditActions.Create, AuditEntities.Model, m.Id, null, ModelAudit.Of(m), InvalidationKind.Config, ct);
        return ModelDto.Of(m, ctx.Now);
    }

    public async Task<ModelDto> UpdateModelAsync(ClaimsPrincipal user, Guid id, ModelRequest input, CancellationToken ct)
    {
        var m = await ModelAsync(id, ct);
        await EnsureKindCanChangeAsync(m, input.Kind, ct);
        if (!string.Equals(m.Name, input.Name.Trim(), StringComparison.OrdinalIgnoreCase)) { await rules.EnsureNotUsedByRulesAsync(m.Name, "byta namn", ct); }
        var before = ModelAudit.Of(m);
        Apply(m, input);
        await ctx.SaveAsync(user, AuditActions.Update, AuditEntities.Model, id, before, ModelAudit.Of(m), InvalidationKind.Config, ct);
        return ModelDto.Of(m, ctx.Now);
    }

    public async Task DeleteModelAsync(ClaimsPrincipal user, Guid id, CancellationToken ct)
    {
        var m = await ModelAsync(id, ct);
        if (await ctx.Db.RouteTargets.AnyAsync(t => t.ModelDeploymentId == id, ct)) { throw new ApiFaultException(409, "Modellen används i en rutt."); }
        await rules.EnsureNotUsedByRulesAsync(m.Name, "tas bort", ct);
        ctx.Db.ModelDeployments.Remove(m);
        await ctx.SaveAsync(user, AuditActions.Delete, AuditEntities.Model, id, ModelAudit.Of(m), null, InvalidationKind.Config, ct);
    }

    /// <summary>A route only holds models of its own kind, so a model in a route keeps its kind.</summary>
    public async Task EnsureKindCanChangeAsync(ModelDeployment m, ModelKind kind, CancellationToken ct)
    {
        if (m.Kind != kind && await ctx.Db.RouteTargets.AnyAsync(t => t.ModelDeploymentId == m.Id, ct))
        {
            throw new ApiFaultException(409, "Modelltypen kan inte ändras när modellen används i en rutt.");
        }
    }

    public void Apply(ModelDeployment m, ModelRequest input)
    {
        m.Name = input.Name.Trim(); m.UpstreamModel = input.UpstreamModel.Trim(); m.Kind = input.Kind; m.ParameterProfile = input.ParameterProfile;
        m.ContextWindow = input.ContextWindow; m.IsEnabled = input.IsEnabled;
        if (input.Features is not null)
        {
            m.Features = [.. input.Features.Select(f => f.Trim().ToLowerInvariant()).Where(f => f.Length is > 0 and <= 40).Distinct().Take(20).Order()];
        }
    }

    public async Task<List<PriceDto>> PricesAsync(Guid id, CancellationToken ct) =>
        [.. (await ModelAsync(id, ct)).Prices.OrderByDescending(p => p.EffectiveFrom).Select(PriceDto.Of)];

    public async Task<PriceDto> AddPriceAsync(ClaimsPrincipal user, Guid id, PriceRequest input, CancellationToken ct)
    {
        var m = await ModelAsync(id, ct);
        var price = NewPrice(input);
        price.ModelDeploymentId = m.Id;
        m.Prices.Add(price);
        ctx.Db.ModelPrices.Add(price);
        await ctx.SaveAsync(user, AuditActions.Price, AuditEntities.Model, id, null, PriceDto.Of(price), InvalidationKind.Config, ct);
        return PriceDto.Of(price);
    }

    /// <summary>A price from the request, effective now unless the request says otherwise.</summary>
    public ModelPrice NewPrice(PriceRequest input) => new()
    {
        InputPerMillionUsd = input.InputPerMillionUsd, CachedInputPerMillionUsd = input.CachedInputPerMillionUsd,
        OutputPerMillionUsd = input.OutputPerMillionUsd, AudioPerMinuteUsd = input.AudioPerMinuteUsd,
        AudioInputPerMillionUsd = input.AudioInputPerMillionUsd, AudioOutputPerMillionUsd = input.AudioOutputPerMillionUsd,
        EffectiveFrom = (input.EffectiveFrom ?? ctx.Now).ToUniversalTime(),
    };

    // ---- routes ------------------------------------------------------------------------------------------

    /// <summary>Route aliases with their targets' models and providers (the navigations <see cref="RouteDto"/> shows).</summary>
    public IQueryable<RouteAlias> Routes() =>
        ctx.Db.RouteAliases.Include(r => r.Targets).ThenInclude(t => t.ModelDeployment!).ThenInclude(m => m.ProviderAccount);

    public async Task<List<RouteDto>> RoutesAsync(CancellationToken ct) =>
        [.. (await Routes().OrderBy(r => r.Name).ToListAsync(ct)).Select(RouteDto.Of)];

    private async Task<RouteAlias> RouteAsync(Guid id, CancellationToken ct) =>
        await Routes().SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw new ApiFaultException(404, "Rutten finns inte.");

    public async Task<RouteDto> CreateRouteAsync(ClaimsPrincipal user, RouteRequest input, CancellationToken ct)
    {
        var r = new RouteAlias { Name = input.Name.Trim() };
        await ApplyRouteAsync(r, input, ct);
        ctx.Db.RouteAliases.Add(r);
        await ctx.SaveAsync(user, AuditActions.Create, AuditEntities.Route, r.Id, null, RouteAudit.Of(r), InvalidationKind.Config, ct);
        return RouteDto.Of(r);
    }

    public async Task<RouteDto> UpdateRouteAsync(ClaimsPrincipal user, Guid id, RouteRequest input, CancellationToken ct)
    {
        var r = await RouteAsync(id, ct);
        if (!string.Equals(r.Name, input.Name.Trim(), StringComparison.OrdinalIgnoreCase)) { await rules.EnsureNotUsedByRulesAsync(r.Name, "byta namn", ct); }
        var before = RouteAudit.Of(r);
        await ApplyRouteAsync(r, input, ct);
        await ctx.SaveAsync(user, AuditActions.Update, AuditEntities.Route, id, before, RouteAudit.Of(r), InvalidationKind.Config, ct);
        return RouteDto.Of(r);
    }

    public async Task DeleteRouteAsync(ClaimsPrincipal user, Guid id, CancellationToken ct)
    {
        var r = await RouteAsync(id, ct);
        await rules.EnsureNotUsedByRulesAsync(r.Name, "tas bort", ct);
        ctx.Db.RouteAliases.Remove(r);
        await ctx.SaveAsync(user, AuditActions.Delete, AuditEntities.Route, id, RouteAudit.Of(r), null, InvalidationKind.Config, ct);
    }

    /// <summary>Copies the request onto the route and replaces its targets. The targets' models must exist and be of the route's kind.</summary>
    public async Task ApplyRouteAsync(RouteAlias r, RouteRequest input, CancellationToken ct)
    {
        var ids = input.Targets.Select(t => t.ModelId).Distinct().ToArray();
        var deployments = await ctx.Db.ModelDeployments.Include(m => m.ProviderAccount).Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
        if (deployments.Count != ids.Length || deployments.Values.Any(m => m.Kind != input.Kind)) { throw new ApiFaultException(400, "Ruttens modeller måste finnas och ha samma typ som rutten."); }
        r.Name = input.Name.Trim(); r.Description = input.Description; r.Kind = input.Kind; r.IsEnabled = input.IsEnabled;
        ctx.Db.RouteTargets.RemoveRange(r.Targets);
        r.Targets = [.. input.Targets.Select(t => new RouteTarget { ModelDeploymentId = t.ModelId, ModelDeployment = deployments[t.ModelId], Priority = t.Priority, Weight = t.Weight })];
        ctx.Db.RouteTargets.AddRange(r.Targets);
    }

    // ---- exchange rate -----------------------------------------------------------------------------------

    public async Task<ExchangeRateDto?> CurrentRateAsync(CancellationToken ct) =>
        await ctx.Db.CurrentRateAsync(ctx.Now, ct) is { } rate ? ExchangeRateDto.Of(rate) : null;

    public async Task<ExchangeRateDto> SetRateAsync(ClaimsPrincipal user, decimal sekPerUnit, CancellationToken ct)
    {
        var before = await CurrentRateAsync(ct);
        var rate = new ExchangeRate { Currency = GatewayQueries.PriceCurrency, SekPerUnit = sekPerUnit, EffectiveFrom = ctx.Now };
        ctx.Db.ExchangeRates.Add(rate);
        await ctx.SaveAsync(user, AuditActions.Update, AuditEntities.ExchangeRate, rate.Id, before, ExchangeRateDto.Of(rate), InvalidationKind.Config, ct);
        return ExchangeRateDto.Of(rate);
    }
}
