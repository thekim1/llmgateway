using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Domain.Routing;
using Ume.LlmGateway.Domain.Routing.Expressions;
using Ume.LlmGateway.Infrastructure.Mcp;
using Ume.LlmGateway.Infrastructure.Security;
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
/// <summary>An MCP tool's approval: <c>Approved</c> with the definition clients see, or <c>Rejected</c>.</summary>
public sealed record ConfigMcpTool(
    [property: Required, StringLength(McpToolSync.MaxNameLength)] string Name,
    [property: EnumDataType(typeof(McpToolApprovalState))] McpToolApprovalState ApprovalState,
    JsonObject? ApprovedDefinition = null,
    [property: Range(typeof(decimal), "0", "1000000")] decimal? PriceSek = null) : AdminRequest, IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ApprovalState is not (McpToolApprovalState.Approved or McpToolApprovalState.Rejected) ||
            (ApprovalState == McpToolApprovalState.Approved && ApprovedDefinition is null))
        {
            yield return new ValidationResult("Ett verktyg är antingen Approved med sin godkända definition eller Rejected.", [nameof(ApprovalState)]);
        }
    }
}
/// <summary>
/// An MCP server with its tools' approvals. The owner is named by the department's cost centre code; keys' grants are
/// not transferred (key ids differ between environments).
/// </summary>
public sealed record ConfigMcpServer(
    [property: Required] McpServerRequest Configuration,
    [property: StringLength(100)] string? OwnerDepartmentCode = null,
    [property: RegularExpression(@"UME_MCP_[A-Z0-9_]+")] string? CredentialEnvironment = null,
    [property: MaxLength(10000)] ConfigMcpTool[]? Tools = null) : AdminRequest;
public sealed record ConfigDocument(
    [property: Required, MaxLength(1000)] ConfigProvider[] Providers,
    [property: Required, MaxLength(10000)] ConfigModel[] Models,
    [property: Required, MaxLength(10000)] ConfigRoute[] Routes,
    [property: Range(1, 1)] int SchemaVersion = 1,
    [property: MaxLength(10000)] ConfigRoutingRule[]? RoutingRules = null,
    [property: MaxLength(1000)] ConfigMcpServer[]? McpServers = null) : AdminRequest;

/// <summary>
/// Moves providers, models with their price history, routes, global routing rules and MCP servers with their tools'
/// approvals between environments.
/// </summary>
public sealed class ConfigTransferService(AdminContext ctx, ProviderCatalogService catalog, RoutingRuleService rules, McpServerService mcp, CredentialProtector protector)
{
    public async Task<ConfigDocument> ExportAsync(CancellationToken ct)
    {
        var providers = await ctx.Db.ProviderAccounts.Include(p => p.Deployments).ThenInclude(m => m.Prices).ToListAsync(ct);
        var routes = await catalog.Routes().ToListAsync(ct);
        var names = PublicModelNames.Public(providers.SelectMany(p => p.Deployments)); // tracked: ProviderAccount is fixed up
        var globalRules = await ctx.Db.RoutingRules.AsNoTracking().Include(r => r.Targets).Where(r => r.Scope == RoutingScope.Global && !r.IsTemplate).OrderBy(r => r.Priority).ThenBy(r => r.Name).ToListAsync(ct);
        var mcpServers = await ctx.Db.McpServers.AsNoTracking().Include(s => s.Tools).OrderBy(s => s.Slug).ToListAsync(ct);
        var owners = mcpServers.Where(s => s.OwnerDepartmentId is not null).Select(s => s.OwnerDepartmentId!.Value).ToList();
        var codes = await ctx.Db.Departments.AsNoTracking().Where(d => owners.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.CostCenterCode, ct);
        return new ConfigDocument(
            [.. providers.Select(p => new ConfigProvider(new ProviderRequest(p.Name, p.BaseUrl, p.Type, p.AuthMode, p.Residency, ProviderCatalogService.Capabilities(p.Capabilities), p.TimeoutSeconds, p.IsEnabled, p.DisplayName)))],
            [.. providers.SelectMany(p => p.Deployments.Select(m => new ConfigModel(p.Name,
                new ModelRequest(Guid.Empty, m.Name, m.UpstreamModel, m.Kind, m.ParameterProfile, m.ContextWindow, m.IsEnabled),
                [.. m.Prices.Select(price => new PriceRequest(price.InputPerMillionUsd, price.CachedInputPerMillionUsd, price.OutputPerMillionUsd, price.EffectiveFrom, price.AudioPerMinuteUsd, price.AudioInputPerMillionUsd, price.AudioOutputPerMillionUsd, price.PerImageUsd))])))],
            [.. routes.Select(r => new ConfigRoute(r.Name, r.Kind, [.. r.Targets.Select(t => new ConfigTarget(names[t.ModelDeploymentId], t.Priority, t.Weight))], r.Description, r.IsEnabled))],
            RoutingRules: [.. globalRules.Select(r => new ConfigRoutingRule(r.Name, [.. r.Targets.Select(t => new RuleTargetRequest(t.Model, t.Weight))], r.Description, r.IsEnabled, r.Priority, r.Condition, r.Chain, [.. r.Fallbacks]))],
            McpServers: [.. mcpServers.Select(s => new ConfigMcpServer(
                new McpServerRequest(s.Slug, s.DisplayName, s.Url, s.Residency, s.AuthType, s.Description, s.IsEnabled, null, [.. s.ForwardHeaders], s.TimeoutSeconds,
                    s.MaxResultBytes, s.AutoApproveNewTools),
                s.OwnerDepartmentId is { } owner ? codes.GetValueOrDefault(owner) : null, null,
                [.. s.Tools.Where(t => t.IsExposed || t.ApprovalState == McpToolApprovalState.Rejected).OrderBy(t => t.Name, StringComparer.Ordinal)
                    .Select(t => t.IsExposed
                        ? new ConfigMcpTool(t.Name, McpToolApprovalState.Approved, JsonNode.Parse(t.ApprovedDefinition!)!.AsObject(), t.PriceSek)
                        : new ConfigMcpTool(t.Name, McpToolApprovalState.Rejected, null, t.PriceSek))]))]);
    }

    /// <summary>Creates or updates everything in the document, by name, in one transaction; nothing is deleted.</summary>
    public async Task<ConfigImportResult> ImportAsync(ConfigDocument input, ClaimsPrincipal user, CancellationToken ct)
    {
        if (input.Providers.Any(p => p.Configuration.Credential is not null))
        {
            throw new ApiFaultException(400, "Importera inte hemligheter. Använd credentialEnvironment med en UME_PROVIDER_-miljövariabel.");
        }
        if (input.Providers.Select(p => p.Configuration.Name).Distinct(StringComparer.Ordinal).Count() != input.Providers.Length ||
            input.Models.Select(m => m.Configuration.Name).Distinct(StringComparer.Ordinal).Count() != input.Models.Length ||
            input.Routes.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count() != input.Routes.Length ||
            (input.RoutingRules ?? []).Select(r => r.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != (input.RoutingRules ?? []).Length ||
            (input.McpServers ?? []).Select(m => m.Configuration.Slug).Distinct(StringComparer.Ordinal).Count() != (input.McpServers ?? []).Length ||
            (input.McpServers ?? []).Any(m => (m.Tools ?? []).Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() != (m.Tools ?? []).Length))
        {
            throw new ApiFaultException(400, "Konfigurationen har dubbletter.");
        }
        if ((input.McpServers ?? []).Any(m => m.Configuration.Credential is not null))
        {
            throw new ApiFaultException(400, "Importera inte hemligheter. Använd credentialEnvironment med en UME_MCP_-miljövariabel.");
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
                    if (string.IsNullOrEmpty(secret)) { throw new ApiFaultException(400, "En refererad leverantörshemlighet saknas."); }
                    config = config with { Credential = secret };
                }
                catalog.Apply(p, config);
            }
            await ctx.Db.SaveChangesAsync(ct);
            foreach (var item in input.Models)
            {
                var config = item.Configuration;
                var provider = await ctx.Db.ProviderAccounts.SingleOrDefaultAsync(p => p.Name == item.ProviderName, ct) ?? throw new ApiFaultException(400, "En modell refererar till en okänd leverantör.");
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
                        || existing.AudioInputPerMillionUsd != price.AudioInputPerMillionUsd || existing.AudioOutputPerMillionUsd != price.AudioOutputPerMillionUsd || existing.PerImageUsd != price.PerImageUsd)
                    {
                        throw new ApiFaultException(409, "Prishistorik är oföränderlig. Lägg till ett nytt giltighetsdatum.");
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
                if (models.Values.Any(m => m is null)) { throw new ApiFaultException(400, "En rutt refererar till en okänd modell. Ange leverantör/modell när flera leverantörer har modellen."); }
                await catalog.ApplyRouteAsync(r, new RouteRequest(item.Name, item.Kind,
                    [.. item.Targets.Select(t => new TargetRequest(models[t.ModelName]!.Id, t.Priority, t.Weight))], item.Description, item.IsEnabled), ct);
            }
            var (rulesCreated, rulesUpdated) = await rules.ImportGlobalAsync(input.RoutingRules ?? [],
                [.. input.Routes.Select(r => r.Name), .. input.Models.Select(m => m.Configuration.Name)], ct);
            created += rulesCreated; updated += rulesUpdated;
            var (mcpCreated, mcpUpdated, skipped) = await ImportMcpAsync(input.McpServers ?? [], user, ct);
            created += mcpCreated; updated += mcpUpdated;
            var counts = new ConfigImportResult(created, updated, skipped);
            ctx.Db.AuditLog.Add(ctx.Audit(user, AuditActions.Import, AuditEntities.Config, null, null, counts));
            await ctx.Db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return counts;
        });
        // Published only after the whole import is committed.
        await ctx.PublishAsync([InvalidationKind.Config], ct);
        return result;
    }

    /// <summary>
    /// MCP servers by slug, and their tools' approvals by name. An approved tool the server has not offered here yet is
    /// created with the approved definition (the next sync compares it with what the server offers); a rejected one that
    /// does not exist here is skipped. A server's credential comes from <c>credentialEnvironment</c> or stays as it is.
    /// Every server and every tool it changes gets its own <c>import</c> audit entry, like the same change made by hand.
    /// </summary>
    private async Task<(int Created, int Updated, int Skipped)> ImportMcpAsync(IReadOnlyList<ConfigMcpServer> input, ClaimsPrincipal user, CancellationToken ct)
    {
        var created = 0; var updated = 0; var skipped = 0;
        foreach (var item in input)
        {
            Guid? owner = null;
            if (item.OwnerDepartmentCode is { } code)
            {
                owner = await ctx.Db.Departments.Where(d => d.CostCenterCode == code).Select(d => (Guid?)d.Id).SingleOrDefaultAsync(ct)
                    ?? throw new ApiFaultException(400, "En MCP-server tillhör en okänd förvaltning.");
            }

            var config = item.Configuration with { OwnerDepartmentId = owner };
            var server = await ctx.Db.McpServers.Include(s => s.Tools).SingleOrDefaultAsync(s => s.Slug == config.Slug, ct);
            await mcp.CheckAddressesAsync(user, config, server, ct);
            var serverBefore = server is null ? null : McpServerAudit.Of(server);
            if (server is null)
            {
                server = new McpServer { Slug = config.Slug, DisplayName = config.DisplayName, Url = config.Url, CreatedAt = ctx.Now };
                ctx.Db.McpServers.Add(server); created++;
            }
            else { updated++; }
            mcp.Apply(server, config, credentialRequired: false);
            if (item.CredentialEnvironment is { } reference)
            {
                var secret = Environment.GetEnvironmentVariable(reference);
                if (string.IsNullOrEmpty(secret)) { throw new ApiFaultException(400, "En refererad MCP-serverhemlighet saknas."); }
                try { _ = McpUpstreamAuth.Parse(config.AuthType, secret); }
                catch (FormatException) { throw new ApiFaultException(400, "En refererad MCP-serverhemlighet har fel form för serverns autentiseringstyp."); }
                server.EncryptedCredential = config.AuthType == McpAuthType.None ? null : protector.Protect(secret);
            }

            ctx.StageAudit(user, AuditActions.Import, AuditEntities.McpServer, server.Id, serverBefore,
                McpServerAudit.Of(server) with { CredentialReplaced = item.CredentialEnvironment is not null && server.EncryptedCredential is not null });

            foreach (var tool in item.Tools ?? [])
            {
                var existing = server.Tools.SingleOrDefault(t => t.Name == tool.Name);
                var toolBefore = existing is null ? null : McpToolAudit.Of(existing);
                if (tool.ApprovalState == McpToolApprovalState.Rejected)
                {
                    if (existing is null) { skipped++; continue; }
                    existing.ApprovalState = McpToolApprovalState.Rejected;
                    existing.ApprovedDefinition = null; existing.ApprovedAt = null; existing.ApprovedBy = null; existing.PriceSek = tool.PriceSek;
                    ctx.StageAudit(user, AuditActions.Import, AuditEntities.McpTool, existing.Id, toolBefore, McpToolAudit.Of(existing));
                    updated++;
                    continue;
                }

                var definition = tool.ApprovedDefinition!.ToJsonString();
                if (existing is null)
                {
                    existing = new McpTool
                    {
                        McpServerId = server.Id, Name = tool.Name, Definition = definition, DefinitionHash = McpToolDefinition.Hash(definition),
                        ExposedName = McpToolNames.Exposed(server.Slug, tool.Name), DiscoveredAt = ctx.Now,
                    };
                    server.Tools.Add(existing);
                    ctx.Db.McpTools.Add(existing); created++;
                }
                else { updated++; }
                McpToolSync.Approve(existing, AdminContext.Actor(user), ctx.Now);
                existing.ApprovedDefinition = definition;
                existing.Title = McpToolSync.Title(definition);
                existing.ExposedName = McpToolNames.Exposed(server.Slug, tool.Name);
                existing.PriceSek = tool.PriceSek;
                if (McpToolDefinition.Hash(existing.Definition) != McpToolDefinition.Hash(definition))
                {
                    existing.ApprovalState = McpToolApprovalState.Changed;
                }

                ctx.StageAudit(user, AuditActions.Import, AuditEntities.McpTool, existing.Id, toolBefore, McpToolAudit.Of(existing));
            }
        }

        await ctx.Db.SaveChangesAsync(ct);
        return (created, updated, skipped);
    }
}
