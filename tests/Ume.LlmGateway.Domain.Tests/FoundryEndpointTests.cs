using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;

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
}
