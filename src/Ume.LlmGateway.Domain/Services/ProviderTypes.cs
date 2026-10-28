namespace Ume.LlmGateway.Domain.Services;

/// <summary>
/// What each provider type serves. The type fixes the API, so admins choose endpoints only for an OpenAI-compatible server,
/// whose endpoints depend on the software behind it (vLLM, LiteLLM, an on-prem speech server). Which model gets which
/// endpoint is then decided by the model's kind (<see cref="GatewayEndpointInfo.Serves"/>).
/// </summary>
public static class ProviderTypes
{
    /// <summary>The full OpenAI API: OpenAI itself, Azure OpenAI and Azure AI Foundry (Claude on Foundry: see <see cref="FoundryEndpoint"/>).</summary>
    private const ProviderCapabilities OpenAIApi =
        ProviderCapabilities.ChatCompletions | ProviderCapabilities.Embeddings | ProviderCapabilities.Responses | ProviderCapabilities.Streaming |
        ProviderCapabilities.AudioTranscriptions | ProviderCapabilities.Realtime | ProviderCapabilities.Images;

    /// <summary>Anthropic's Messages API; the gateway translates Chat Completions to it.</summary>
    public const ProviderCapabilities AnthropicApi =
        ProviderCapabilities.ChatCompletions | ProviderCapabilities.AnthropicMessages | ProviderCapabilities.Streaming;

    /// <summary>What a new OpenAI-compatible server is assumed to offer until the admin says otherwise.</summary>
    public const ProviderCapabilities CompatibleDefault =
        ProviderCapabilities.ChatCompletions | ProviderCapabilities.Streaming | ProviderCapabilities.Embeddings;

    /// <summary>The type's capabilities, or null for an OpenAI-compatible server (the admin chooses).</summary>
    public static ProviderCapabilities? Fixed(ProviderType type) => type switch
    {
        ProviderType.OpenAI or ProviderType.AzureOpenAI or ProviderType.AzureAIFoundry => OpenAIApi,
        ProviderType.Anthropic => AnthropicApi,
        ProviderType.Ollama => ProviderCapabilities.ChatCompletions | ProviderCapabilities.Embeddings | ProviderCapabilities.Streaming,
        ProviderType.OllamaCloud => ProviderCapabilities.ChatCompletions | ProviderCapabilities.Streaming,
        _ => null,
    };

    /// <summary>What a provider of <paramref name="type"/> serves: fixed by the type, else <paramref name="chosen"/>.</summary>
    public static ProviderCapabilities Capabilities(ProviderType type, ProviderCapabilities chosen) => Fixed(type) ?? chosen;
}
