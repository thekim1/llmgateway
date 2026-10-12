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
        bool usesFallbackRate = false,
        IReadOnlyList<McpServer>? mcpServers = null,
        IReadOnlyList<VirtualKeyMcpGrant>? mcpGrants = null)
    {
        Providers = providers;
        var duplicates = new List<string>();
        Routes = ByName(routes, r => r.Name, duplicates);

        // Names are unique per provider; see PublicModelNames for how clients address a model several providers offer.
        var owned = providers.SelectMany(p => p.Deployments.Select(d => (Provider: p.Name, Deployment: d))).OrderBy(x => x.Deployment.Id).ToList();
        var shared = PublicModelNames.SharedNames(owned.Select(x => x.Deployment.Name));
        var byPublic = new Dictionary<string, ModelDeployment>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (provider, deployment) in owned)
        {
            var qualified = PublicModelNames.Qualified(provider, deployment.Name);
            var publicName = shared.Contains(deployment.Name) ? qualified : deployment.Name;
            _names[deployment.Id] = (publicName, qualified);
            if (!byPublic.TryAdd(publicName, deployment))
            {
                duplicates.Add(publicName);
            }

            _qualified.TryAdd(qualified, deployment);

            if (shared.Contains(deployment.Name))
            {
                (ambiguous.TryGetValue(deployment.Name, out var choices) ? choices : ambiguous[deployment.Name] = []).Add(qualified);
            }
        }

        Deployments = byPublic;
        _ambiguous = ambiguous.ToDictionary(a => a.Key, a => (IReadOnlyList<string>)[.. a.Value.Order(StringComparer.OrdinalIgnoreCase)], StringComparer.OrdinalIgnoreCase);
        DuplicateNames = duplicates;
        Budgets = budgets.ToLookup(b => (b.Scope, b.ScopeId));
        SekPerUsd = sekPerUsd;
        LoadedAt = loadedAt;
        KeyReplacements = keyReplacements ?? new Dictionary<Guid, Guid>();
        _previousKeys = KeyRotation.PreviousKeys(KeyReplacements);
        Rules = rules ?? RoutingRuleSet.Empty;
        RuleErrors = ruleErrors ?? [];
        UsesFallbackRate = usesFallbackRate;

        var mcpDuplicates = new List<string>();
        var mcpInvalid = new List<string>();
        foreach (var server in mcpServers ?? [])
        {
            if (!_mcpServers.TryAdd(server.Slug, server))
            {
                mcpDuplicates.Add(server.Slug);
                continue;
            }

            _mcpServersById[server.Id] = server;
            var tools = new Dictionary<string, McpToolEntry>(StringComparer.Ordinal);
            foreach (var tool in server.Tools.Where(t => t.IsExposed).OrderBy(t => t.Id))
            {
                if (McpToolDefinition.Parse(tool.ApprovedDefinition) is not { } definition)
                {
                    mcpInvalid.Add(tool.ExposedName);
                    continue;
                }

                var entry = new McpToolEntry(server, tool, definition);
                if (tools.ContainsKey(tool.Name) || !_mcpTools.TryAdd(tool.ExposedName, entry))
                {
                    mcpDuplicates.Add(tool.ExposedName);
                    continue;
                }

                tools[tool.Name] = entry;
            }

            _mcpServerTools[server.Id] = tools;
        }

        foreach (var grant in mcpGrants ?? [])
        {
            if (_mcpServersById.ContainsKey(grant.McpServerId))
            {
                (_mcpGrants.TryGetValue(grant.VirtualKeyId, out var byServer) ? byServer : _mcpGrants[grant.VirtualKeyId] = []).TryAdd(grant.McpServerId, grant);
            }
        }

        McpDuplicateNames = mcpDuplicates;
        McpInvalidTools = mcpInvalid;
    }

    public IReadOnlyList<ProviderAccount> Providers { get; }

    /// <summary>
    /// Model or route names that differ only in case from one already loaded (names are matched case-insensitively, the
    /// database's unique index is case-sensitive). The earlier entry is used; the admin API refuses new duplicates.
    /// </summary>
    public IReadOnlyList<string> DuplicateNames { get; }
    public IReadOnlyDictionary<string, RouteAlias> Routes { get; }
    /// <summary>Deployments by public name (see <see cref="PublicModelNames"/>).</summary>
    public IReadOnlyDictionary<string, ModelDeployment> Deployments { get; }

    private readonly Dictionary<Guid, (string Public, string Qualified)> _names = [];
    private readonly Dictionary<string, ModelDeployment> _qualified = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<string>> _ambiguous;

    /// <summary>The deployment's public name (the plain name, or provider/name when several providers have it).</summary>
    public string PublicName(ModelDeployment deployment) => _names.TryGetValue(deployment.Id, out var n) ? n.Public : deployment.Name;

    /// <summary>
    /// For a plain name several providers offer (and no route has), the provider/name choices the client can use; the
    /// gateway refuses to pick one. <c>null</c> when the name is not ambiguous.
    /// </summary>
    public IReadOnlyList<string>? AmbiguousChoices(string model) => Routes.ContainsKey(model) ? null : _ambiguous.GetValueOrDefault(model);
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

    private readonly Dictionary<string, McpServer> _mcpServers = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, McpServer> _mcpServersById = [];
    private readonly Dictionary<Guid, Dictionary<string, McpToolEntry>> _mcpServerTools = [];
    private readonly Dictionary<string, McpToolEntry> _mcpTools = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Dictionary<Guid, VirtualKeyMcpGrant>> _mcpGrants = [];
    private static readonly Dictionary<string, McpToolEntry> NoMcpTools = [];
    private static readonly Dictionary<Guid, VirtualKeyMcpGrant> NoMcpGrants = [];

    /// <summary>MCP servers by slug, enabled or not.</summary>
    public IReadOnlyDictionary<string, McpServer> McpServers => _mcpServers;

    /// <summary>Approved MCP tools by exposed name (<see cref="McpToolNames.Exposed"/>), for the aggregated <c>/mcp</c> and the LLM path.</summary>
    public IReadOnlyDictionary<string, McpToolEntry> McpTools => _mcpTools;

    /// <summary>
    /// Slugs or exposed tool names loaded twice (the database's unique indexes should prevent this). The first by id is
    /// kept; a tool whose exposed name collides is left out of the per-server view too.
    /// </summary>
    public IReadOnlyList<string> McpDuplicateNames { get; }

    /// <summary>Exposed names of approved tools whose approved definition is not a JSON object; they are left out.</summary>
    public IReadOnlyList<string> McpInvalidTools { get; }

    public McpServer? McpServerById(Guid id) => _mcpServersById.GetValueOrDefault(id);

    /// <summary>The server's approved tools by their name on the server, for <c>/mcp/{slug}</c>.</summary>
    public IReadOnlyDictionary<string, McpToolEntry> McpServerTools(Guid serverId) => _mcpServerTools.GetValueOrDefault(serverId) ?? NoMcpTools;

    /// <summary>Decrypted MCP server credentials, cached for this snapshot's lifetime like <see cref="Credentials"/>.</summary>
    internal McpCredentials McpCredentials { get; } = new();

    /// <summary>The key's MCP grants by server id. A key without grants has no MCP access.</summary>
    public IReadOnlyDictionary<Guid, VirtualKeyMcpGrant> McpGrants(Guid keyId) => _mcpGrants.GetValueOrDefault(keyId) ?? NoMcpGrants;

    /// <summary>Decrypted provider credentials, cached for this snapshot's lifetime (see <see cref="ProviderCredentials"/>).</summary>
    internal ProviderCredentials Credentials { get; } = new();

    private static Dictionary<string, T> ByName<T>(IEnumerable<T> items, Func<T, string> name, List<string> duplicates)
    {
        var byName = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (!byName.TryAdd(name(item), item))
            {
                duplicates.Add(name(item));
            }
        }

        return byName;
    }

    /// <summary>Resolves a requested model name to candidate targets: a route alias, or a concrete deployment.</summary>
    public ResolvedModel? Resolve(string model)
    {
        if (Routes.TryGetValue(model, out var route))
        {
            return route.IsEnabled ? new ResolvedModel(route.Name, route.Kind, route.Targets) : null;
        }

        if (Deployments.TryGetValue(model, out var deployment) || _qualified.TryGetValue(model, out deployment))
        {
            var (publicName, qualified) = _names.TryGetValue(deployment.Id, out var n) ? n : (deployment.Name, deployment.Name);
            return new ResolvedModel(publicName, deployment.Kind,
                [new RouteTarget { ModelDeployment = deployment, ModelDeploymentId = deployment.Id, Priority = 0, Weight = 1 }],
                deployment.Name, qualified);
        }

        return null;
    }
}

/// <summary>
/// A requested name resolved to a route or one deployment. For a deployment, <see cref="PlainName"/> and
/// <see cref="QualifiedName"/> are its other names; a key that allows any of them may use it.
/// </summary>
public sealed record ResolvedModel(string Name, ModelKind Kind, IReadOnlyList<RouteTarget> Targets, string? PlainName = null, string? QualifiedName = null)
{
    public bool IsAllowedFor(VirtualKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.IsModelAllowed(Name) || (PlainName is { } plain && key.IsModelAllowed(plain)) || (QualifiedName is { } qualified && key.IsModelAllowed(qualified));
    }
}

/// <summary>An approved MCP tool, the server it belongs to and its parsed approved definition.</summary>
public sealed record McpToolEntry(McpServer Server, McpTool Tool, McpToolDefinition Definition);

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Model or route name '{Name}' differs only in case from another one and is ignored; rename it in the admin UI")]
    private static partial void LogDuplicateName(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP server slug or tool name '{Name}' is loaded twice and the later one is ignored")]
    private static partial void LogDuplicateMcpName(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP tool '{Name}' is ignored: its approved definition is not a JSON object")]
    private static partial void LogInvalidMcpTool(ILogger logger, string name);

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
        var mcpServers = await db.McpServers.AsNoTracking()
            .Include(m => m.Tools.Where(t => t.ApprovalState == McpToolApprovalState.Approved || t.ApprovalState == McpToolApprovalState.Changed))
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        var mcpGrants = await db.VirtualKeyMcpGrants.AsNoTracking().ToListAsync(cancellationToken);
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
            // A Foundry provider keeps the project endpoint the admin entered (it names the project for discovery);
            // requests go to the resource's /openai/v1 or /anthropic/v1. Derived once per snapshot, not per request.
            provider.BaseUrl = FoundryEndpoint.RequestBaseUrl(provider.Type, provider.BaseUrl);
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

        var snapshot = new CatalogSnapshot(providers, routes, budgets, rate ?? _options.CurrentValue.FallbackSekPerUsd, now, replacements, rules, ruleErrors,
            usesFallbackRate: rate is null, mcpServers, mcpGrants);
        foreach (var name in snapshot.DuplicateNames)
        {
            LogDuplicateName(_logger, name);
        }

        foreach (var name in snapshot.McpDuplicateNames)
        {
            LogDuplicateMcpName(_logger, name);
        }

        foreach (var name in snapshot.McpInvalidTools)
        {
            LogInvalidMcpTool(_logger, name);
        }

        return snapshot;
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _lock.Dispose();
    }
}
