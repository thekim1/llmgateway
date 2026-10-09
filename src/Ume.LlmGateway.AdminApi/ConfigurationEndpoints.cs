using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Security;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi;

public sealed record ProviderRequest(
    [property: Required, StringLength(100), RegularExpression(@"[a-z0-9][a-z0-9-]*")] string Name,
    [property: Required, StringLength(500)] string BaseUrl,
    [property: EnumDataType(typeof(ProviderType))] ProviderType Type,
    [property: EnumDataType(typeof(ProviderAuthMode))] ProviderAuthMode AuthMode,
    [property: EnumDataType(typeof(DataResidency))] DataResidency Residency,
    [property: Required] ProviderCapabilities[] Capabilities,
    [property: Range(1, 900)] int TimeoutSeconds = 120,
    bool IsEnabled = true,
    [property: StringLength(200)] string? DisplayName = null,
    [property: StringLength(2000), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Credential = null) : AdminRequest, IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || (uri.Scheme == "http" && Residency != DataResidency.OnPrem))
        {
            yield return new ValidationResult("Ange en HTTPS-adress utan inloggningsuppgifter, frågesträng eller fragment. HTTP är endast tillåtet för on-prem.", [nameof(BaseUrl)]);
        }
        if (Capabilities?.Any(c => !Enum.IsDefined(c) || c == ProviderCapabilities.None) == true)
        {
            yield return new ValidationResult("Egenskapen är ogiltig.", [nameof(Capabilities)]);
        }
    }
}
public sealed record PriceRequest(
    [property: Range(typeof(decimal), "0", "1000000")] decimal InputPerMillionUsd,
    [property: Range(typeof(decimal), "0", "1000000")] decimal CachedInputPerMillionUsd,
    [property: Range(typeof(decimal), "0", "1000000")] decimal OutputPerMillionUsd,
    DateTimeOffset? EffectiveFrom = null) : AdminRequest;
public sealed record ModelRequest(
    Guid ProviderId,
    [property: Required, StringLength(200)] string Name,
    [property: Required, StringLength(200)] string UpstreamModel,
    [property: EnumDataType(typeof(ModelKind))] ModelKind Kind,
    [property: EnumDataType(typeof(ParameterProfile))] ParameterProfile ParameterProfile,
    [property: Range(1, int.MaxValue)] int? ContextWindow = null,
    bool IsEnabled = true,
    PriceRequest? Price = null,
    [property: MaxLength(20)] string[]? Features = null) : AdminRequest;
public sealed record TargetRequest(Guid ModelId, [property: Range(0, 1000)] int Priority, [property: Range(1, 1000000)] int Weight) : AdminRequest;
public sealed record RouteRequest(
    [property: Required, StringLength(200)] string Name,
    [property: EnumDataType(typeof(ModelKind))] ModelKind Kind,
    [property: Required, MinLength(1), MaxLength(100)] TargetRequest[] Targets,
    [property: StringLength(1000)] string? Description = null,
    bool IsEnabled = true) : AdminRequest;
public sealed record DrainRequest(bool Drained) : AdminRequest;
public sealed record BudgetRequest(
    [property: EnumDataType(typeof(BudgetScope))] BudgetScope Scope,
    Guid ScopeId,
    [property: Range(typeof(decimal), "0", "1000000000")] decimal LimitSek,
    [property: EnumDataType(typeof(BudgetPeriod))] BudgetPeriod Period,
    [property: Required, MaxLength(100)] int[] AlertThresholds,
    bool IsActive = true) : AdminRequest, IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (AlertThresholds?.Any(t => t is < 1 or > 100) == true)
        {
            yield return new ValidationResult("Larmgränser måste vara 1–100 procent.", [nameof(AlertThresholds)]);
        }
    }
}
public sealed record ExchangeRequest([property: Range(typeof(decimal), "0.000001", "1000000", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)] decimal SekPerUnit) : AdminRequest;

public static class ConfigurationEndpoints
{
    public static ProviderCapabilities[] Capabilities(ProviderCapabilities value) =>
        [.. Enum.GetValues<ProviderCapabilities>().Where(c => c != ProviderCapabilities.None && value.HasFlag(c))];
    public static object ProviderDto(ProviderAccount p) => new
    {
        p.Id, p.Name, p.DisplayName, p.Type, p.BaseUrl, p.AuthMode,
        hasCredential = !string.IsNullOrEmpty(p.EncryptedCredential), p.Residency,
        capabilities = Capabilities(p.Capabilities), p.IsEnabled, p.IsDrained, p.TimeoutSeconds,
        p.CreatedAt, deploymentCount = p.Deployments.Count,
    };
    public static object PriceDto(ModelPrice p) => new { p.InputPerMillionUsd, p.CachedInputPerMillionUsd, p.OutputPerMillionUsd, p.EffectiveFrom };
    public static object ModelDto(ModelDeployment m, DateTimeOffset now) => new
    {
        m.Id, providerId = m.ProviderAccountId, providerName = m.ProviderAccount?.Name, residency = m.ProviderAccount?.Residency,
        m.Name, m.UpstreamModel, m.Kind, m.ParameterProfile,         m.ContextWindow, m.IsEnabled, m.Features,
        currentPrice = m.PriceAt(now) is { } p ? PriceDto(p) : null,
    };
    public static object RouteDto(RouteAlias r) => new
    {
        r.Id, r.Name, r.Description, r.Kind, r.IsEnabled,
        targets = r.Targets.OrderBy(t => t.Priority).Select(t => new
        {
            modelId = t.ModelDeploymentId, modelName = t.ModelDeployment?.Name,
            providerName = t.ModelDeployment?.ProviderAccount?.Name, residency = t.ModelDeployment?.ProviderAccount?.Residency,
            t.Priority, t.Weight,
        }).ToArray(),
    };

    public static void MapConfiguration(this RouteGroupBuilder api)
    {
        api = api.MapGroup("").RequireAuthorization("read");
        var providers = api.MapGroup("/providers").RequireAuthorization("admin");
        providers.MapGet("", async (AdminContext ctx, CancellationToken ct) =>
            (await ctx.Db.ProviderAccounts.Include(p => p.Deployments).OrderBy(p => p.Name).ToListAsync(ct)).Select(ProviderDto));
        providers.MapPost("", async (ProviderRequest input, AdminContext ctx, ClaimsPrincipal user, CredentialProtector protector, CancellationToken ct) =>
        {
            var p = new ProviderAccount { Name = input.Name, BaseUrl = input.BaseUrl, CreatedAt = ctx.Now };
            Apply(p, input, protector);
            ctx.Db.ProviderAccounts.Add(p);
            await ctx.SaveAsync(user, "create", "Provider", p.Id, null, ProviderDto(p), InvalidationKind.Config, ct);
            return Results.Created($"/api/providers/{p.Id}", ProviderDto(p));
        });
        providers.MapPut("/{id:guid}", async (Guid id, ProviderRequest input, AdminContext ctx, ClaimsPrincipal user, CredentialProtector protector, CancellationToken ct) =>
        {
            var p = await ProviderAsync(ctx, id, ct);
            var before = ProviderDto(p);
            Apply(p, input, protector);
            await ctx.SaveAsync(user, "update", "Provider", id, before, ProviderDto(p), InvalidationKind.Config, ct);
            return Results.Ok(ProviderDto(p));
        });
        providers.MapPost("/{id:guid}/drain", async (Guid id, DrainRequest input, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var p = await ProviderAsync(ctx, id, ct);
            var before = ProviderDto(p); p.IsDrained = input.Drained;
            await ctx.SaveAsync(user, "drain", "Provider", id, before, ProviderDto(p), InvalidationKind.Config, ct);
            return Results.Ok(ProviderDto(p));
        });
        providers.MapPost("/{id:guid}/discover-models", async (Guid id, AdminContext ctx, CredentialProtector protector, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var p = await ProviderAsync(ctx, id, ct);
            return Results.Ok(await ModelDiscovery.DiscoverAsync(p, clients.CreateClient("provider-discovery"), protector, ct));
        });
        providers.MapDelete("/{id:guid}", async (Guid id, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var p = await ProviderAsync(ctx, id, ct);
            if (p.Deployments.Count > 0) { throw new AdminFaultException(409, "Leverantören har modeller och kan inte tas bort."); }
            ctx.Db.ProviderAccounts.Remove(p);
            await ctx.SaveAsync(user, "delete", "Provider", id, ProviderDto(p), null, InvalidationKind.Config, ct);
            return Results.NoContent();
        });
        var models = api.MapGroup("/models").RequireAuthorization("admin");
        models.MapGet("", async (AdminContext ctx, CancellationToken ct) =>
            (await ctx.Db.ModelDeployments.Include(m => m.ProviderAccount).Include(m => m.Prices).OrderBy(m => m.Name).ToListAsync(ct)).Select(m => ModelDto(m, ctx.Now)));
        models.MapPost("", async (ModelRequest input, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var provider = await ProviderAsync(ctx, input.ProviderId, ct);
            var m = new ModelDeployment { Name = input.Name.Trim(), UpstreamModel = input.UpstreamModel, ProviderAccount = provider, ProviderAccountId = provider.Id };
            Apply(m, input);
            if (input.Price is { } price) { m.Prices.Add(Price(price, ctx.Now)); }
            ctx.Db.ModelDeployments.Add(m);
            await ctx.SaveAsync(user, "create", "Model", m.Id, null, ModelDto(m, ctx.Now), InvalidationKind.Config, ct);
            return Results.Created($"/api/models/{m.Id}", ModelDto(m, ctx.Now));
        });
        models.MapPut("/{id:guid}", async (Guid id, ModelRequest input, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var m = await ModelAsync(ctx, id, ct);
            if (m.Kind != input.Kind && await ctx.Db.RouteTargets.AnyAsync(t => t.ModelDeploymentId == id, ct)) { throw new AdminFaultException(409, "Modelltypen kan inte ändras när modellen används i en rutt."); }
            if (!string.Equals(m.Name, input.Name.Trim(), StringComparison.OrdinalIgnoreCase)) { await RoutingRuleEndpoints.EnsureNotUsedByRulesAsync(ctx, m.Name, "byta namn", ct); }
            var before = ModelDto(m, ctx.Now); Apply(m, input);
            await ctx.SaveAsync(user, "update", "Model", id, before, ModelDto(m, ctx.Now), InvalidationKind.Config, ct);
            return Results.Ok(ModelDto(m, ctx.Now));
        });
        models.MapDelete("/{id:guid}", async (Guid id, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var m = await ModelAsync(ctx, id, ct);
            if (await ctx.Db.RouteTargets.AnyAsync(t => t.ModelDeploymentId == id, ct)) { throw new AdminFaultException(409, "Modellen används i en rutt."); }
            await RoutingRuleEndpoints.EnsureNotUsedByRulesAsync(ctx, m.Name, "tas bort", ct);
            ctx.Db.ModelDeployments.Remove(m);
            await ctx.SaveAsync(user, "delete", "Model", id, ModelDto(m, ctx.Now), null, InvalidationKind.Config, ct);
            return Results.NoContent();
        });
        models.MapGet("/{id:guid}/prices", async (Guid id, AdminContext ctx, CancellationToken ct) =>
            (await ModelAsync(ctx, id, ct)).Prices.OrderByDescending(p => p.EffectiveFrom).Select(PriceDto));
        models.MapPost("/{id:guid}/prices", async (Guid id, PriceRequest input, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var m = await ModelAsync(ctx, id, ct);
            var price = Price(input, ctx.Now); m.Prices.Add(price);
            price.ModelDeploymentId = m.Id;
            ctx.Db.ModelPrices.Add(price);
            await ctx.SaveAsync(user, "price", "Model", id, null, PriceDto(price), InvalidationKind.Config, ct);
            return Results.Created($"/api/models/{id}/prices", PriceDto(price));
        });
        var routes = api.MapGroup("/routes").RequireAuthorization("admin");
        routes.MapGet("", async (AdminContext ctx, CancellationToken ct) => (await Routes(ctx).OrderBy(r => r.Name).ToListAsync(ct)).Select(RouteDto));
        routes.MapPost("", async (RouteRequest input, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var r = new RouteAlias { Name = input.Name.Trim() };
            await ApplyRouteAsync(r, input, ctx, ct);
            ctx.Db.RouteAliases.Add(r);
            await ctx.SaveAsync(user, "create", "Route", r.Id, null, RouteDto(r), InvalidationKind.Config, ct);
            return Results.Created($"/api/routes/{r.Id}", RouteDto(r));
        });
        routes.MapPut("/{id:guid}", async (Guid id, RouteRequest input, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var r = await Routes(ctx).SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw new AdminFaultException(404, "Rutten finns inte.");
            if (!string.Equals(r.Name, input.Name.Trim(), StringComparison.OrdinalIgnoreCase)) { await RoutingRuleEndpoints.EnsureNotUsedByRulesAsync(ctx, r.Name, "byta namn", ct); }
            var before = RouteDto(r); ctx.Db.RouteTargets.RemoveRange(r.Targets); r.Targets.Clear();
            await ApplyRouteAsync(r, input, ctx, ct);
            await ctx.SaveAsync(user, "update", "Route", id, before, RouteDto(r), InvalidationKind.Config, ct);
            return Results.Ok(RouteDto(r));
        });
        routes.MapDelete("/{id:guid}", async (Guid id, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var r = await Routes(ctx).SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw new AdminFaultException(404, "Rutten finns inte.");
            await RoutingRuleEndpoints.EnsureNotUsedByRulesAsync(ctx, r.Name, "tas bort", ct);
            ctx.Db.RouteAliases.Remove(r);
            await ctx.SaveAsync(user, "delete", "Route", id, RouteDto(r), null, InvalidationKind.Config, ct);
            return Results.NoContent();
        });
        MapBudgets(api);
        api.MapGet("/settings/exchange-rate", async (AdminContext ctx, CancellationToken ct) => await CurrentRateAsync(ctx, ct));
        api.MapPut("/settings/exchange-rate", async (ExchangeRequest input, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var before = await CurrentRateAsync(ctx, ct);
            var rate = new ExchangeRate { SekPerUnit = input.SekPerUnit, EffectiveFrom = ctx.Now };
            ctx.Db.ExchangeRates.Add(rate);
            await ctx.SaveAsync(user, "update", "ExchangeRate", rate.Id, before, new { rate.Currency, rate.SekPerUnit, rate.EffectiveFrom }, InvalidationKind.Config, ct);
            return Results.Ok(new { rate.Currency, rate.SekPerUnit, rate.EffectiveFrom });
        }).RequireAuthorization("admin");
    }

    /// <summary>One budget per owner and period: spend counters are keyed by owner and period, so two would share one counter.</summary>
    private static async Task EnsureUniquePeriodAsync(AdminContext ctx, BudgetRequest input, Guid? self, CancellationToken ct)
    {
        if (await ctx.Db.Budgets.AnyAsync(x => x.Scope == input.Scope && x.ScopeId == input.ScopeId && x.Period == input.Period && x.Id != self, ct))
        {
            throw new AdminFaultException(409, "Det finns redan en budget för samma period. Ändra den befintliga budgeten.");
        }
    }

    private static void MapBudgets(RouteGroupBuilder api)
    {
        api.MapGet("/budgets", async (BudgetScope? scope, Guid? scopeId, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var items = new List<object>();
            foreach (var b in await ctx.Db.Budgets.Where(b => (scope == null || b.Scope == scope) && (scopeId == null || b.ScopeId == scopeId)).ToListAsync(ct))
            {
                if (await ctx.ScopeNameAsync(user, b.Scope, b.ScopeId, ct) is { } name) { items.Add(await BudgetDtoAsync(b, name, ctx, ct)); }
            }
            return items;
        });
        api.MapPost("/budgets", async (BudgetRequest input, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var name = await ctx.ScopeNameAsync(user, input.Scope, input.ScopeId, ct) ?? throw new AdminFaultException(404, "Budgetens ägare finns inte.");
            await EnsureUniquePeriodAsync(ctx, input, null, ct);
            var b = new Budget { Scope = input.Scope, ScopeId = input.ScopeId, LimitSek = input.LimitSek, Period = input.Period, AlertThresholds = [.. input.AlertThresholds.Distinct().Order()], IsActive = input.IsActive, CreatedAt = ctx.Now };
            ctx.Db.Budgets.Add(b);
            await ctx.SaveAsync(user, "create", "Budget", b.Id, null, new { b.Scope, b.ScopeId, b.LimitSek, b.Period, b.AlertThresholds, b.IsActive }, InvalidationKind.Config, ct);
            return Results.Created($"/api/budgets/{b.Id}", await BudgetDtoAsync(b, name, ctx, ct));
        }).RequireAuthorization("manage");
        api.MapPut("/budgets/{id:guid}", async (Guid id, BudgetRequest input, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var b = await ctx.Db.Budgets.SingleOrDefaultAsync(b => b.Id == id, ct) ?? throw new AdminFaultException(404, "Budgeten finns inte.");
            var oldName = await ctx.ScopeNameAsync(user, b.Scope, b.ScopeId, ct) ?? throw new AdminFaultException(404, "Budgeten finns inte.");
            var name = await ctx.ScopeNameAsync(user, input.Scope, input.ScopeId, ct) ?? throw new AdminFaultException(404, "Budgetens ägare finns inte.");
            await EnsureUniquePeriodAsync(ctx, input, id, ct);
            var before = await BudgetDtoAsync(b, oldName, ctx, ct);
            b.Scope = input.Scope; b.ScopeId = input.ScopeId; b.LimitSek = input.LimitSek; b.Period = input.Period; b.AlertThresholds = [.. input.AlertThresholds.Distinct().Order()]; b.IsActive = input.IsActive;
            await ctx.SaveAsync(user, "update", "Budget", id, before, new { b.Scope, b.ScopeId, b.LimitSek, b.Period, b.AlertThresholds, b.IsActive }, InvalidationKind.Config, ct);
            return Results.Ok(await BudgetDtoAsync(b, name, ctx, ct));
        }).RequireAuthorization("manage");
        api.MapDelete("/budgets/{id:guid}", async (Guid id, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var b = await ctx.Db.Budgets.SingleOrDefaultAsync(b => b.Id == id, ct) ?? throw new AdminFaultException(404, "Budgeten finns inte.");
            if (await ctx.ScopeNameAsync(user, b.Scope, b.ScopeId, ct) is null) { throw new AdminFaultException(404, "Budgeten finns inte."); }
            ctx.Db.Budgets.Remove(b);
            await ctx.SaveAsync(user, "delete", "Budget", id, new { b.Scope, b.ScopeId, b.LimitSek, b.Period }, null, InvalidationKind.Config, ct);
            return Results.NoContent();
        }).RequireAuthorization("manage");
        api.MapGet("/alerts", async (bool? acknowledged, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var result = new List<object>();
            foreach (var a in await ctx.Db.AlertEvents.Where(a => acknowledged == null || a.Acknowledged == acknowledged).OrderByDescending(a => a.Timestamp).ToListAsync(ct))
            {
                if (await ctx.ScopeNameAsync(user, a.Scope, a.ScopeId, ct) is { } name)
                {
                    result.Add(new { a.Id, a.BudgetId, a.Scope, scopeName = name, a.ThresholdPercent, a.SpentSek, a.LimitSek, a.PeriodStart, a.Timestamp, a.Acknowledged });
                }
            }
            return result;
        });
        api.MapPost("/alerts/{id:guid}/acknowledge", async (Guid id, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var a = await ctx.Db.AlertEvents.SingleOrDefaultAsync(a => a.Id == id, ct) ?? throw new AdminFaultException(404, "Larmet finns inte.");
            if (await ctx.ScopeNameAsync(user, a.Scope, a.ScopeId, ct) is null) { throw new AdminFaultException(404, "Larmet finns inte."); }
            a.Acknowledged = true;
            await ctx.SaveAsync(user, "acknowledge", "Alert", id, null, new { a.Acknowledged }, InvalidationKind.Config, ct);
            return Results.NoContent();
        }).RequireAuthorization("manage");
    }

    private static async Task<object> BudgetDtoAsync(Budget b, string name, AdminContext ctx, CancellationToken ct)
    {
        var window = BudgetPeriods.GetWindow(b.Period, ctx.Now);
        var query = ctx.Db.UsageRecords.Where(u => u.Timestamp >= window.Start && u.Timestamp < window.End);
        var keyIds = b.Scope == BudgetScope.VirtualKey
            ? KeyRotation.Descendants(b.ScopeId, await ctx.Db.VirtualKeys.Where(k => k.RotatedToKeyId != null).ToDictionaryAsync(k => k.Id, k => k.RotatedToKeyId!.Value, ct)).ToArray()
            : [];
        query = b.Scope switch { BudgetScope.Department => query.Where(u => u.DepartmentId == b.ScopeId), BudgetScope.Team => query.Where(u => u.TeamId == b.ScopeId), _ => query.Where(u => keyIds.Contains(u.VirtualKeyId)) };
        var spent = await query.SumAsync(u => u.CostSek, ct);
        return new { b.Id, b.Scope, b.ScopeId, scopeName = name, b.LimitSek, b.Period, b.AlertThresholds, b.IsActive, periodStart = window.Start, periodEnd = window.End, spentSek = spent, percentUsed = b.LimitSek == 0 ? (spent > 0 ? 100 : 0) : decimal.Round(spent / b.LimitSek * 100, 2) };
    }

    internal static IQueryable<RouteAlias> Routes(AdminContext ctx) => ctx.Db.RouteAliases.Include(r => r.Targets).ThenInclude(t => t.ModelDeployment!).ThenInclude(m => m.ProviderAccount);
    internal static async Task<ProviderAccount> ProviderAsync(AdminContext ctx, Guid id, CancellationToken ct) => await ctx.Db.ProviderAccounts.Include(p => p.Deployments).SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw new AdminFaultException(404, "Leverantören finns inte.");
    private static async Task<ModelDeployment> ModelAsync(AdminContext ctx, Guid id, CancellationToken ct) => await ctx.Db.ModelDeployments.Include(m => m.ProviderAccount).Include(m => m.Prices).SingleOrDefaultAsync(m => m.Id == id, ct) ?? throw new AdminFaultException(404, "Modellen finns inte.");
    internal static async Task<object?> CurrentRateAsync(AdminContext ctx, CancellationToken ct) =>
        await ctx.Db.ExchangeRates.Where(r => r.Currency == "USD" && r.EffectiveFrom <= ctx.Now).OrderByDescending(r => r.EffectiveFrom).Select(r => new { r.Currency, r.SekPerUnit, r.EffectiveFrom }).FirstOrDefaultAsync(ct);
    internal static void Apply(ProviderAccount p, ProviderRequest input, CredentialProtector protector)
    {
        p.Name = input.Name; p.DisplayName = input.DisplayName; p.BaseUrl = input.BaseUrl.TrimEnd('/'); p.Type = input.Type; p.AuthMode = input.AuthMode;
        p.Residency = input.Residency; p.Capabilities = input.Capabilities.Aggregate(ProviderCapabilities.None, (a, b) => a | b); p.TimeoutSeconds = input.TimeoutSeconds; p.IsEnabled = input.IsEnabled;
        if (input.Credential is not null) { p.EncryptedCredential = input.Credential.Length == 0 ? null : protector.Protect(input.Credential); }
    }
    internal static void Apply(ModelDeployment m, ModelRequest input)
    {
        m.Name = input.Name.Trim(); m.UpstreamModel = input.UpstreamModel.Trim(); m.Kind = input.Kind; m.ParameterProfile = input.ParameterProfile;
        m.ContextWindow = input.ContextWindow; m.IsEnabled = input.IsEnabled;
        if (input.Features is not null)
        {
            m.Features = [.. input.Features.Select(f => f.Trim().ToLowerInvariant()).Where(f => f.Length is > 0 and <= 40).Distinct().Take(20).Order()];
        }
    }
    internal static ModelPrice Price(PriceRequest input, DateTimeOffset now) => new()
    {
        InputPerMillionUsd = input.InputPerMillionUsd, CachedInputPerMillionUsd = input.CachedInputPerMillionUsd,
        OutputPerMillionUsd = input.OutputPerMillionUsd, EffectiveFrom = (input.EffectiveFrom ?? now).ToUniversalTime(),
    };
    internal static async Task ApplyRouteAsync(RouteAlias r, RouteRequest input, AdminContext ctx, CancellationToken ct)
    {
        var ids = input.Targets.Select(t => t.ModelId).Distinct().ToArray();
        var deployments = await ctx.Db.ModelDeployments.Include(m => m.ProviderAccount).Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
        if (deployments.Count != ids.Length || deployments.Values.Any(m => m.Kind != input.Kind)) { throw new AdminFaultException(400, "Ruttens modeller måste finnas och ha samma typ som rutten."); }
        r.Name = input.Name.Trim(); r.Description = input.Description; r.Kind = input.Kind; r.IsEnabled = input.IsEnabled;
        r.Targets = [.. input.Targets.Select(t => new RouteTarget { ModelDeploymentId = t.ModelId, ModelDeployment = deployments[t.ModelId], Priority = t.Priority, Weight = t.Weight })];
        ctx.Db.RouteTargets.AddRange(r.Targets);
    }
}
