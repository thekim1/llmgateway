using System.IO.Pipelines;
using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Providers;

namespace Ume.LlmGateway.Benchmarks;

/// <summary>
/// The provider leg of a request: serialise the body, call upstream, read the answer (JSON or SSE) and write it to
/// the client. Upstream is an in-process stub, so the numbers are the gateway's CPU and allocation cost only.
/// </summary>
[MemoryDiagnoser]
public class ProviderBenchmarks
{
    private OpenAICompatibleAdapter _openai = null!;
    private AnthropicAdapter _anthropic = null!;
    private ProviderAccount _openaiProvider = null!;
    private ProviderAccount _anthropicProvider = null!;
    private JsonObject _chat = null!;
    private JsonObject _chatStream = null!;
    private JsonObject _embeddings = null!;

    [GlobalSetup]
    public void Setup()
    {
        var http = new ProviderHttpClient(new StubUpstream());
        _openai = new OpenAICompatibleAdapter(http);
        _anthropic = new AnthropicAdapter(http, TimeProvider.System);
        _openaiProvider = Provider(ProviderType.OpenAICompatible);
        _anthropicProvider = Provider(ProviderType.Anthropic);
        _chat = (JsonObject)JsonNode.Parse(Payloads.ChatRequest("bench-chat"))!;
        _chatStream = (JsonObject)JsonNode.Parse(Payloads.ChatRequest("bench-chat", stream: true))!;
        _embeddings = (JsonObject)JsonNode.Parse(Payloads.EmbeddingsRequest("bench-embed"))!;
    }

    [Benchmark(Description = "Chat JSON (OpenAI-compatible)")]
    public Task ChatJson() => SendAsync(_openai, _openaiProvider, GatewayEndpoint.ChatCompletions, _chat, stream: false);

    [Benchmark(Description = "Embeddings 16x1536 (OpenAI-compatible)")]
    public Task EmbeddingsJson() => SendAsync(_openai, _openaiProvider, GatewayEndpoint.Embeddings, _embeddings, stream: false);

    [Benchmark(Description = "Chat stream 300 chunks (OpenAI-compatible)")]
    public Task ChatStream() => SendAsync(_openai, _openaiProvider, GatewayEndpoint.ChatCompletions, _chatStream, stream: true);

    [Benchmark(Description = "Messages stream 300 chunks (Anthropic passthrough)")]
    public Task AnthropicPassthroughStream() => SendAsync(_anthropic, _anthropicProvider, GatewayEndpoint.AnthropicMessages, _chatStream, stream: true);

    [Benchmark(Description = "Chat stream 300 chunks (Anthropic translated)")]
    public Task AnthropicTranslatedStream() => SendAsync(_anthropic, _anthropicProvider, GatewayEndpoint.ChatCompletions, _chatStream, stream: true);

    private static async Task SendAsync(IProviderAdapter adapter, ProviderAccount provider, GatewayEndpoint endpoint, JsonObject body, bool stream)
    {
        var call = new ProviderCall(provider, provider.Deployments[0], endpoint, body, stream, "secret", false);
        var result = await adapter.SendAsync(call, CancellationToken.None);
        var client = PipeWriter.Create(Stream.Null);
        switch (result)
        {
            case ProviderJsonResult json:
                // Mirrors GatewayRequestHandler: the provider's bytes are written back to the client.
                using (json)
                {
                    await client.WriteAsync(json.Body);
                }

                break;
            case ProviderStreamResult events:
                using (events)
                {
                    await foreach (var evt in events.Events)
                    {
                        // Mirrors GatewayRequestHandler.StreamAsync: one write and one flush per event.
                        evt.WriteTo(client);
                        await client.FlushAsync();
                    }
                }

                break;
            default:
                throw new InvalidOperationException("Unexpected provider result " + result.GetType().Name);
        }
    }

    private static ProviderAccount Provider(ProviderType type)
    {
        var provider = new ProviderAccount
        {
            Name = type.ToString(), Type = type, BaseUrl = "http://upstream/v1", TimeoutSeconds = 60,
            AuthMode = type == ProviderType.Anthropic ? ProviderAuthMode.XApiKeyHeader : ProviderAuthMode.Bearer,
        };
        provider.Deployments.Add(new ModelDeployment { Name = "bench/chat", UpstreamModel = "bench-chat", ProviderAccount = provider });
        return provider;
    }
}
