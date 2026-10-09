using System.Net;
using System.Net.Http.Headers;

namespace Ume.LlmGateway.Benchmarks;

/// <summary>
/// In-process stand-in for an LLM provider: answers instantly with canned bodies so benchmarks measure only the
/// gateway's own work. The request body is read in full, as a real socket would.
/// </summary>
public sealed class StubUpstream : HttpMessageHandler
{
    private static readonly byte[] StreamMarker = "\"stream\":true"u8.ToArray();

    public byte[] ChatResponse { get; init; } = Payloads.ChatResponse;
    public byte[] EmbeddingsResponse { get; init; } = Payloads.EmbeddingsResponse();
    public byte[] ChatStream { get; init; } = Payloads.OpenAIStream();
    public byte[] AnthropicStream { get; init; } = Payloads.AnthropicStream();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var path = request.RequestUri!.AbsolutePath;
        var stream = body.AsSpan().IndexOf(StreamMarker) >= 0;
        var (payload, contentType) = path.EndsWith("/embeddings", StringComparison.Ordinal) ? (EmbeddingsResponse, "application/json")
            : path.EndsWith("/messages", StringComparison.Ordinal) && stream ? (AnthropicStream, "text/event-stream")
            : stream ? (ChatStream, "text/event-stream")
            : (ChatResponse, "application/json");

        var content = new StreamContent(new MemoryStream(payload, writable: false));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request };
    }
}
