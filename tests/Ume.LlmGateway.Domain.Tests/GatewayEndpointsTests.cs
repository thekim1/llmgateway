using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.Domain.Tests;

public class GatewayEndpointsTests
{
    [Fact]
    public void Every_endpoint_has_an_entry_at_its_own_index()
    {
        foreach (var endpoint in Enum.GetValues<GatewayEndpoint>())
        {
            var info = GatewayEndpoints.Info(endpoint);
            info.ShouldNotBeNull();
            info.Endpoint.ShouldBe(endpoint);
            info.ClientPath.ShouldStartWith("/v1/");
            info.RoutingName.ShouldNotBeNullOrWhiteSpace();
        }

        GatewayEndpoints.All.Count.ShouldBe(Enum.GetValues<GatewayEndpoint>().Length);
        GatewayEndpoints.All.Select(e => e.RoutingName).ShouldBeUnique();
        GatewayEndpoints.All.Select(e => e.ClientPath).ShouldBeUnique();
        Should.Throw<ArgumentOutOfRangeException>(() => GatewayEndpoints.Info((GatewayEndpoint)999));
    }

    [Theory]
    [InlineData(GatewayEndpoint.ChatCompletions, "chat_completions", "/v1/chat/completions", "chat/completions")]
    [InlineData(GatewayEndpoint.Embeddings, "embeddings", "/v1/embeddings", "embeddings")]
    [InlineData(GatewayEndpoint.Responses, "responses", "/v1/responses", "responses")]
    [InlineData(GatewayEndpoint.AnthropicMessages, "anthropic_messages", "/v1/messages", "messages")]
    [InlineData(GatewayEndpoint.Models, "models", "/v1/models", null)]
    [InlineData(GatewayEndpoint.AudioTranscriptions, "audio_transcriptions", "/v1/audio/transcriptions", "audio/transcriptions")]
    [InlineData(GatewayEndpoint.AudioTranslations, "audio_translations", "/v1/audio/translations", "audio/translations")]
    [InlineData(GatewayEndpoint.Realtime, "realtime", "/v1/realtime", "realtime")]
    [InlineData(GatewayEndpoint.RealtimeTranslations, "realtime_translations", "/v1/realtime/translations", "realtime/translations")]
    public void Names_and_paths_are_the_documented_ones(GatewayEndpoint endpoint, string routingName, string clientPath, string? upstreamPath)
    {
        var info = GatewayEndpoints.Info(endpoint);
        info.RoutingName.ShouldBe(routingName);
        info.ClientPath.ShouldBe(clientPath);
        info.UpstreamPath.ShouldBe(upstreamPath);
    }

    [Theory]
    [InlineData(GatewayEndpoint.ChatCompletions, ProviderCapabilities.ChatCompletions)]
    [InlineData(GatewayEndpoint.Embeddings, ProviderCapabilities.Embeddings)]
    [InlineData(GatewayEndpoint.Responses, ProviderCapabilities.Responses)]
    [InlineData(GatewayEndpoint.AnthropicMessages, ProviderCapabilities.AnthropicMessages)]
    [InlineData(GatewayEndpoint.AudioTranscriptions, ProviderCapabilities.AudioTranscriptions)]
    [InlineData(GatewayEndpoint.AudioTranslations, ProviderCapabilities.AudioTranscriptions)]
    [InlineData(GatewayEndpoint.Realtime, ProviderCapabilities.Realtime)]
    [InlineData(GatewayEndpoint.RealtimeTranslations, ProviderCapabilities.Realtime)]
    public void Provider_support_follows_the_required_capability(GatewayEndpoint endpoint, ProviderCapabilities capability)
    {
        new ProviderAccount { Name = "p", BaseUrl = "http://x", Capabilities = capability }.Supports(endpoint).ShouldBeTrue();
        new ProviderAccount { Name = "p", BaseUrl = "http://x", Capabilities = ~capability & (ProviderCapabilities)0x7F }.Supports(endpoint).ShouldBeFalse();
    }

    [Fact]
    public void Models_endpoint_is_never_served_by_a_provider()
    {
        new ProviderAccount { Name = "p", BaseUrl = "http://x", Capabilities = (ProviderCapabilities)0x7F }.Supports(GatewayEndpoint.Models).ShouldBeFalse();
        new ProviderAccount { Name = "p", BaseUrl = "http://x", Capabilities = ProviderCapabilities.None }.Supports(GatewayEndpoint.Models).ShouldBeFalse();
    }

    [Theory]
    [InlineData(GatewayEndpoint.ChatCompletions, new[] { ModelKind.Chat })]
    [InlineData(GatewayEndpoint.Responses, new[] { ModelKind.Chat })]
    [InlineData(GatewayEndpoint.AnthropicMessages, new[] { ModelKind.Chat })]
    [InlineData(GatewayEndpoint.Embeddings, new[] { ModelKind.Embedding })]
    [InlineData(GatewayEndpoint.AudioTranscriptions, new[] { ModelKind.Transcription })]
    [InlineData(GatewayEndpoint.AudioTranslations, new[] { ModelKind.Transcription })]
    [InlineData(GatewayEndpoint.Realtime, new[] { ModelKind.Realtime, ModelKind.Transcription })]
    [InlineData(GatewayEndpoint.RealtimeTranslations, new[] { ModelKind.SpeechTranslation })]
    [InlineData(GatewayEndpoint.Models, new ModelKind[0])]
    public void Endpoints_serve_their_model_kinds(GatewayEndpoint endpoint, ModelKind[] kinds)
    {
        var info = GatewayEndpoints.Info(endpoint);
        foreach (var kind in Enum.GetValues<ModelKind>())
        {
            info.Serves(kind).ShouldBe(kinds.Contains(kind), $"{endpoint} / {kind}");
            GatewayEndpoints.ServingKind(kind).Contains(endpoint).ShouldBe(kinds.Contains(kind));
        }
    }

    [Fact]
    public void Flags_mark_audio_realtime_streaming_and_output()
    {
        GatewayEndpoints.All.Where(e => e.IsAudio).Select(e => e.Endpoint).ShouldBe([GatewayEndpoint.AudioTranscriptions, GatewayEndpoint.AudioTranslations]);
        GatewayEndpoints.All.Where(e => e.IsRealtime).Select(e => e.Endpoint).ShouldBe([GatewayEndpoint.Realtime, GatewayEndpoint.RealtimeTranslations]);
        GatewayEndpoints.Info(GatewayEndpoint.Embeddings).SupportsStreaming.ShouldBeFalse();
        GatewayEndpoints.Info(GatewayEndpoint.Embeddings).HasOutputTokens.ShouldBeFalse();
        GatewayEndpoints.Info(GatewayEndpoint.ChatCompletions).SupportsStreaming.ShouldBeTrue();
    }
}

public class ModelAllowListTests
{
    [Fact]
    public void Empty_allow_list_allows_every_model()
    {
        new VirtualKey { Name = "k", Prefix = "p", KeyHash = "h" }.IsModelAllowed("anything").ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Allow_list_is_case_insensitive_with_or_without_the_index(bool indexed)
    {
        var key = new VirtualKey { Name = "k", Prefix = "p", KeyHash = "h", AllowedModels = ["ume/Chat-Standard"] };
        if (indexed)
        {
            key.IndexAllowedModels();
        }

        key.IsModelAllowed("UME/chat-standard").ShouldBeTrue();
        key.IsModelAllowed("ume/chat-premium").ShouldBeFalse();
    }

    [Fact]
    public void Index_is_ignored_once_the_list_is_replaced_or_changed()
    {
        var key = new VirtualKey { Name = "k", Prefix = "p", KeyHash = "h", AllowedModels = ["a"] };
        key.IndexAllowedModels();
        key.AllowedModels = ["b"];
        key.IsModelAllowed("b").ShouldBeTrue();
        key.IsModelAllowed("a").ShouldBeFalse();

        key.IndexAllowedModels();
        key.AllowedModels.Add("c");
        key.IsModelAllowed("c").ShouldBeTrue();
    }
}

public class EligibilityTests
{
    private static ModelDeployment Deployment(ProviderCapabilities caps, bool enabled = true, bool drained = false, string name = "p")
    {
        var provider = new ProviderAccount { Name = name, BaseUrl = "http://x", Residency = DataResidency.Eu, Capabilities = caps, IsDrained = drained };
        return new ModelDeployment { Name = name + "/m", UpstreamModel = "m", IsEnabled = enabled, ProviderAccount = provider, ProviderAccountId = provider.Id };
    }

    [Fact]
    public void A_model_is_listable_when_some_endpoint_of_its_kind_is_supported()
    {
        var constraints = new RoutingConstraints(GatewayEndpoint.Models, null);
        RouteSelector.IsEligibleForAnyEndpoint(Deployment(ProviderCapabilities.Embeddings), ModelKind.Embedding, constraints).ShouldBeTrue();
        RouteSelector.IsEligibleForAnyEndpoint(Deployment(ProviderCapabilities.ChatCompletions), ModelKind.Embedding, constraints).ShouldBeFalse();
        RouteSelector.IsEligibleForAnyEndpoint(Deployment(ProviderCapabilities.Realtime), ModelKind.Transcription, constraints).ShouldBeTrue();
        RouteSelector.IsEligibleForAnyEndpoint(Deployment(ProviderCapabilities.Embeddings, drained: true), ModelKind.Embedding, constraints).ShouldBeFalse();
        RouteSelector.IsEligibleForAnyEndpoint(Deployment(ProviderCapabilities.Embeddings, enabled: false), ModelKind.Embedding, constraints).ShouldBeFalse();
    }

    [Fact]
    public void Deployment_eligibility_applies_availability_endpoint_and_allow_lists()
    {
        var chat = new RoutingConstraints(GatewayEndpoint.ChatCompletions, null);
        RouteSelector.IsEligible(Deployment(ProviderCapabilities.ChatCompletions), chat).ShouldBeTrue();
        RouteSelector.IsEligible(Deployment(ProviderCapabilities.ChatCompletions, drained: true), chat).ShouldBeFalse();
        RouteSelector.IsEligible(Deployment(ProviderCapabilities.Embeddings), chat).ShouldBeFalse();
        RouteSelector.IsEligible(Deployment(ProviderCapabilities.ChatCompletions, name: "other"), chat with { AllowedProviders = ["p"] }).ShouldBeFalse();
        RouteSelector.IsEligible(Deployment(ProviderCapabilities.ChatCompletions), chat with { AllowedResidencies = [DataResidency.OnPrem] }).ShouldBeFalse();
        RouteSelector.IsEligible((ModelDeployment?)null, chat).ShouldBeFalse();
    }
}
