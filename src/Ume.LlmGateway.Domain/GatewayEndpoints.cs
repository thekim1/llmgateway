namespace Ume.LlmGateway.Domain;

/// <summary>
/// What a <see cref="GatewayEndpoint"/> is: its client path, the name routing conditions see, the provider path and
/// capability it needs, and the kinds of model it serves. One entry per enum value (see <see cref="GatewayEndpoints"/>).
/// </summary>
/// <param name="Endpoint">The endpoint this entry describes.</param>
/// <param name="RoutingName">The value of the <c>endpoint</c> variable in routing conditions.</param>
/// <param name="ClientPath">The path clients call, e.g. <c>/v1/chat/completions</c>.</param>
/// <param name="UpstreamPath">Path appended to a provider's base URL; null when the endpoint never reaches a provider.</param>
/// <param name="RequiredCapability">What a provider must support to serve the endpoint; <see cref="ProviderCapabilities.None"/> = no provider can.</param>
/// <param name="IsAudio">A multipart speech-to-text upload instead of a JSON body.</param>
/// <param name="IsRealtime">A live WebSocket session.</param>
/// <param name="SupportsStreaming">A client may ask for a streamed (SSE) answer.</param>
/// <param name="HasOutputTokens">The answer is billed for output tokens (embeddings are not).</param>
/// <param name="ModelKinds">The kinds of model the endpoint serves; none when it never reaches a provider.</param>
public sealed record GatewayEndpointInfo(
    GatewayEndpoint Endpoint,
    string RoutingName,
    string ClientPath,
    string? UpstreamPath,
    ProviderCapabilities RequiredCapability,
    bool IsAudio,
    bool IsRealtime,
    bool SupportsStreaming,
    bool HasOutputTokens,
    params ModelKind[] ModelKinds)
{
    private readonly int _kindMask = ModelKinds.Aggregate(0, (mask, kind) => mask | (1 << (int)kind));

    /// <summary>Whether a model of <paramref name="kind"/> can be used with this endpoint.</summary>
    public bool Serves(ModelKind kind) => (_kindMask & (1 << (int)kind)) != 0;

    /// <summary>Whether a provider with <paramref name="capabilities"/> can serve this endpoint.</summary>
    public bool IsSupportedBy(ProviderCapabilities capabilities) =>
        RequiredCapability != ProviderCapabilities.None && (capabilities & RequiredCapability) == RequiredCapability;
}

/// <summary>The one table of endpoint semantics, indexed by enum value: lookups are an array access.</summary>
public static class GatewayEndpoints
{
    private static readonly GatewayEndpointInfo[] Table = Build(
    [
        new(GatewayEndpoint.ChatCompletions, "chat_completions", "/v1/chat/completions", "chat/completions", ProviderCapabilities.ChatCompletions,
            IsAudio: false, IsRealtime: false, SupportsStreaming: true, HasOutputTokens: true, ModelKind.Chat),
        new(GatewayEndpoint.Embeddings, "embeddings", "/v1/embeddings", "embeddings", ProviderCapabilities.Embeddings,
            IsAudio: false, IsRealtime: false, SupportsStreaming: false, HasOutputTokens: false, ModelKind.Embedding),
        new(GatewayEndpoint.Responses, "responses", "/v1/responses", "responses", ProviderCapabilities.Responses,
            IsAudio: false, IsRealtime: false, SupportsStreaming: true, HasOutputTokens: true, ModelKind.Chat),
        new(GatewayEndpoint.AnthropicMessages, "anthropic_messages", "/v1/messages", "messages", ProviderCapabilities.AnthropicMessages,
            IsAudio: false, IsRealtime: false, SupportsStreaming: true, HasOutputTokens: true, ModelKind.Chat),
        new(GatewayEndpoint.Models, "models", "/v1/models", null, ProviderCapabilities.None,
            IsAudio: false, IsRealtime: false, SupportsStreaming: false, HasOutputTokens: false),
        new(GatewayEndpoint.AudioTranscriptions, "audio_transcriptions", "/v1/audio/transcriptions", "audio/transcriptions", ProviderCapabilities.AudioTranscriptions,
            IsAudio: true, IsRealtime: false, SupportsStreaming: true, HasOutputTokens: true, ModelKind.Transcription),
        new(GatewayEndpoint.AudioTranslations, "audio_translations", "/v1/audio/translations", "audio/translations", ProviderCapabilities.AudioTranscriptions,
            IsAudio: true, IsRealtime: false, SupportsStreaming: true, HasOutputTokens: true, ModelKind.Transcription),

        // Live transcription uses speech-to-text models on /v1/realtime.
        new(GatewayEndpoint.Realtime, "realtime", "/v1/realtime", "realtime", ProviderCapabilities.Realtime,
            IsAudio: false, IsRealtime: true, SupportsStreaming: false, HasOutputTokens: true, ModelKind.Realtime, ModelKind.Transcription),
        new(GatewayEndpoint.RealtimeTranslations, "realtime_translations", "/v1/realtime/translations", "realtime/translations", ProviderCapabilities.Realtime,
            IsAudio: false, IsRealtime: true, SupportsStreaming: false, HasOutputTokens: true, ModelKind.SpeechTranslation),
    ]);

    private static readonly GatewayEndpoint[][] ByKind = BuildByKind();

    /// <summary>Every endpoint, in enum order.</summary>
    public static IReadOnlyList<GatewayEndpointInfo> All => Table;

    public static GatewayEndpointInfo Info(GatewayEndpoint endpoint) =>
        (uint)endpoint < (uint)Table.Length ? Table[(int)endpoint] : throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "Unknown gateway endpoint.");

    /// <summary>The endpoints a model of <paramref name="kind"/> can be used with.</summary>
    public static IReadOnlyList<GatewayEndpoint> ServingKind(ModelKind kind) =>
        (uint)kind < (uint)ByKind.Length ? ByKind[(int)kind] : [];

    private static GatewayEndpointInfo[] Build(GatewayEndpointInfo[] entries)
    {
        var table = new GatewayEndpointInfo[entries.Max(e => (int)e.Endpoint) + 1];
        foreach (var entry in entries)
        {
            table[(int)entry.Endpoint] = entry;
        }

        return table;
    }

    private static GatewayEndpoint[][] BuildByKind()
    {
        var byKind = new GatewayEndpoint[Enum.GetValues<ModelKind>().Max(k => (int)k) + 1][];
        for (var kind = 0; kind < byKind.Length; kind++)
        {
            byKind[kind] = [.. Table.Where(e => e is not null && e.Serves((ModelKind)kind)).Select(e => e.Endpoint)];
        }

        return byKind;
    }
}
