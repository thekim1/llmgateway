namespace Ume.LlmGateway.Domain;

/// <summary>Where a provider processes data. Drives PII routing and key residency restrictions.</summary>
public enum DataResidency
{
    /// <summary>Runs on Umeå kommun's own infrastructure (e.g. Ollama, vLLM).</summary>
    OnPrem = 0,

    /// <summary>External provider contractually processing data inside the EU/EEA (e.g. Azure Sweden Central).</summary>
    Eu = 1,

    /// <summary>External provider that may process data outside the EU/EEA.</summary>
    External = 2,
}

public enum ProviderType
{
    OpenAI = 0,
    AzureOpenAI = 1,
    AzureAIFoundry = 2,
    Anthropic = 3,
    Ollama = 4,
    OllamaCloud = 5,
    OpenAICompatible = 6,
}

public enum ProviderAuthMode
{
    None = 0,

    /// <summary>Authorization: Bearer &lt;credential&gt;.</summary>
    Bearer = 1,

    /// <summary>api-key: &lt;credential&gt; (Azure OpenAI / Azure AI Foundry).</summary>
    ApiKeyHeader = 2,

    /// <summary>x-api-key: &lt;credential&gt; (Anthropic).</summary>
    XApiKeyHeader = 3,
}

[Flags]
public enum ProviderCapabilities
{
    None = 0,
    ChatCompletions = 1,
    Embeddings = 2,
    Responses = 4,
    AnthropicMessages = 8,
    Streaming = 16,

    /// <summary><c>/audio/transcriptions</c> and <c>/audio/translations</c> (speech to text).</summary>
    AudioTranscriptions = 32,

    /// <summary>Live audio over WebSocket: <c>/realtime</c> and <c>/realtime/translations</c> (OpenAI Realtime protocol).</summary>
    Realtime = 64,

    /// <summary><c>/images/generations</c> and <c>/images/edits</c> (text to image, image editing).</summary>
    Images = 128,
}

public enum ModelKind
{
    Chat = 0,
    Embedding = 1,

    /// <summary>Speech to text (Whisper, gpt-4o-transcribe, KB-Whisper …); used by the audio endpoints and live transcription.</summary>
    Transcription = 2,

    /// <summary>Realtime conversation models (gpt-realtime …) on <c>/v1/realtime</c>.</summary>
    Realtime = 3,

    /// <summary>Live speech translation models (gpt-realtime-translate …) on <c>/v1/realtime/translations</c>.</summary>
    SpeechTranslation = 4,

    /// <summary>Image generation and editing (gpt-image-1, DALL·E, FLUX …) on <c>/v1/images/generations</c> and <c>/v1/images/edits</c>.</summary>
    Image = 5,
}

/// <summary>
/// How request parameters must be adapted for a model family. Data-driven so new families
/// (e.g. GPT-6) are configuration, not code.
/// </summary>
public enum ParameterProfile
{
    /// <summary>Pass parameters through unchanged.</summary>
    Standard = 0,

    /// <summary>
    /// Reasoning-style OpenAI models (o-series, GPT-5/6 families): use <c>max_completion_tokens</c>
    /// instead of <c>max_tokens</c> and drop sampling parameters they reject.
    /// </summary>
    OpenAIReasoning = 1,
}
