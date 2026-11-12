namespace Ume.LlmGateway.Domain;

public enum GatewayEndpoint
{
    ChatCompletions = 0,
    Embeddings = 1,
    Responses = 2,
    AnthropicMessages = 3,

    /// <summary><c>GET /v1/models</c>. Never sent to a provider; only appears in authentication failures.</summary>
    Models = 4,

    /// <summary><c>POST /v1/audio/transcriptions</c>: speech to text in the spoken language (multipart upload).</summary>
    AudioTranscriptions = 5,

    /// <summary><c>POST /v1/audio/translations</c>: speech to English text (multipart upload).</summary>
    AudioTranslations = 6,

    /// <summary><c>GET /v1/realtime</c> (WebSocket): live transcription and realtime conversation sessions.</summary>
    Realtime = 7,

    /// <summary><c>GET /v1/realtime/translations</c> (WebSocket): live speech translation (interpreting).</summary>
    RealtimeTranslations = 8,

    /// <summary><c>POST /v1/images/generations</c>: an image from a text prompt (JSON body).</summary>
    ImageGenerations = 9,

    /// <summary><c>POST /v1/images/edits</c>: edits or combines uploaded images from a prompt (multipart upload).</summary>
    ImageEdits = 10,

    /// <summary><c>POST /v1/messages/count_tokens</c>: the input tokens of an Anthropic Messages request (free, not billed).</summary>
    AnthropicCountTokens = 11,

    /// <summary>
    /// <c>POST /v1/rerank</c>: scores documents against a query (Cohere-compatible JSON, as Berget AI, Cohere, vLLM,
    /// Infinity and Jina serve it). Optional: used only when a provider has the <c>Rerank</c> capability.
    /// </summary>
    Rerank = 12,

    /// <summary>
    /// <c>GET /v1/ume/context</c>: what the key is and may use (its integration, teams, assigned keys; docs/integrations.md).
    /// Never sent to a provider; only appears in authentication failures.
    /// </summary>
    Context = 13,
}
