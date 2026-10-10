using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Routing;
using Ume.LlmGateway.Domain.Services;
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
        IReadOnlyDictionary<Guid, Guid>? keyReplacements = null,
        RoutingRuleSet? rules = null,
        IReadOnlyList<RuleBuildError>? ruleErrors = null,
        bool usesFallbackRate = false)
    {
        Providers = providers;
        Routes = routes.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        Deployments = providers.SelectMany(p => p.Deployments).ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
        Budgets = budgets.ToLookup(b => (b.Scope, b.ScopeId));
        SekPerUsd = sekPerUsd;
        LoadedAt = loadedAt;
        KeyReplacements = keyReplacements ?? new Dictionary<Guid, Guid>();
        _previousKeys = KeyRotation.PreviousKeys(KeyReplacements);
        Rules = rules ?? RoutingRuleSet.Empty;
        RuleErrors = ruleErrors ?? [];
        UsesFallbackRate = usesFallbackRate;
    }

    public IReadOnlyList<ProviderAccount> Providers { get; }
    public IReadOnlyDictionary<string, RouteAlias> Routes { get; }
    public IReadOnlyDictionary<string, ModelDeployment> Deployments { get; }
    public ILookup<(BudgetScope Scope, Guid ScopeId), Budget> Budgets { get; }
    public decimal SekPerUsd { get; }

    /// <summary>No USD exchange rate was in effect: <see cref="SekPerUsd"/> is <c>Gateway:FallbackSekPerUsd</c>.</summary>
    public bool UsesFallbackRate { get; }

    public DateTimeOffset LoadedAt { get; }
    public IReadOnlyDictionary<Guid, Guid> KeyReplacements { get; }

    private readonly ILookup<Guid, Guid> _previousKeys;

    /// <summary>The key and every key it replaced through rotation. The reverse map is built once per snapshot, not per request.</summary>
    public IReadOnlySet<Guid> KeyLineage(Guid keyId) => KeyRotation.Ancestors(keyId, _previousKeys);

    /// <summary>Compiled routing rules. Rules that failed validation are left out and listed in <see cref="RuleErrors"/>.</summary>
    public RoutingRuleSet Rules { get; }

    public IReadOnlyList<RuleBuildError> RuleErrors { get; }

    /// <summary>Decrypted provider credentials, cached for this snapshot's lifetime (see <see cref="ProviderCredentials"/>).</summary>
    internal ProviderCredentials Credentials { get; } = new();

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

/// <summary>
/// Loads and caches the routing catalogue. An admin change (via <see cref="IInvalidationBus"/>) drops the snapshot, so
/// the next request loads a fresh one. When the snapshot merely ages past the TTL it keeps being served while one
/// background refresh replaces it, so requests never wait on the catalogue queries for routine expiry.
/// </summary>
public sealed partial class GatewayCatalog : IDisposable
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<GatewayCatalog> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly IDisposable _subscription;
    private volatile CatalogSnapshot? _snapshot;
    private int _generation;
    private int _refreshing;

    public GatewayCatalog(IServiceScopeFactory scopes, IOptionsMonitor<GatewayOptions> options, TimeProvider time, IInvalidationBus bus, ILogger<GatewayCatalog> logger)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _scopes = scopes;
        _logger = logger;
        _options = options;
        _time = time;
        _subscription = bus.Subscribe(kind =>
        {
            if (kind == InvalidationKind.Config)
            {
                Invalidate();
            }
        });
    }

    /// <summary>Drops the snapshot. A load that started before this call is not cached, since it may predate the change.</summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _snapshot = null;
    }

    public async Task<CatalogSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        var current = _snapshot;
        var ttl = TimeSpan.FromSeconds(_options.CurrentValue.CatalogCacheSeconds);
        if (current is not null && IsFresh(current, ttl))
        {
            return current;
        }

        if (current is not null && ttl > TimeSpan.Zero)
        {
            RefreshInBackground(ttl);
            return current;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            current = _snapshot;
            if (current is not null && IsFresh(current, ttl))
            {
                return current;
            }

            return await LoadAndCacheAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>A TTL of 0 never serves from cache, even if the wall clock steps backwards (age below zero).</summary>
    private bool IsFresh(CatalogSnapshot snapshot, TimeSpan ttl) =>
        ttl > TimeSpan.Zero && _time.GetUtcNow() - snapshot.LoadedAt < ttl;

    private async Task<CatalogSnapshot> LoadAndCacheAsync(CancellationToken cancellationToken)
    {
        var generation = Volatile.Read(ref _generation);
        var loaded = await LoadAsync(cancellationToken);
        if (generation == Volatile.Read(ref _generation))
        {
            _snapshot = loaded;
        }

        return loaded;
    }

    private void RefreshInBackground(TimeSpan ttl)
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _lock.WaitAsync();
                try
                {
                    if (_snapshot is { } current && _time.GetUtcNow() - current.LoadedAt >= ttl)
                    {
                        await LoadAndCacheAsync(CancellationToken.None);
                    }
                }
                finally
                {
                    _lock.Release();
                }
            }
            catch (Exception ex)
            {
                // Keep serving the previous snapshot; the next request after the TTL tries again.
                LogRefreshFailed(_logger, ex);
            }
            finally
            {
                Volatile.Write(ref _refreshing, 0);
            }
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Background catalogue refresh failed; serving the previous snapshot")]
    private static partial void LogRefreshFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No {Currency} exchange rate is in effect; costs are calculated with Gateway:FallbackSekPerUsd = {SekPerUsd}. Enter a rate in the admin API.")]
    private static partial void LogFallbackRate(ILogger logger, string currency, decimal sekPerUsd);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Routing rule '{Rule}' ({RuleId}) is ignored: {Error}")]
    private static partial void LogRuleIgnored(ILogger logger, string rule, Guid ruleId, string error);

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
        var ruleEntities = await db.RoutingRules.AsNoTracking().Include(r => r.Targets).ToListAsync(cancellationToken);
        var replacements = await db.KeyRotationMapAsync(cancellationToken);
        var now = _time.GetUtcNow();
        var rate = (await db.CurrentRateAsync(now, cancellationToken))?.SekPerUnit;
        if (rate is null)
        {
            LogFallbackRate(_logger, GatewayQueries.PriceCurrency, _options.CurrentValue.FallbackSekPerUsd);
        }

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

        // Compile rules once per snapshot. An invalid rule is skipped (and reported) rather than failing the catalogue.
        var (rules, ruleErrors) = RoutingRuleSet.Build(ruleEntities.Select(r => r.ToDefinition()));
        foreach (var error in ruleErrors)
        {
            LogRuleIgnored(_logger, error.RuleName, error.RuleId, error.Message);
        }

        return new CatalogSnapshot(providers, routes, budgets, rate ?? _options.CurrentValue.FallbackSekPerUsd, now, replacements, rules, ruleErrors,
            usesFallbackRate: rate is null);
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _lock.Dispose();
    }
}
