using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Gateway.Requests;

namespace Ume.LlmGateway.Gateway;

/// <summary>
/// Maps one route per <see cref="GatewayEndpoints"/> entry at its <see cref="GatewayEndpointInfo.ClientPath"/>, so a path
/// is written in one place. Live sessions are WebSocket GETs (HTTP/2 extended CONNECT is presented as GET as well).
/// </summary>
public static class GatewayRoutes
{
    /// <summary>The OpenAPI summary of each endpoint; every endpoint in the table needs one.</summary>
    private static readonly Dictionary<GatewayEndpoint, string> Summaries = new()
    {
        [GatewayEndpoint.ChatCompletions] = "Chat completions (OpenAI-compatible, supports stream)",
        [GatewayEndpoint.Embeddings] = "Embeddings (OpenAI-compatible)",
        [GatewayEndpoint.Responses] = "Responses API (OpenAI-compatible; only providers that support it)",
        [GatewayEndpoint.AnthropicMessages] = "Anthropic Messages API (Anthropic providers only)",
        [GatewayEndpoint.AnthropicCountTokens] = "Anthropic token counting for a Messages request (Anthropic providers only; free)",
        [GatewayEndpoint.Models] = "Models and aliases the key may use",
        [GatewayEndpoint.AudioTranscriptions] = "Speech to text (OpenAI-compatible, multipart/form-data with the 'file' field)",
        [GatewayEndpoint.AudioTranslations] = "Speech to English text (OpenAI-compatible, multipart/form-data with the 'file' field)",
        [GatewayEndpoint.ImageGenerations] = "Image generation from text (OpenAI-compatible, JSON)",
        [GatewayEndpoint.ImageEdits] = "Image editing (OpenAI-compatible, multipart/form-data with the 'image' and optional 'mask' fields)",
        [GatewayEndpoint.Realtime] = "Live audio over WebSocket: live transcription and realtime conversation (OpenAI Realtime-compatible)",
        [GatewayEndpoint.Rerank] = "Rerank documents against a query (Cohere-compatible; only providers with the Rerank capability)",
        [GatewayEndpoint.RealtimeTranslations] = "Live interpretation over WebSocket (OpenAI Realtime translations-compatible)",
        [GatewayEndpoint.Context] = "What the key is and may use: its integration, assigned keys, teams and departments (docs/integrations.md)",
    };

    public static void MapGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        var llm = app.MapGroup(string.Empty).WithTags("LLM");
        foreach (var info in GatewayEndpoints.All.OfType<GatewayEndpointInfo>())
        {
            var endpoint = info.Endpoint;
            var route = endpoint == GatewayEndpoint.Models
                ? llm.MapGet(info.ClientPath, (HttpContext http, ModelListEndpoint h) => h.ListModelsAsync(http))
                : endpoint == GatewayEndpoint.Context
                ? llm.MapGet(info.ClientPath, (HttpContext http, ContextEndpoint h) => h.GetAsync(http))
                : info.IsRealtime
                    ? llm.MapGet(info.ClientPath, (HttpContext http, RealtimeRequestHandler h) => h.HandleAsync(http, endpoint))
                    : llm.MapPost(info.ClientPath, (HttpContext http, GatewayRequestHandler h) => h.HandleAsync(http, endpoint));
            route.WithSummary(Summaries[endpoint]);
            if (!info.IsRealtime)
            {
                route.Produces<JsonObject>(200);
            }
        }
    }
}
