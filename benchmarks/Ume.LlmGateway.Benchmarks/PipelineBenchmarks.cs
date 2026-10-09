using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace Ume.LlmGateway.Benchmarks;

/// <summary>
/// End-to-end gateway overhead: a full request through the real ASP.NET Core pipeline (auth, catalog, rate limit,
/// budgets, routing, provider call, accounting) against an instant in-process upstream. Postgres (and Redis) run in
/// containers, so <c>StoreKind.Redis</c> includes real network round trips to Redis.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class PipelineBenchmarks
{
    private GatewayHost _host = null!;
    private byte[] _chat = null!;
    private byte[] _chatStream = null!;
    private byte[] _embeddings = null!;

    [Params(StoreKind.InMemory, StoreKind.Redis)]
    public StoreKind Store { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _host = GatewayHost.StartAsync(Store).GetAwaiter().GetResult();
        _chat = Encoding.UTF8.GetBytes(Payloads.ChatRequest(GatewayHost.ChatAlias));
        _chatStream = Encoding.UTF8.GetBytes(Payloads.ChatRequest(GatewayHost.ChatAlias, stream: true));
        _embeddings = Encoding.UTF8.GetBytes(Payloads.EmbeddingsRequest(GatewayHost.EmbeddingsAlias));
        Chat().GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup() => _host.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [Benchmark(Description = "POST /v1/chat/completions")]
    public Task<int> Chat() => _host.SendAsync("/v1/chat/completions", _chat);

    [Benchmark(Description = "POST /v1/chat/completions stream (300 chunks)")]
    public Task<int> ChatStream() => _host.SendAsync("/v1/chat/completions", _chatStream);

    [Benchmark(Description = "POST /v1/embeddings (16x1536)")]
    public Task<int> Embeddings() => _host.SendAsync("/v1/embeddings", _embeddings);
}
