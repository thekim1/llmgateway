using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Domain.Routing;
using Ume.LlmGateway.Domain.Routing.Expressions;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi;

// The configuration document (GET /api/ops/config/export, POST /api/ops/config/import). Its JSON format is a contract
// between environments: references are by name, since ids differ, and its limits are the API's own.

public sealed record ConfigProvider(
    [property: Required] ProviderRequest Configuration,
    [property: RegularExpression(@"UME_PROVIDER_[A-Z0-9_]+")] string? CredentialEnvironment = null) : AdminRequest;
public sealed record ConfigModel(
    [property: Required] string ProviderName,
    [property: Required] ModelRequest Configuration,
    [property: Required] PriceRequest[] Prices) : AdminRequest;
public sealed record ConfigTarget(
    [property: Required] string ModelName,
    [property: Range(0, RouteLimits.MaxPriority)] int Priority,
    [property: Range(1, RouteLimits.MaxWeight)] int Weight) : AdminRequest;
public sealed record ConfigRoute(
    [property: Required, StringLength(RouteLimits.MaxNameLength)] string Name,
    [property: EnumDataType(typeof(ModelKind))] ModelKind Kind,
    [property: Required, MinLength(1), MaxLength(RouteLimits.MaxTargets)] ConfigTarget[] Targets,
    [property: StringLength(RouteLimits.MaxDescriptionLength)] string? Description = null, bool IsEnabled = true) : AdminRequest;
/// <summary>A global routing rule. Rules scoped to a key, team or department are not transferred: their ids differ between environments.</summary>
public sealed record ConfigRoutingRule(
    [property: Required, StringLength(RoutingRuleLimits.MaxNameLength)] string Name,
    [property: Required, MinLength(1), MaxLength(RoutingRuleSet.MaxTargets)] RuleTargetRequest[] Targets,
    [property: StringLength(RoutingRuleLimits.MaxDescriptionLength)] string? Description = null,
    bool IsEnabled = true,
    [property: Range(-RoutingRuleLimits.MaxPriority, RoutingRuleLimits.MaxPriority)] int Priority = 0,
    [property: StringLength(RoutingExpression.MaxLength)] string? Condition = null,
    bool Chain = false,
    [property: MaxLength(RoutingRuleSet.MaxTargets)] string[]? Fallbacks = null) : AdminRequest;
public sealed record ConfigDocument(
    [property: Required, MaxLength(1000)] ConfigProvider[] Providers,
    [property: Required, MaxLength(10000)] ConfigModel[] Models,
    [property: Required, MaxLength(10000)] ConfigRoute[] Routes,
    [property: Range(1, 1)] int SchemaVersion = 1,
    [property: MaxLength(10000)] ConfigRoutingRule[]? RoutingRules = null) : AdminRequest;

/// <summary>Moves providers, models with their price history, routes and global routing rules between environments.</summary>
public sealed class ConfigTransferService(AdminContext ctx, ProviderCatalogService catalog, RoutingRuleService rules)
{
    public async Task<ConfigDocument> ExportAsync(CancellationToken ct)
    {
        var providers = await ctx.Db.ProviderAccounts.Include(p => p.Deployments).ThenInclude(m => m.Prices).ToListAsync(ct);
        var routes = await catalog.Routes().ToListAsync(ct);
        var names = PublicModelNames.Public(providers.SelectMany(p => p.Deployments)); // tracked: ProviderAccount is fixed up
        var globalRules = await ctx.Db.RoutingRules.AsNoTracking().Include(r => r.Targets).Where(r => r.Scope == RoutingScope.Global && !r.IsTemplate).OrderBy(r => r.Priority).ThenBy(r => r.Name).ToListAsync(ct);
        return new ConfigDocument(
            [.. providers.Select(p => new ConfigProvider(new ProviderRequest(p.Name, p.BaseUrl, p.Type, p.AuthMode, p.Residency, ProviderCatalogService.Capabilities(p.SupportedCapabilities), p.TimeoutSeconds, p.IsEnabled, p.DisplayName, AutoUpdatePrices: p.AutoUpdatePrices)))],
            [.. providers.SelectMany(p => p.Deployments.Select(m => new ConfigModel(p.Name,
                new ModelRequest(Guid.Empty, m.Name, m.UpstreamModel, m.Kind, m.ParameterProfile, m.ContextWindow, m.IsEnabled, Features: m.Features,
                    AutoUpdatePrice: m.AutoUpdatePrice, Description: m.Description, BaseModel: m.BaseModel),
                [.. m.Prices.Select(price => new PriceRequest(price.InputPerMillionUsd, price.CachedInputPerMillionUsd, price.OutputPerMillionUsd, price.EffectiveFrom, price.AudioPerMinuteUsd, price.AudioInputPerMillionUsd, price.AudioOutputPerMillionUsd, price.PerImageUsd, price.CacheWriteInputPerMillionUsd))])))],
            [.. routes.Select(r => new ConfigRoute(r.Name, r.Kind, [.. r.Targets.Select(t => new ConfigTarget(names[t.ModelDeploymentId], t.Priority, t.Weight))], r.Description, r.IsEnabled))],
            RoutingRules: [.. globalRules.Select(r => new ConfigRoutingRule(r.Name, [.. r.Targets.Select(t => new RuleTargetRequest(t.Model, t.Weight))], r.Description, r.IsEnabled, r.Priority, r.Condition, r.Chain, [.. r.Fallbacks]))]);
    }

    /// <summary>Creates or updates everything in the document, by name, in one transaction; nothing is deleted.</summary>
    public async Task<ConfigImportResult> ImportAsync(ConfigDocument input, CancellationToken ct)
    {
        if (input.Providers.Any(p => p.Configuration.Credential is not null))
        {
            throw new ApiFaultException(400, "Do not import secrets. Use credentialEnvironment with a UME_PROVIDER_ environment variable.");
        }
        if (input.Providers.Select(p => p.Configuration.Name).Distinct(StringComparer.Ordinal).Count() != input.Providers.Length ||
            input.Models.Select(m => m.Configuration.Name).Distinct(StringComparer.Ordinal).Count() != input.Models.Length ||
            input.Routes.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count() != input.Routes.Length ||
            (input.RoutingRules ?? []).Select(r => r.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != (input.RoutingRules ?? []).Length)
        {
            throw new ApiFaultException(400, "The configuration has duplicates.");
        }
        var result = await ctx.Db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            ctx.Db.ChangeTracker.Clear();
            await using var transaction = await ctx.Db.Database.BeginTransactionAsync(ct);
            var created = 0; var updated = 0;
            foreach (var item in input.Providers)
            {
                var config = item.Configuration;
                var p = await ctx.Db.ProviderAccounts.SingleOrDefaultAsync(p => p.Name == config.Name, ct);
                if (p is null)
                {
                    p = new ProviderAccount { Name = config.Name, BaseUrl = config.BaseUrl, CreatedAt = ctx.Now };
                    ctx.Db.ProviderAccounts.Add(p); created++;
                }
                else { updated++; }
                if (item.CredentialEnvironment is { } reference)
                {
                    var secret = Environment.GetEnvironmentVariable(reference);
                    if (string.IsNullOrEmpty(secret)) { throw new ApiFaultException(400, "A referenced provider secret is missing."); }
                    config = config with { Credential = secret };
                }
                catalog.Apply(p, config);
            }
            await ctx.Db.SaveChangesAsync(ct);
            foreach (var item in input.Models)
            {
                var config = item.Configuration;
                var provider = await ctx.Db.ProviderAccounts.SingleOrDefaultAsync(p => p.Name == item.ProviderName, ct) ?? throw new ApiFaultException(400, "A model refers to an unknown provider.");
                // Model names are unique per provider.
                var m = await ctx.Db.ModelDeployments.Include(m => m.Prices).SingleOrDefaultAsync(m => m.ProviderAccountId == provider.Id && m.Name == config.Name, ct);
                if (m is null)
                {
                    m = new ModelDeployment { Name = config.Name, UpstreamModel = config.UpstreamModel, ProviderAccountId = provider.Id };
                    ctx.Db.ModelDeployments.Add(m); created++;
                }
                else
                {
                    await catalog.EnsureKindCanChangeAsync(m, config.Kind, ct);
                    updated++;
                }
                catalog.Apply(m, config);
                foreach (var price in item.Prices)
                {
                    var timestamp = (price.EffectiveFrom ?? ctx.Now).ToUniversalTime();
                    var existing = m.Prices.SingleOrDefault(p => p.EffectiveFrom == timestamp);
                    if (existing is null)
                    {
                        var added = catalog.NewPrice(price with { EffectiveFrom = timestamp });
                        added.ModelDeploymentId = m.Id;
                        m.Prices.Add(added);
                        ctx.Db.ModelPrices.Add(added);
                    }
                    else if (existing.InputPerMillionUsd != price.InputPerMillionUsd || existing.CachedInputPerMillionUsd != price.CachedInputPerMillionUsd || existing.OutputPerMillionUsd != price.OutputPerMillionUsd || existing.AudioPerMinuteUsd != price.AudioPerMinuteUsd
                        || existing.AudioInputPerMillionUsd != price.AudioInputPerMillionUsd || existing.AudioOutputPerMillionUsd != price.AudioOutputPerMillionUsd || existing.PerImageUsd != price.PerImageUsd
                        || existing.CacheWriteInputPerMillionUsd != price.CacheWriteInputPerMillionUsd)
                    {
                        throw new ApiFaultException(409, "Price history cannot be changed. Add a new effective date.");
                    }
                }
            }
            await ctx.Db.SaveChangesAsync(ct);
            // Route targets name a model by its public name or provider/name, as clients do.
            var allModels = await ctx.Db.ModelDeployments.Include(m => m.ProviderAccount).ToListAsync(ct);
            var publicNames = PublicModelNames.Public(allModels);
            var byName = new Dictionary<string, ModelDeployment>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in allModels)
            {
                byName.TryAdd(publicNames[model.Id], model);
                byName.TryAdd(PublicModelNames.Qualified(model.ProviderAccount!.Name, model.Name), model);
            }
            foreach (var item in input.Routes)
            {
                var r = await catalog.Routes().SingleOrDefaultAsync(r => r.Name == item.Name, ct);
                if (r is null) { r = new RouteAlias { Name = item.Name }; ctx.Db.RouteAliases.Add(r); created++; }
                else { updated++; }
                var models = item.Targets.Select(t => t.ModelName).Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(n => n, n => byName.GetValueOrDefault(n), StringComparer.OrdinalIgnoreCase);
                if (models.Values.Any(m => m is null)) { throw new ApiFaultException(400, "A route refers to an unknown model. Give provider/model when several providers offer the model."); }
                await catalog.ApplyRouteAsync(r, new RouteRequest(item.Name, item.Kind,
                    [.. item.Targets.Select(t => new TargetRequest(models[t.ModelName]!.Id, t.Priority, t.Weight))], item.Description, item.IsEnabled), ct);
            }
            var (rulesCreated, rulesUpdated) = await rules.ImportGlobalAsync(input.RoutingRules ?? [],
                [.. input.Routes.Select(r => r.Name), .. input.Models.Select(m => m.Configuration.Name)], ct);
            created += rulesCreated; updated += rulesUpdated;
            var counts = new ConfigImportResult(created, updated, 0);
            ctx.Db.AuditLog.Add(ctx.Audit(AuditActions.Import, AuditEntities.Config, null, null, counts));
            await ctx.Db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return counts;
        });
        // Published only after the whole import is committed.
        await ctx.PublishAsync([InvalidationKind.Config], ct);
        return result;
    }
}
