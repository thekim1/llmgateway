using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;

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
