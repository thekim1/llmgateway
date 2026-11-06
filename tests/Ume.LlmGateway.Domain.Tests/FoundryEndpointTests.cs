using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Providers;

namespace Ume.LlmGateway.Domain.Tests;

public class FoundryEndpointTests
{
    private static ProviderAccount Foundry(string baseUrl = "https://res.services.ai.azure.com/api/projects/umea") => new()
    {
        Id = Guid.NewGuid(), Name = "foundry", Type = ProviderType.AzureAIFoundry, BaseUrl = baseUrl, AuthMode = ProviderAuthMode.ApiKeyHeader,
        EncryptedCredential = "protected", Residency = DataResidency.Eu, TimeoutSeconds = 60, IsDrained = true,
        Capabilities = ProviderCapabilities.ChatCompletions | ProviderCapabilities.Streaming | ProviderCapabilities.Embeddings | ProviderCapabilities.Responses,
    };

    [Theory]
    [InlineData("claude-sonnet-4-5", null, true)]
    [InlineData("prod-claude", "claude-opus-4-5", true)]
    [InlineData("claude-alias", "gpt-4.1", false)]
    [InlineData("gpt-4.1", null, false)]
    public void Claude_on_a_Foundry_provider_is_known_by_its_base_model(string deployment, string? baseModel, bool claude) =>
        FoundryEndpoint.IsClaude(Foundry(), new ModelDeployment { Name = deployment, UpstreamModel = deployment, BaseModel = baseModel }).ShouldBe(claude);

    [Fact]
    public void Claude_on_another_provider_type_is_not_rerouted()
    {
        var anthropic = Foundry();
        anthropic.Type = ProviderType.Anthropic;
        FoundryEndpoint.IsClaude(anthropic, new ModelDeployment { Name = "claude-sonnet-4-5", UpstreamModel = "claude-sonnet-4-5" }).ShouldBeFalse();
    }

    [Fact]
    public void Claude_can_be_called_for_Messages_whatever_the_Foundry_provider_ticks()
    {
        var foundry = Foundry(); // the stored boxes do not matter for a Foundry provider
        var claude = new ModelDeployment { Name = "claude-sonnet-4-5", UpstreamModel = "claude-sonnet-4-5" };
        var gpt = new ModelDeployment { Name = "gpt-4.1", UpstreamModel = "gpt-4.1" };
        FoundryEndpoint.Capabilities(foundry, claude).ShouldBe(ProviderCapabilities.ChatCompletions | ProviderCapabilities.AnthropicMessages | ProviderCapabilities.Streaming);
        FoundryEndpoint.Capabilities(foundry, gpt).ShouldBe(foundry.SupportedCapabilities);
        foundry.SupportedCapabilities.HasFlag(ProviderCapabilities.Realtime).ShouldBeTrue(); // the full OpenAI API, whatever is stored
        FoundryEndpoint.AnthropicView(foundry).Supports(GatewayEndpoint.AnthropicMessages).ShouldBeTrue();
    }

    [Theory]
    [InlineData("https://res.services.ai.azure.com/api/projects/umea")]
    [InlineData("https://res.services.ai.azure.com/openai/v1")]
    [InlineData("https://res.openai.azure.com/openai/v1?api-version=preview")]
    [InlineData("https://res.cognitiveservices.azure.com")]
    public void Anthropic_view_is_the_same_provider_on_the_resources_Anthropic_API(string baseUrl)
    {
        var foundry = Foundry(baseUrl);
        var view = FoundryEndpoint.AnthropicView(foundry);

        view.BaseUrl.ShouldBe("https://res.services.ai.azure.com/anthropic/v1");
        view.Type.ShouldBe(ProviderType.Anthropic);
        // Same provider for credentials, health, circuit, usage and the key's provider allow-list.
        (view.Id, view.Name, view.EncryptedCredential, view.Residency, view.TimeoutSeconds, view.IsDrained)
            .ShouldBe((foundry.Id, foundry.Name, foundry.EncryptedCredential, foundry.Residency, foundry.TimeoutSeconds, foundry.IsDrained));
        view.Capabilities.ShouldBe(ProviderCapabilities.ChatCompletions | ProviderCapabilities.AnthropicMessages | ProviderCapabilities.Streaming);
        foundry.BaseUrl.ShouldBe(baseUrl); // the provider itself is unchanged
    }

    [Theory]
    [InlineData(ProviderAuthMode.ApiKeyHeader, ProviderAuthMode.XApiKeyHeader)] // the Anthropic API answers 401 to api-key
    [InlineData(ProviderAuthMode.XApiKeyHeader, ProviderAuthMode.XApiKeyHeader)]
    [InlineData(ProviderAuthMode.Bearer, ProviderAuthMode.Bearer)]
    public void Anthropic_view_sends_the_key_where_the_Anthropic_API_reads_it(ProviderAuthMode foundryAuth, ProviderAuthMode viewAuth)
    {
        var foundry = Foundry();
        foundry.AuthMode = foundryAuth;
        FoundryEndpoint.AnthropicView(foundry).AuthMode.ShouldBe(viewAuth);
    }

    private static ModelDeployment Reranker(string? publisher = "Cohere", string? requestBaseUrl = null) => new()
    {
        Name = "Cohere-rerank-v5-fast", UpstreamModel = "Cohere-rerank-v5-fast", Kind = ModelKind.Rerank, Publisher = publisher, RequestBaseUrl = requestBaseUrl,
    };

    [Fact]
    public void A_reranker_is_a_Foundry_deployment_of_kind_Rerank()
    {
        FoundryEndpoint.IsRerank(Foundry(), Reranker()).ShouldBeTrue();
        FoundryEndpoint.IsRerank(Foundry(), new ModelDeployment { Name = "rerank-ish", UpstreamModel = "rerank-ish", Kind = ModelKind.Chat }).ShouldBeFalse();
        var compatible = Foundry();
        compatible.Type = ProviderType.OpenAICompatible;
        FoundryEndpoint.IsRerank(compatible, Reranker()).ShouldBeFalse(); // ticks Rerank itself, as before
    }

    [Theory]
    [InlineData("https://res.services.ai.azure.com/api/projects/umea", "Cohere", "https://res.services.ai.azure.com/providers/cohere/v2")]
    [InlineData("https://res.services.ai.azure.com/openai/v1", "Cohere", "https://res.services.ai.azure.com/providers/cohere/v2")]
    [InlineData("https://res.openai.azure.com/openai/v1?api-version=preview", "Cohere", "https://res.services.ai.azure.com/providers/cohere/v2")]
    [InlineData("https://res.cognitiveservices.azure.com", "cohere", "https://res.services.ai.azure.com/providers/cohere/v2")]
    [InlineData("https://res.services.ai.azure.com/api/projects/umea", "Jina AI", "https://res.services.ai.azure.com/providers/jina%20ai/v2")] // any publisher: data, not code
    [InlineData("https://res.services.ai.azure.com/api/projects/umea", "NewRanker", "https://res.services.ai.azure.com/providers/newranker/v2")]
    public void The_rerank_address_comes_from_the_deployments_publisher(string baseUrl, string publisher, string expected) =>
        FoundryEndpoint.RerankBaseUrl(Foundry(baseUrl), Reranker(publisher)).ShouldBe(expected);

    [Theory]
    [InlineData("https://res.services.ai.azure.com/providers/cohere/v2/", "https://res.services.ai.azure.com/providers/cohere/v2")]
    [InlineData("https://res.openai.azure.com/v1/rerankers?api-version=2025-01-01", "https://res.openai.azure.com/v1/rerankers?api-version=2025-01-01")] // same resource, other host name
    [InlineData("https://other.services.ai.azure.com/providers/cohere/v2", null)] // another resource would get the key
    [InlineData("http://res.services.ai.azure.com/providers/cohere/v2", null)]
    [InlineData("https://res.services.ai.azure.com:8443/providers/cohere/v2", null)]
    [InlineData("https://user:pw@res.services.ai.azure.com/providers/cohere/v2", null)]
    [InlineData("not an address", null)]
    public void The_address_set_on_the_deployment_wins_when_it_is_on_the_resource(string requestBaseUrl, string? expected) =>
        FoundryEndpoint.RerankBaseUrl(Foundry(), Reranker("Cohere", requestBaseUrl)).ShouldBe(expected);

    [Fact]
    public void A_reranker_without_publisher_or_address_has_no_address_and_no_capability()
    {
        var foundry = Foundry();
        FoundryEndpoint.RerankBaseUrl(foundry, Reranker(publisher: null)).ShouldBeNull();
        FoundryEndpoint.RerankView(foundry, Reranker(publisher: " ")).ShouldBeNull();
        FoundryEndpoint.Capabilities(foundry, Reranker(publisher: null)).ShouldBe(ProviderCapabilities.None);
        FoundryEndpoint.Routed(foundry, Reranker(publisher: null)).ShouldBeSameAs(foundry); // which serves no Rerank
        foundry.Supports(GatewayEndpoint.Rerank).ShouldBeFalse();
    }

    [Fact]
    public void Rerank_view_is_the_same_provider_at_the_publishers_address()
    {
        var foundry = Foundry();
        var view = FoundryEndpoint.RerankView(foundry, Reranker()).ShouldNotBeNull();

        view.BaseUrl.ShouldBe("https://res.services.ai.azure.com/providers/cohere/v2");
        (view.Id, view.Name, view.EncryptedCredential, view.Residency, view.TimeoutSeconds, view.IsDrained, view.IsEnabled)
            .ShouldBe((foundry.Id, foundry.Name, foundry.EncryptedCredential, foundry.Residency, foundry.TimeoutSeconds, foundry.IsDrained, foundry.IsEnabled));
        view.AuthMode.ShouldBe(ProviderAuthMode.ApiKeyHeader); // the provider's api-key header
        view.SupportedCapabilities.ShouldBe(ProviderCapabilities.Rerank);
        view.Supports(GatewayEndpoint.Rerank).ShouldBeTrue();
        view.Supports(GatewayEndpoint.ChatCompletions).ShouldBeFalse();
        FoundryEndpoint.Capabilities(foundry, Reranker()).ShouldBe(ProviderCapabilities.Rerank);
        foundry.BaseUrl.ShouldBe("https://res.services.ai.azure.com/api/projects/umea"); // the provider itself is unchanged
    }

    [Theory]
    [InlineData(null, ProviderAuthMode.ApiKeyHeader)]
    [InlineData(ProviderAuthMode.Bearer, ProviderAuthMode.Bearer)]
    public void Rerank_view_sends_the_key_as_the_deployment_says_else_as_the_provider(ProviderAuthMode? requestAuth, ProviderAuthMode expected)
    {
        var reranker = Reranker();
        reranker.RequestAuthMode = requestAuth;
        FoundryEndpoint.RerankView(Foundry(), reranker)!.AuthMode.ShouldBe(expected);
    }

    [Fact]
    public void A_chat_model_on_the_same_provider_is_routed_through_the_provider_itself()
    {
        var foundry = Foundry();
        var gpt = new ModelDeployment { Name = "gpt-4.1", UpstreamModel = "gpt-4.1" };
        FoundryEndpoint.Routed(foundry, gpt).ShouldBeSameAs(foundry);
        FoundryEndpoint.Capabilities(foundry, gpt).ShouldBe(foundry.SupportedCapabilities);
        FoundryEndpoint.Routed(foundry, new ModelDeployment { Name = "claude-sonnet-4-5", UpstreamModel = "claude-sonnet-4-5" }).Type.ShouldBe(ProviderType.Anthropic);
    }

    [Theory]
    [InlineData(null, DataResidency.Eu)]
    [InlineData(DataResidency.Eu, DataResidency.Eu)]
    [InlineData(DataResidency.External, DataResidency.External)]
    [InlineData(DataResidency.OnPrem, DataResidency.OnPrem)]
    public void A_deployments_own_residency_overrides_the_providers(DataResidency? own, DataResidency expected)
    {
        var foundry = Foundry(); // Eu
        foreach (var deployment in new[]
        {
            new ModelDeployment { Name = "gpt-4.1", UpstreamModel = "gpt-4.1", Residency = own },
            new ModelDeployment { Name = "claude-sonnet-4-5", UpstreamModel = "claude-sonnet-4-5", Residency = own },
            new ModelDeployment { Name = "rerank", UpstreamModel = "rerank", Kind = ModelKind.Rerank, Publisher = "Cohere", Residency = own },
        })
        {
            var routed = FoundryEndpoint.Routed(foundry, deployment);
            routed.Residency.ShouldBe(expected);
            deployment.EffectiveResidency(foundry).ShouldBe(expected);
            routed.Id.ShouldBe(foundry.Id);
            routed.Supports(deployment.Kind == ModelKind.Rerank ? GatewayEndpoint.Rerank : GatewayEndpoint.ChatCompletions).ShouldBeTrue();
        }

        foundry.Residency.ShouldBe(DataResidency.Eu); // the provider itself is unchanged
    }
}
