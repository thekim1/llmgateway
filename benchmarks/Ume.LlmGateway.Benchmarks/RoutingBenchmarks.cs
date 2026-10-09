using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Gateway.Pipeline;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.Benchmarks;

/// <summary>
/// In-memory routing decisions made for every request. <see cref="RotatedKeys"/> models a gateway that has been in
/// production for a while: every key rotation adds a permanent entry to the replacement map.
/// </summary>
[MemoryDiagnoser]
public class RoutingBenchmarks
{
    private CatalogSnapshot _snapshot = null!;
    private VirtualKey _key = null!;
    private RouteResolver _router = null!;
    private ResolvedModel _model = null!;
    private VirtualKeyHasher _hasher = null!;
    private string _presentedKey = null!;

    [Params(0, 10_000)]
    public int RotatedKeys { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var department = new Department { Name = "Bench", CostCenterCode = "B" };
        var team = new Team { Name = "Bench", DepartmentId = department.Id, Department = department };
        _key = new VirtualKey { Name = "bench", Prefix = "ume-sk-bench", KeyHash = "x", TeamId = team.Id, Team = team };

        var providers = Enumerable.Range(0, 3).Select(i =>
        {
            var provider = new ProviderAccount
            {
                Name = $"provider-{i}", BaseUrl = "http://upstream", Residency = DataResidency.Eu,
                Capabilities = ProviderCapabilities.ChatCompletions | ProviderCapabilities.Streaming,
            };
            provider.Deployments.Add(new ModelDeployment
            {
                Name = $"provider-{i}/chat", UpstreamModel = "chat", ProviderAccount = provider, ProviderAccountId = provider.Id,
                Prices = [new ModelPrice { InputPerMillionUsd = 1, OutputPerMillionUsd = 2, EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-30) }],
            });
            return provider;
        }).ToList();
        var route = new RouteAlias
        {
            Name = "ume/chat",
            Targets = [.. providers.Select((p, i) => new RouteTarget { ModelDeployment = p.Deployments[0], ModelDeploymentId = p.Deployments[0].Id, Priority = i / 2 })],
        };
        var budgets = new List<Budget>
        {
            new() { Scope = BudgetScope.VirtualKey, ScopeId = _key.Id, LimitSek = 100, Period = BudgetPeriod.Monthly },
            new() { Scope = BudgetScope.Team, ScopeId = team.Id, LimitSek = 1000, Period = BudgetPeriod.Monthly },
            new() { Scope = BudgetScope.Department, ScopeId = department.Id, LimitSek = 10000, Period = BudgetPeriod.Monthly },
        };
        var replacements = Enumerable.Range(0, RotatedKeys).ToDictionary(_ => Guid.NewGuid(), _ => Guid.NewGuid());

        _snapshot = new CatalogSnapshot(providers, [route], budgets, 10m, DateTimeOffset.UtcNow, replacements);
        _router = new RouteResolver(new InMemoryCircuitBreakerStore(TimeProvider.System, Options.Create(new CircuitBreakerOptions())));
        _model = _snapshot.Resolve("ume/chat")!;
        _hasher = new VirtualKeyHasher(new byte[32]);
        _presentedKey = _hasher.Generate().PlainText;
    }

    [Benchmark(Description = "Resolve model + select candidates")]
    public async Task<int> SelectCandidates()
    {
        var resolution = _router.ResolveModel(_snapshot, _key, GatewayEndpoint.ChatCompletions, "ume/chat");
        var selection = await _router.SelectCandidatesAsync([resolution.Model ?? _model], _key, GatewayEndpoint.ChatCompletions, null, stream: true, CancellationToken.None);
        return selection.Candidates.Count;
    }

    [Benchmark(Description = "Applicable budgets (key lineage + team + department)")]
    public int ApplicableBudgets() => BudgetService.ApplicableBudgets(_snapshot, _key).Count;

    [Benchmark(Description = "Hash presented key (HMAC-SHA256)")]
    public string HashKey() => _hasher.Hash(_presentedKey);
}
