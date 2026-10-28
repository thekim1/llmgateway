using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Security;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi;

/// <summary>Providers, model deployments, their prices and route aliases.</summary>
public sealed class ProviderCatalogService(AdminContext ctx, RoutingRuleService rules, CredentialProtector protector, ModelListService modelList)
{
    public static ProviderCapabilities[] Capabilities(ProviderCapabilities value) =>
        [.. Enum.GetValues<ProviderCapabilities>().Where(c => c != ProviderCapabilities.None && value.HasFlag(c))];

    // ---- providers ---------------------------------------------------------------------------------------

    public async Task<List<ProviderDto>> ProvidersAsync(CancellationToken ct) =>
        [.. (await ctx.Db.ProviderAccounts.Include(p => p.Deployments).OrderBy(p => p.Name).ToListAsync(ct)).Select(ProviderDto.Of)];

    public async Task<ProviderAccount> ProviderAsync(Guid id, CancellationToken ct) =>
        await ctx.Db.ProviderAccounts.Include(p => p.Deployments).SingleOrDefaultAsync(p => p.Id == id, ct)
        ?? throw new ApiFaultException(404, "The provider does not exist.");

    public async Task<ProviderDto> CreateProviderAsync(ProviderRequest input, CancellationToken ct)
    {
        var p = new ProviderAccount { Name = input.Name, BaseUrl = input.BaseUrl, CreatedAt = ctx.Now };
        Apply(p, input);
        ctx.Db.ProviderAccounts.Add(p);
        await ctx.SaveAsync(AuditActions.Create, AuditEntities.Provider, p.Id, null, ProviderAudit.Of(p), InvalidationKind.Config, ct);
        return ProviderDto.Of(p);
    }

    public async Task<ProviderDto> UpdateProviderAsync(Guid id, ProviderRequest input, CancellationToken ct)
    {
        var p = await ProviderAsync(id, ct);
        var before = ProviderAudit.Of(p);
        Apply(p, input);
        await ctx.SaveAsync(AuditActions.Update, AuditEntities.Provider, id, before, ProviderAudit.Of(p), InvalidationKind.Config, ct);
        return ProviderDto.Of(p);
    }

    public async Task<ProviderDto> DrainAsync(Guid id, bool drained, CancellationToken ct)
    {
        var p = await ProviderAsync(id, ct);
        var before = ProviderAudit.Of(p);
        p.IsDrained = drained;
        await ctx.SaveAsync(AuditActions.Drain, AuditEntities.Provider, id, before, ProviderAudit.Of(p), InvalidationKind.Config, ct);
        return ProviderDto.Of(p);
    }

    public async Task DeleteProviderAsync(Guid id, CancellationToken ct)
    {
        var p = await ProviderAsync(id, ct);
        if (p.Deployments.Count > 0)
        {
            throw new ApiFaultException(409, "The provider has models and cannot be deleted.");
        }

        ctx.Db.ProviderAccounts.Remove(p);
        await ctx.SaveAsync(AuditActions.Delete, AuditEntities.Provider, id, ProviderAudit.Of(p), null, InvalidationKind.Config, ct);
    }

    public const string CredentialForNewAddressMessage =
        "Enter the credential again when you change the provider's address. The stored credential is never sent to a new address.";

    /// <summary>
    /// Copies the request onto the provider. A <c>null</c> credential keeps the stored one; an empty one removes it. A stored
    /// credential is only kept while the scheme, host and port stay the same, so editing the address cannot send it elsewhere.
    /// </summary>
    public void Apply(ProviderAccount p, ProviderRequest input)
    {
        if (p.EncryptedCredential is not null && input.Credential is null && !SameOrigin(p.BaseUrl, input.BaseUrl))
        {
            throw ApiFaultException.Validation(new Dictionary<string, string[]> { ["credential"] = [CredentialForNewAddressMessage] });
        }

        p.Name = input.Name; p.DisplayName = input.DisplayName; p.BaseUrl = input.BaseUrl.TrimEnd('/'); p.Type = input.Type; p.AuthMode = input.AuthMode;
        p.Residency = input.Residency; p.TimeoutSeconds = input.TimeoutSeconds; p.IsEnabled = input.IsEnabled;
        if (input.Credential is not null) { p.EncryptedCredential = input.Credential.Length == 0 ? null : protector.Protect(input.Credential); }
        if (input.AutoUpdatePrices is { } auto) { p.AutoUpdatePrices = auto; }
        // Only an OpenAI-compatible server reads it; the others store their fixed set, which a change to OpenAI-compatible starts from.
        p.Capabilities = ProviderTypes.Fixed(p.Type)
            ?? (input.Capabilities is { } chosen ? chosen.Aggregate(ProviderCapabilities.None, (a, b) => a | b)
                : p.Capabilities == ProviderCapabilities.None ? ProviderTypes.CompatibleDefault : p.Capabilities);
    }

    /// <summary>Same scheme, host and port (the parts that decide who receives the credential); the path may change.</summary>
    internal static bool SameOrigin(string current, string next) =>
        Uri.TryCreate(current, UriKind.Absolute, out var a) && Uri.TryCreate(next, UriKind.Absolute, out var b) &&
        Uri.Compare(a, b, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;

    // ---- models and prices -------------------------------------------------------------------------------

    public async Task<List<ModelDto>> ModelsAsync(CancellationToken ct)
    {
        var all = await ctx.Db.ModelDeployments.Include(m => m.ProviderAccount).Include(m => m.Prices).OrderBy(m => m.Name).ToListAsync(ct);
        var names = PublicModelNames.Public(all);
        var list = await modelList.GetAsync(ct);
        return [.. all.Select(m => ModelDto.Of(m, ctx.Now, names[m.Id], list, modelList.UpdatesPrices))];
    }

    private async Task<ModelDto> DtoAsync(ModelDeployment m, CancellationToken ct) =>
        ModelDto.Of(m, ctx.Now, await PublicNameAsync(m, ct), await modelList.GetAsync(ct), modelList.UpdatesPrices);

    public async Task<ModelDeployment> ModelAsync(Guid id, CancellationToken ct) =>
        await ctx.Db.ModelDeployments.Include(m => m.ProviderAccount).Include(m => m.Prices).SingleOrDefaultAsync(m => m.Id == id, ct)
        ?? throw new ApiFaultException(404, "The model does not exist.");

    public async Task<ModelDto> CreateModelAsync(ModelRequest input, CancellationToken ct)
    {
        var provider = await ProviderAsync(input.ProviderId, ct);
        await EnsureModelNameFreeAsync(input.Name.Trim(), null, provider.Id, ct);
        var m = new ModelDeployment { Name = input.Name.Trim(), UpstreamModel = input.UpstreamModel, ProviderAccount = provider, ProviderAccountId = provider.Id };
        Apply(m, input);
        if (input.Price is { } price) { m.Prices.Add(NewPrice(price)); }
        ctx.Db.ModelDeployments.Add(m);
        await ctx.SaveAsync(AuditActions.Create, AuditEntities.Model, m.Id, null, ModelAudit.Of(m), InvalidationKind.Config, ct);
        return await DtoAsync(m, ct);
    }

    public async Task<ModelDto> UpdateModelAsync(Guid id, ModelRequest input, CancellationToken ct)
    {
        var m = await ModelAsync(id, ct);
        await EnsureKindCanChangeAsync(m, input.Kind, ct);
        await EnsureModelNameFreeAsync(input.Name.Trim(), id, m.ProviderAccountId, ct);
        if (!string.Equals(m.Name, input.Name.Trim(), StringComparison.OrdinalIgnoreCase)) { await rules.EnsureNotUsedByRulesAsync(m.Name, "renamed", ct); }
        var before = ModelAudit.Of(m);
        Apply(m, input);
        await ctx.SaveAsync(AuditActions.Update, AuditEntities.Model, id, before, ModelAudit.Of(m), InvalidationKind.Config, ct);
        return await DtoAsync(m, ct);
    }

    public async Task DeleteModelAsync(Guid id, CancellationToken ct)
    {
        var m = await ModelAsync(id, ct);
        if (await ctx.Db.RouteTargets.AnyAsync(t => t.ModelDeploymentId == id, ct)) { throw new ApiFaultException(409, "The model is used in a route."); }
        await rules.EnsureNotUsedByRulesAsync(m.Name, "deleted", ct);
        ctx.Db.ModelDeployments.Remove(m);
        await ctx.SaveAsync(AuditActions.Delete, AuditEntities.Model, id, ModelAudit.Of(m), null, InvalidationKind.Config, ct);
    }

    /// <summary>A route only holds models of its own kind, so a model in a route keeps its kind.</summary>
    public async Task EnsureKindCanChangeAsync(ModelDeployment m, ModelKind kind, CancellationToken ct)
    {
        if (m.Kind != kind && await ctx.Db.RouteTargets.AnyAsync(t => t.ModelDeploymentId == m.Id, ct))
        {
            throw new ApiFaultException(409, "The model type cannot be changed while the model is used in a route.");
        }
    }

    public void Apply(ModelDeployment m, ModelRequest input)
    {
        m.Name = input.Name.Trim(); m.UpstreamModel = input.UpstreamModel.Trim(); m.Kind = input.Kind; m.ParameterProfile = input.ParameterProfile;
        m.ContextWindow = input.ContextWindow; m.IsEnabled = input.IsEnabled;
        if (input.AutoUpdatePrice is { } auto) { m.AutoUpdatePrice = auto; }
        if (input.Description is { } description) { m.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(); }
        if (input.BaseModel is { } baseModel) { m.BaseModel = string.IsNullOrWhiteSpace(baseModel) ? null : baseModel.Trim(); }
        if (input.Features is not null)
        {
            m.Features = [.. input.Features.Select(f => f.Trim().ToLowerInvariant()).Where(f => f.Length is > 0 and <= 40).Distinct().Take(20).Order()];
        }
    }

    /// <summary>
    /// A model's name is unique per provider, compared case-insensitively as the gateway does. Other providers may use the
    /// same name: clients then name the provider (<see cref="PublicModelNames"/>).
    /// </summary>
    private async Task EnsureModelNameFreeAsync(string name, Guid? id, Guid providerId, CancellationToken ct)
    {
        var pattern = LikeLiteral(name);
        if (await ctx.Db.ModelDeployments.AnyAsync(x => x.Id != id && x.ProviderAccountId == providerId && EF.Functions.ILike(x.Name, pattern, LikeEscape), ct))
        {
            throw new ApiFaultException(409, $"The provider already has a model called {name}.");
        }
    }

    /// <summary>The model's public name: provider/name when another provider has a model of the same name.</summary>
    public async Task<string> PublicNameAsync(ModelDeployment m, CancellationToken ct)
    {
        var pattern = LikeLiteral(m.Name);
        var shared = await ctx.Db.ModelDeployments.AnyAsync(x => x.Id != m.Id && EF.Functions.ILike(x.Name, pattern, LikeEscape), ct);
        return shared ? PublicModelNames.Qualified(m.ProviderAccount?.Name ?? (await ProviderAsync(m.ProviderAccountId, ct)).Name, m.Name) : m.Name;
    }

    public async Task<List<PriceDto>> PricesAsync(Guid id, CancellationToken ct) =>
        [.. (await ModelAsync(id, ct)).Prices.OrderByDescending(p => p.EffectiveFrom).Select(PriceDto.Of)];

    public async Task<PriceDto> AddPriceAsync(Guid id, PriceRequest input, CancellationToken ct)
    {
        var m = await ModelAsync(id, ct);
        if ((await modelList.GetAsync(ct)).Mode(m, modelList.UpdatesPrices) == PriceMode.List)
        {
            throw new ApiFaultException(409,
                "The model's price follows the models.dev list price. Turn off automatic prices for the model or its provider to set your own.");
        }
        var price = NewPrice(input);
        price.ModelDeploymentId = m.Id;
        m.Prices.Add(price);
        ctx.Db.ModelPrices.Add(price);
        await ctx.SaveAsync(AuditActions.Price, AuditEntities.Model, id, null, PriceDto.Of(price), InvalidationKind.Config, ct);
        return PriceDto.Of(price);
    }

    /// <summary>A price from the request, effective now unless the request says otherwise.</summary>
    public ModelPrice NewPrice(PriceRequest input) => new()
    {
        InputPerMillionUsd = input.InputPerMillionUsd, CachedInputPerMillionUsd = input.CachedInputPerMillionUsd,
        OutputPerMillionUsd = input.OutputPerMillionUsd, AudioPerMinuteUsd = input.AudioPerMinuteUsd,
        AudioInputPerMillionUsd = input.AudioInputPerMillionUsd, AudioOutputPerMillionUsd = input.AudioOutputPerMillionUsd,
        PerImageUsd = input.PerImageUsd, CacheWriteInputPerMillionUsd = input.CacheWriteInputPerMillionUsd,
        EffectiveFrom = (input.EffectiveFrom ?? ctx.Now).ToUniversalTime(),
    };

    // ---- routes ------------------------------------------------------------------------------------------

    /// <summary>Route aliases with their targets' models and providers (the navigations <see cref="RouteDto"/> shows).</summary>
    public IQueryable<RouteAlias> Routes() =>
        ctx.Db.RouteAliases.Include(r => r.Targets).ThenInclude(t => t.ModelDeployment!).ThenInclude(m => m.ProviderAccount);

    public async Task<List<RouteDto>> RoutesAsync(CancellationToken ct) =>
        [.. (await Routes().OrderBy(r => r.Name).ToListAsync(ct)).Select(RouteDto.Of)];

    private async Task<RouteAlias> RouteAsync(Guid id, CancellationToken ct) =>
        await Routes().SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw new ApiFaultException(404, "The route does not exist.");

    public async Task<RouteDto> CreateRouteAsync(RouteRequest input, CancellationToken ct)
    {
        var r = new RouteAlias { Name = input.Name.Trim() };
        await EnsureRouteNameFreeAsync(r.Name, null, ct);
        await ApplyRouteAsync(r, input, ct);
        ctx.Db.RouteAliases.Add(r);
        await ctx.SaveAsync(AuditActions.Create, AuditEntities.Route, r.Id, null, RouteAudit.Of(r), InvalidationKind.Config, ct);
        return RouteDto.Of(r);
    }

    public async Task<RouteDto> UpdateRouteAsync(Guid id, RouteRequest input, CancellationToken ct)
    {
        var r = await RouteAsync(id, ct);
        if (!string.Equals(r.Name, input.Name.Trim(), StringComparison.OrdinalIgnoreCase)) { await rules.EnsureNotUsedByRulesAsync(r.Name, "renamed", ct); }
        await EnsureRouteNameFreeAsync(input.Name.Trim(), id, ct);
        var before = RouteAudit.Of(r);
        await ApplyRouteAsync(r, input, ct);
        await ctx.SaveAsync(AuditActions.Update, AuditEntities.Route, id, before, RouteAudit.Of(r), InvalidationKind.Config, ct);
        return RouteDto.Of(r);
    }

    public async Task DeleteRouteAsync(Guid id, CancellationToken ct)
    {
        var r = await RouteAsync(id, ct);
        await rules.EnsureNotUsedByRulesAsync(r.Name, "deleted", ct);
        ctx.Db.RouteAliases.Remove(r);
        await ctx.SaveAsync(AuditActions.Delete, AuditEntities.Route, id, RouteAudit.Of(r), null, InvalidationKind.Config, ct);
    }

    private const string LikeEscape = "\\";

    /// <summary>A name as an ILIKE pattern that matches only itself (case-insensitively): wildcards are escaped.</summary>
    private static string LikeLiteral(string name) =>
        name.Replace(LikeEscape, LikeEscape + LikeEscape, StringComparison.Ordinal)
            .Replace("%", LikeEscape + "%", StringComparison.Ordinal).Replace("_", LikeEscape + "_", StringComparison.Ordinal);

    /// <summary>Route names are matched case-insensitively. A route may share a model's name: the route is used (fallback).</summary>
    private async Task EnsureRouteNameFreeAsync(string name, Guid? id, CancellationToken ct)
    {
        var pattern = LikeLiteral(name);
        if (await ctx.Db.RouteAliases.AnyAsync(x => x.Id != id && EF.Functions.ILike(x.Name, pattern, LikeEscape), ct))
        {
            throw new ApiFaultException(409, $"There is already a route called {name}.");
        }
    }

    /// <summary>Copies the request onto the route and replaces its targets. The targets' models must exist and be of the route's kind.</summary>
    public async Task ApplyRouteAsync(RouteAlias r, RouteRequest input, CancellationToken ct)
    {
        var ids = input.Targets.Select(t => t.ModelId).Distinct().ToArray();
        var deployments = await ctx.Db.ModelDeployments.Include(m => m.ProviderAccount).Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
        if (deployments.Count != ids.Length || deployments.Values.Any(m => m.Kind != input.Kind)) { throw new ApiFaultException(400, "The route's models must exist and be of the same kind as the route."); }
        r.Name = input.Name.Trim(); r.Description = input.Description; r.Kind = input.Kind; r.IsEnabled = input.IsEnabled;
        ctx.Db.RouteTargets.RemoveRange(r.Targets);
        r.Targets = [.. input.Targets.Select(t => new RouteTarget { ModelDeploymentId = t.ModelId, ModelDeployment = deployments[t.ModelId], Priority = t.Priority, Weight = t.Weight })];
        ctx.Db.RouteTargets.AddRange(r.Targets);
    }
}
