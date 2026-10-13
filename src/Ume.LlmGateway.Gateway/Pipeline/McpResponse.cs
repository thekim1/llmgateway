using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>
/// The answer to one MCP POST: a single JSON-RPC message as <c>application/json</c>, or, once something has to be sent
/// before it (progress, a <c>subscriptions/listen</c> stream), an SSE stream of messages. Writes are serialised, since
/// progress arrives on the upstream client's threads while the request awaits its result.
/// </summary>
internal sealed class McpResponse(HttpContext http) : IDisposable
{
    private static readonly byte[] EventPrefix = "event: message\ndata: "u8.ToArray();
    private static readonly byte[] EventSuffix = "\n\n"u8.ToArray();
    private static readonly byte[] KeepAlive = ": keep-alive\n\n"u8.ToArray();

    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _streaming;
    private bool _done;

    public HttpContext Http => http;

    /// <summary>Sets a response header while headers can still be sent; afterwards it is dropped.</summary>
    public void SetHeader(string name, string value)
    {
        if (!http.Response.HasStarted)
        {
            http.Response.Headers[name] = value;
        }
    }

    /// <summary>Switches to SSE now (for a stream that starts with nothing to say yet).</summary>
    public async Task StartStreamAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureStreamingAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>A notification before the answer; the response becomes an SSE stream if it is not one yet.</summary>
    public async Task NotifyAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_done)
            {
                return;
            }

            await EnsureStreamingAsync(ct);
            await WriteEventAsync(Build(w =>
            {
                w.WriteStartObject();
                w.WriteString("jsonrpc", "2.0");
                w.WriteString("method", method);
                w.WritePropertyName("params");
                parameters.WriteTo(w);
                w.WriteEndObject();
            }), ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task KeepAliveAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_streaming && !_done)
            {
                await http.Response.Body.WriteAsync(KeepAlive, ct);
                await http.Response.Body.FlushAsync(ct);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task ResultAsync(JsonNode? id, Action<Utf8JsonWriter> result, CancellationToken ct) =>
        AnswerAsync(id, w =>
        {
            w.WritePropertyName("result");
            result(w);
        }, 200, ct);

    public Task ErrorAsync(JsonNode? id, int code, string message, JsonObject? data = null, int httpStatus = 200, CancellationToken? ct = null) =>
        AnswerAsync(id, w =>
        {
            w.WriteStartObject("error");
            w.WriteNumber("code", code);
            w.WriteString("message", message);
            if (data is not null)
            {
                w.WritePropertyName("data");
                data.WriteTo(w);
            }

            w.WriteEndObject();
        }, httpStatus, ct ?? http.RequestAborted);

    /// <summary>The JSON-RPC response; the last message of the request.</summary>
    private async Task AnswerAsync(JsonNode? id, Action<Utf8JsonWriter> body, int httpStatus, CancellationToken ct)
    {
        var message = Build(w =>
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            if (id is null)
            {
                w.WriteNullValue();
            }
            else
            {
                id.WriteTo(w);
            }

            body(w);
            w.WriteEndObject();
        });
        await _lock.WaitAsync(CancellationToken.None);
        try
        {
            if (_done)
            {
                return;
            }

            _done = true;
            if (_streaming)
            {
                await WriteEventAsync(message, ct);
                return;
            }

            http.Response.StatusCode = httpStatus;
            http.Response.ContentType = "application/json";
            http.Response.ContentLength = message.WrittenCount;
            await http.Response.Body.WriteAsync(message.WrittenMemory, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task EnsureStreamingAsync(CancellationToken ct)
    {
        if (_streaming)
        {
            return;
        }

        _streaming = true;
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        http.Response.StatusCode = 200;
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache, no-store";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        await http.Response.StartAsync(ct);
    }

    private async Task WriteEventAsync(ArrayBufferWriter<byte> message, CancellationToken ct)
    {
        await http.Response.Body.WriteAsync(EventPrefix, ct);
        await http.Response.Body.WriteAsync(message.WrittenMemory, ct);
        await http.Response.Body.WriteAsync(EventSuffix, ct);
        await http.Response.Body.FlushAsync(ct);
    }

    private static ArrayBufferWriter<byte> Build(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using var w = new Utf8JsonWriter(buffer);
        write(w);
        w.Flush();
        return buffer;
    }

    public void Dispose() => _lock.Dispose();
}
