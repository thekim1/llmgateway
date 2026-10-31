using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Routing;

namespace Ume.LlmGateway.Domain.Tests;

public class RouteSelectorTests
{
    private static RouteTarget Target(string name, int priority, int weight = 1, DataResidency residency = DataResidency.Eu,
        ProviderCapabilities caps = ProviderCapabilities.ChatCompletions | ProviderCapabilities.Streaming, bool enabled = true, bool drained = false)
    {
        var provider = new ProviderAccount
        {
            Name = name,
            BaseUrl = "http://x",
            Type = ProviderType.OpenAICompatible,
            Residency = residency,
            Capabilities = caps,
            IsEnabled = enabled,
            IsDrained = drained,
        };
        var deployment = new ModelDeployment { Name = name, UpstreamModel = name, ProviderAccount = provider, ProviderAccountId = provider.Id };
        return new RouteTarget { Priority = priority, Weight = weight, ModelDeployment = deployment, ModelDeploymentId = deployment.Id };
    }

    private static readonly RoutingConstraints Chat = new(GatewayEndpoint.ChatCompletions, null);

    [Fact]
    public void Orders_by_priority_for_fallback()
    {
        var primary = Target("primary", 0);
        var secondary = Target("secondary", 1);
        var tertiary = Target("tertiary", 2);

        RouteSelector.Order([tertiary, primary, secondary], Chat, new Random(1))
            .ShouldBe([primary, secondary, tertiary]);
    }

    [Fact]
    public void Weighted_load_balancing_within_same_priority()
    {
        var heavy = Target("heavy", 0, weight: 9);
        var light = Target("light", 0, weight: 1);
        var random = new Random(42);

        var firstPicks = Enumerable.Range(0, 10_000)
            .Select(_ => RouteSelector.Order([heavy, light], Chat, random)[0])
            .Count(t => t == heavy);

        firstPicks.ShouldBeInRange(8_700, 9_300);
    }

    [Fact]
    public void Least_loaded_prefers_the_better_score_but_keeps_the_worse_reachable()
    {
        var fast = Target("fast", 0);
        var slow = Target("slow", 0);
        var backup = Target("backup", 1);
        var random = new Random(7);
        double Score(RouteTarget t) => t == fast ? 0.9 : 0.1;

        var orders = Enumerable.Range(0, 10_000).Select(_ => RouteSelector.Order([slow, fast, backup], Chat, random, RouteSelector.PowerOfTwoChoices(Score))).ToList();

        // Two draws with replacement: the slow one is first only when it is drawn twice (about a quarter of the time).
        orders.Count(o => o[0] == fast).ShouldBeInRange(7_200, 7_800);
        orders.ShouldAllBe(o => o.Count == 3 && o[2] == backup); // priorities still decide the fallback chain
    }

    [Fact]
    public void Affinity_keeps_a_session_on_one_target_and_spreads_sessions_by_weight()
    {
        var a = Target("a", 0, weight: 3);
        var b = Target("b", 0, weight: 1);
        var backup = Target("backup", 1);
        var random = new Random(3);

        var session = RouteSelector.Rendezvous(RouteSelector.AffinityHash("conversation-42"));
        var first = RouteSelector.Order([a, b, backup], Chat, random, session)[0];
        for (var i = 0; i < 50; i++)
        {
            RouteSelector.Order(i % 2 == 0 ? [b, a, backup] : [a, b, backup], Chat, random, session)[0].ShouldBeSameAs(first); // input order does not matter
        }

        var toA = Enumerable.Range(0, 4_000)
            .Count(i => RouteSelector.Order([a, b, backup], Chat, random, RouteSelector.Rendezvous(RouteSelector.AffinityHash("s" + i)))[0] == a);
        toA.ShouldBeInRange(2_800, 3_200); // about 3 in 4

        RouteSelector.AffinityHash("x").ShouldBe(RouteSelector.AffinityHash("x"));
        RouteSelector.AffinityHash("x").ShouldNotBe(RouteSelector.AffinityHash("y"));
    }

    [Fact]
    public void Filters_disabled_drained_and_unsupported_targets()
    {
        var ok = Target("ok", 0);
        var disabled = Target("disabled", 0, enabled: false);
        var drained = Target("drained", 0, drained: true);
        var embeddingsOnly = Target("emb", 0, caps: ProviderCapabilities.Embeddings);

        RouteSelector.Order([ok, disabled, drained, embeddingsOnly], Chat, new Random(1)).ShouldBe([ok]);
    }

    [Fact]
    public void Residency_constraint_filters_targets()
    {
        var onPrem = Target("ollama", 1, residency: DataResidency.OnPrem);
        var cloud = Target("openai", 0, residency: DataResidency.External);

        var result = RouteSelector.Order([cloud, onPrem], new RoutingConstraints(GatewayEndpoint.ChatCompletions, [DataResidency.OnPrem]), new Random(1));
        result.ShouldBe([onPrem]);
    }

    [Fact]
    public void Unhealthy_providers_are_tried_last()
    {
        var primary = Target("primary", 0);
        var secondary = Target("secondary", 1);
        var unhealthy = primary.ModelDeployment!.ProviderAccountId;

        var result = RouteSelector.Order([primary, secondary], Chat with { IsProviderHealthy = id => id != unhealthy }, new Random(1));
        result.ShouldBe([secondary, primary]);
    }

    [Fact]
    public void Effective_residencies_never_widen_access()
    {
        RouteSelector.EffectiveResidencies([], null).ShouldBeNull();
        RouteSelector.EffectiveResidencies([DataResidency.Eu], null).ShouldBe([DataResidency.Eu]);
        RouteSelector.EffectiveResidencies([], DataResidency.OnPrem).ShouldBe([DataResidency.OnPrem]);
        RouteSelector.EffectiveResidencies([DataResidency.Eu, DataResidency.OnPrem], DataResidency.OnPrem).ShouldBe([DataResidency.OnPrem]);
        RouteSelector.EffectiveResidencies([DataResidency.Eu], DataResidency.OnPrem)!.ShouldBeEmpty();
    }

    [Fact]
    public void Provider_allowlist_removes_other_providers_and_empty_means_all()
    {
        var a = Target("a", 0);
        var b = Target("b", 1);
        var c = Target("c", 2);

        RouteSelector.Order([a, b, c], Chat with { AllowedProviders = ["b", "C"] }, new Random(1)).ShouldBe([b, c]);
        RouteSelector.Order([a, b, c], Chat with { AllowedProviders = [] }, new Random(1)).ShouldBe([a, b, c]);
        RouteSelector.Order([a, b, c], Chat with { AllowedProviders = null }, new Random(1)).ShouldBe([a, b, c]);
        RouteSelector.Order([a, b, c], Chat with { AllowedProviders = ["missing"] }, new Random(1)).ShouldBeEmpty();
    }
}
