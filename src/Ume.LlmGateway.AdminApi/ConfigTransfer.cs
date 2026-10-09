using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Security;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi;

public sealed record ConfigProvider(
    [property: Required] ProviderRequest Configuration,
    [property: RegularExpression(@"UME_PROVIDER_[A-Z0-9_]+")] string? CredentialEnvironment = null) : AdminRequest;
public sealed record ConfigModel(
    [property: Required] string ProviderName,
    [property: Required] ModelRequest Configuration,
    [property: Required] PriceRequest[] Prices) : AdminRequest;
public sealed record ConfigTarget([property: Required] string ModelName, [property: Range(0, 1000)] int Priority, [property: Range(1, 1000000)] int Weight) : AdminRequest;
public sealed record ConfigRoute(
    [property: Required, StringLength(200)] string Name,
    [property: EnumDataType(typeof(ModelKind))] ModelKind Kind,
    [property: Required, MinLength(1), MaxLength(100)] ConfigTarget[] Targets,
    [property: StringLength(1000)] string? Description = null, bool IsEnabled = true) : AdminRequest;
public sealed record ConfigRuleTarget([property: Required, StringLength(200)] string Model, [property: Range(1, 1000000)] int Weight = 1) : AdminRequest;
/// <summary>A global routing rule. Rules scoped to a key, team or department are not transferred: their ids differ between environments.</summary>
public sealed record ConfigRoutingRule(
    [property: Required, StringLength(200)] string Name,
    [property: Required, MinLength(1), MaxLength(20)] ConfigRuleTarget[] Targets,
    [property: StringLength(1000)] string? Description = null,
    bool IsEnabled = true,
    [property: Range(-1_000_000, 1_000_000)] int Priority = 0,
    [property: StringLength(2000)] string? Condition = null,
    bool Chain = false,
    [property: MaxLength(20)] string[]? Fallbacks = null) : AdminRequest;
public sealed record ConfigDocument(
    [property: Required, MaxLength(1000)] ConfigProvider[] Providers,
    [property: Required, MaxLength(10000)] ConfigModel[] Models,
    [property: Required, MaxLength(10000)] ConfigRoute[] Routes,
    [property: Range(1, 1)] int SchemaVersion = 1,
    [property: MaxLength(10000)] ConfigRoutingRule[]? RoutingRules = null) : AdminRequest;

public static class ConfigTransfer
{
    public static async Task<IResult> ExportAsync(AdminContext ctx, CancellationToken ct)
    {
        var providers = await ctx.Db.ProviderAccounts.Include(p => p.Deployments).ThenInclude(m => m.Prices).ToListAsync(ct);
        var routes = await ConfigurationEndpoints.Routes(ctx).ToListAsync(ct);
        var rules = await ctx.Db.RoutingRules.AsNoTracking().Include(r => r.Targets).Where(r => r.Scope == Domain.Routing.RoutingScope.Global).OrderBy(r => r.Priority).ThenBy(r => r.Name).ToListAsync(ct);
        return Results.Ok(new ConfigDocument(
            [.. providers.Select(p => new ConfigProvider(new ProviderRequest(p.Name, p.BaseUrl, p.Type, p.AuthMode, p.Residency, ConfigurationEndpoints.Capabilities(p.Capabilities), p.TimeoutSeconds, p.IsEnabled, p.DisplayName)))],
            [.. providers.SelectMany(p => p.Deployments.Select(m => new ConfigModel(p.Name,
                new ModelRequest(Guid.Empty, m.Name, m.UpstreamModel, m.Kind, m.ParameterProfile, m.ContextWindow, m.IsEnabled),
                [.. m.Prices.Select(price => new PriceRequest(price.InputPerMillionUsd, price.CachedInputPerMillionUsd, price.OutputPerMillionUsd, price.EffectiveFrom))])))],
            [.. routes.Select(r => new ConfigRoute(r.Name, r.Kind, [.. r.Targets.Select(t => new ConfigTarget(t.ModelDeployment!.Name, t.Priority, t.Weight))], r.Description, r.IsEnabled))],
            RoutingRules: [.. rules.Select(r => new ConfigRoutingRule(r.Name, [.. r.Targets.Select(t => new ConfigRuleTarget(t.Model, t.Weight))], r.Description, r.IsEnabled, r.Priority, r.Condition, r.Chain, [.. r.Fallbacks]))]));
    }

    public static async Task<IResult> ImportAsync(ConfigDocument input, AdminContext ctx, ClaimsPrincipal user, CredentialProtector protector, CancellationToken ct)
    {
        if (input.Providers.Any(p => p.Configuration.Credential is not null))
        {
            throw new AdminFaultException(400, "Importera inte hemligheter. Använd credentialEnvironment med en UME_PROVIDER_-miljövariabel.");
        }
        if (input.Providers.Select(p => p.Configuration.Name).Distinct(StringComparer.Ordinal).Count() != input.Providers.Length ||
            input.Models.Select(m => m.Configuration.Name).Distinct(StringComparer.Ordinal).Count() != input.Models.Length ||
            input.Routes.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count() != input.Routes.Length ||
            (input.RoutingRules ?? []).Select(r => r.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != (input.RoutingRules ?? []).Length)
        {
            throw new AdminFaultException(400, "Konfigurationen har dubbletter.");
        }
        return await ctx.Db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
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
                    if (string.IsNullOrEmpty(secret)) { throw new AdminFaultException(400, "En refererad leverantörshemlighet saknas."); }
                    config = config with { Credential = secret };
                }
                ConfigurationEndpoints.Apply(p, config, protector);
            }
            await ctx.Db.SaveChangesAsync(ct);
            foreach (var item in input.Models)
            {
                var config = item.Configuration;
                var provider = await ctx.Db.ProviderAccounts.SingleOrDefaultAsync(p => p.Name == item.ProviderName, ct) ?? throw new AdminFaultException(400, "En modell refererar till en okänd leverantör.");
                var m = await ctx.Db.ModelDeployments.Include(m => m.Prices).SingleOrDefaultAsync(m => m.Name == config.Name, ct);
                if (m is null)
                {
                    m = new ModelDeployment { Name = config.Name, UpstreamModel = config.UpstreamModel, ProviderAccountId = provider.Id };
                    ctx.Db.ModelDeployments.Add(m); created++;
                }
                else
                {
                    if (m.ProviderAccountId != provider.Id) { throw new AdminFaultException(409, "En befintlig modell kan inte flyttas mellan leverantörer."); }
                    if (m.Kind != config.Kind && await ctx.Db.RouteTargets.AnyAsync(t => t.ModelDeploymentId == m.Id, ct)) { throw new AdminFaultException(409, "En modell i en rutt kan inte byta typ."); }
                    updated++;
                }
                ConfigurationEndpoints.Apply(m, config);
                foreach (var price in item.Prices)
                {
                    var timestamp = (price.EffectiveFrom ?? ctx.Now).ToUniversalTime();
                    var existing = m.Prices.SingleOrDefault(p => p.EffectiveFrom == timestamp);
                    if (existing is null)
                    {
                        var added = ConfigurationEndpoints.Price(price with { EffectiveFrom = timestamp }, ctx.Now);
                        added.ModelDeploymentId = m.Id;
                        m.Prices.Add(added);
                        ctx.Db.ModelPrices.Add(added);
                    }
                    else if (existing.InputPerMillionUsd != price.InputPerMillionUsd || existing.CachedInputPerMillionUsd != price.CachedInputPerMillionUsd || existing.OutputPerMillionUsd != price.OutputPerMillionUsd)
                    {
                        throw new AdminFaultException(409, "Prishistorik är oföränderlig. Lägg till ett nytt giltighetsdatum.");
                    }
                }
            }
            await ctx.Db.SaveChangesAsync(ct);
            foreach (var item in input.Routes)
            {
                var r = await ConfigurationEndpoints.Routes(ctx).SingleOrDefaultAsync(r => r.Name == item.Name, ct);
                if (r is null) { r = new RouteAlias { Name = item.Name }; ctx.Db.RouteAliases.Add(r); created++; }
                else { ctx.Db.RouteTargets.RemoveRange(r.Targets); r.Targets.Clear(); updated++; }
                var names = item.Targets.Select(t => t.ModelName).Distinct().ToArray();
                var models = await ctx.Db.ModelDeployments.Where(m => names.Contains(m.Name)).ToDictionaryAsync(m => m.Name, ct);
                if (models.Count != names.Length) { throw new AdminFaultException(400, "En rutt refererar till en okänd modell."); }
                await ConfigurationEndpoints.ApplyRouteAsync(r, new RouteRequest(item.Name, item.Kind,
                    [.. item.Targets.Select(t => new TargetRequest(models[t.ModelName].Id, t.Priority, t.Weight))], item.Description, item.IsEnabled), ctx, ct);
            }
            var (rulesCreated, rulesUpdated) = await RoutingRuleEndpoints.ImportGlobalAsync(ctx, input.RoutingRules ?? [],
                [.. input.Routes.Select(r => r.Name), .. input.Models.Select(m => m.Configuration.Name)], ct);
            created += rulesCreated; updated += rulesUpdated;
            ctx.Db.AuditLog.Add(new AuditLogEntry
            {
                Timestamp = ctx.Now, Actor = user.FindFirstValue("sub") ?? "unknown", Action = "import", EntityType = "Config",
                Details = System.Text.Json.JsonSerializer.Serialize(new { created, updated, skipped = 0 }),
            });
            await ctx.Db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            // Publish only after the complete import transaction is committed.
            await ctx.SaveAsync(user, "invalidate", "Config", "all", null, null, InvalidationKind.Config, ct);
            return Results.Ok(new { created, updated, skipped = 0 });
        });
    }
}
