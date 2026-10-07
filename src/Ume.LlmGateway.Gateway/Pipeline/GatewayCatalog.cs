using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>Immutable in-memory view of routing and pricing configuration.</summary>
public sealed class CatalogSnapshot
{
    public CatalogSnapshot(
        IReadOnlyList<ProviderAccount> providers,
        IReadOnlyList<RouteAlias> routes,
        IReadOnlyList<Budget> budgets,
        decimal sekPerUsd,
        DateTimeOffset loadedAt,
        IReadOnlyDictionary<Guid, Guid>? keyReplacements = null)
    {
        Providers = providers;
        Routes = routes.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        Deployments = providers.SelectMany(p => p.Deployments).ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
        Budgets = budgets.ToLookup(b => (b.Scope, b.ScopeId));
        SekPerUsd = sekPerUsd;
        LoadedAt = loadedAt;
        KeyReplacements = keyReplacements ?? new Dictionary<Guid, Guid>();
    }

    public IReadOnlyList<ProviderAccount> Providers { get; }
    public IReadOnlyDictionary<string, RouteAlias> Routes { get; }
    public IReadOnlyDictionary<string, ModelDeployment> Deployments { get; }
    public ILookup<(BudgetScope Scope, Guid ScopeId), Budget> Budgets { get; }
    public decimal SekPerUsd { get; }
    public DateTimeOffset LoadedAt { get; }
    public IReadOnlyDictionary<Guid, Guid> KeyReplacements { get; }

    /// <summary>Resolves a requested model name to candidate targets: a route alias, or a concrete deployment.</summary>
    public ResolvedModel? Resolve(string model)
    {
        if (Routes.TryGetValue(model, out var route))
        {
            return route.IsEnabled ? new ResolvedModel(route.Name, route.Kind, route.Targets) : null;
        }

        if (Deployments.TryGetValue(model, out var deployment))
        {
            return new ResolvedModel(deployment.Name, deployment.Kind,
                [new RouteTarget { ModelDeployment = deployment, ModelDeploymentId = deployment.Id, Priority = 0, Weight = 1 }]);
        }

        return null;
    }
}

public sealed record ResolvedModel(string Name, ModelKind Kind, IReadOnlyList<RouteTarget> Targets);

/// <summary>Loads and caches the routing catalogue; invalidated by admin changes via <see cref="IInvalidationBus"/>.</summary>
public sealed class GatewayCatalog : IDisposable
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly IDisposable _subscription;
    private volatile CatalogSnapshot? _snapshot;

    public GatewayCatalog(IServiceScopeFactory scopes, IOptionsMonitor<GatewayOptions> options, TimeProvider time, IInvalidationBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _scopes = scopes;
        _options = options;
        _time = time;
        _subscription = bus.Subscribe(kind =>
        {
            if (kind == InvalidationKind.Config)
            {
                _snapshot = null;
            }
        });
    }

    public void Invalidate() => _snapshot = null;

    public async Task<CatalogSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        var current = _snapshot;
        var ttl = TimeSpan.FromSeconds(_options.CurrentValue.CatalogCacheSeconds);
        if (current is not null && _time.GetUtcNow() - current.LoadedAt < ttl)
        {
            return current;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            current = _snapshot;
            if (current is not null && _time.GetUtcNow() - current.LoadedAt < ttl)
            {
                return current;
            }

            current = await LoadAsync(cancellationToken);
            _snapshot = current;
            return current;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<CatalogSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var providers = await db.ProviderAccounts.AsNoTracking()
            .Include(p => p.Deployments).ThenInclude(d => d.Prices)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        var routes = await db.RouteAliases.AsNoTracking().Include(r => r.Targets).ToListAsync(cancellationToken);
        var budgets = await db.Budgets.AsNoTracking().Where(b => b.IsActive).ToListAsync(cancellationToken);
        var replacements = await db.VirtualKeys.AsNoTracking().Where(k => k.RotatedToKeyId != null)
            .ToDictionaryAsync(k => k.Id, k => k.RotatedToKeyId!.Value, cancellationToken);
        var now = _time.GetUtcNow();
        var rate = await db.ExchangeRates.AsNoTracking()
            .Where(r => r.Currency == "USD" && r.EffectiveFrom <= now)
            .OrderByDescending(r => r.EffectiveFrom)
            .Select(r => (decimal?)r.SekPerUnit)
            .FirstOrDefaultAsync(cancellationToken) ?? 10m;

        // Wire navigation properties across the separately loaded graphs.
        var deployments = new Dictionary<Guid, ModelDeployment>();
        foreach (var provider in providers)
        {
            foreach (var deployment in provider.Deployments)
            {
                deployment.ProviderAccount = provider;
                deployments[deployment.Id] = deployment;
            }
        }

        foreach (var route in routes)
        {
            foreach (var target in route.Targets)
            {
                target.ModelDeployment = deployments.GetValueOrDefault(target.ModelDeploymentId);
            }

            route.Targets.RemoveAll(t => t.ModelDeployment is null);
        }

        return new CatalogSnapshot(providers, routes, budgets, rate, now, replacements);
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _lock.Dispose();
    }
}
