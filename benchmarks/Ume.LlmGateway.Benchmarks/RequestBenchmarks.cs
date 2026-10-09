using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.Benchmarks;

/// <summary>Per-request work on the client's body before it is sent upstream: parse, PII scan, rewrite, serialise.</summary>
[MemoryDiagnoser]
public class RequestBenchmarks
{
    private byte[] _bytes = null!;
    private JsonObject _body = null!;

    [Params("small", "large")]
    public string Size { get; set; } = "small";

    [GlobalSetup]
    public void Setup()
    {
        _bytes = Encoding.UTF8.GetBytes(Size == "small" ? Payloads.ChatRequest("ume/chat") : Payloads.LargeChatRequest("ume/chat"));
        _body = (JsonObject)JsonNode.Parse(_bytes)!;
    }

    [Benchmark(Description = "Parse body")]
    public JsonNode? Parse() => JsonNode.Parse(_bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 64 });

    [Benchmark(Description = "Clone + rewrite + serialise (one attempt)")]
    public byte[] PrepareAttempt()
    {
        var attempt = (JsonObject)_body.DeepClone();
        RequestRewriter.Apply(attempt, GatewayEndpoint.ChatCompletions, "bench-chat", ParameterProfile.Standard, streaming: false);
        return JsonSerializer.SerializeToUtf8Bytes(attempt);
    }

    [Benchmark(Description = "PII scan (detect only)")]
    public bool PiiScan() => PiiJsonScanner.Scan(_body, redact: false).HasPii;
}
