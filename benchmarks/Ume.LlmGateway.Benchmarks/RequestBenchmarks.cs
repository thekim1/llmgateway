using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Pii;
using Ume.LlmGateway.Domain.Requests;
using Ume.LlmGateway.Gateway.Requests;

namespace Ume.LlmGateway.Benchmarks;

/// <summary>Per-request work on the client's body before it is sent upstream: parse, PII scan, rewrite, serialise.</summary>
[MemoryDiagnoser]
public class RequestBenchmarks
{
    private byte[] _bytes = null!;
    private JsonObject _body = null!;
    private RewriteSnapshot _original = null!;

    [Params("small", "large")]
    public string Size { get; set; } = "small";

    [GlobalSetup]
    public void Setup()
    {
        _bytes = Encoding.UTF8.GetBytes(Size == "small" ? Payloads.ChatRequest("ume/chat") : Payloads.LargeChatRequest("ume/chat"));
        _body = (JsonObject)JsonNode.Parse(_bytes)!;
        _original = RequestRewriter.Capture(_body);
    }

    [Benchmark(Description = "Parse body")]
    public JsonNode? Parse() => JsonNode.Parse(_bytes, documentOptions: GatewayRequestHandler.JsonOptions);

    /// <summary>What a fallback attempt costs: the previous rewrite undone, the body rewritten and serialised.</summary>
    [Benchmark(Description = "Restore + rewrite + serialise (one attempt)")]
    public byte[] PrepareAttempt()
    {
        RequestRewriter.Restore(_body, _original);
        RequestRewriter.Apply(_body, GatewayEndpoint.ChatCompletions, "bench-chat", ParameterProfile.Standard, streaming: false);
        return JsonSerializer.SerializeToUtf8Bytes(_body);
    }

    [Benchmark(Description = "PII scan (detect only)")]
    public bool PiiScan() => PiiJsonScanner.Scan(_body, redact: false).HasPii;
}
