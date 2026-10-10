using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>
/// A scripted Realtime provider on a loopback port (WireMock has no WebSockets). Each provider in the fixture has its
/// own base path; <c>livefail</c> refuses the handshake with 503. Sessions answer like OpenAI's:
/// <list type="bullet">
/// <item>commit → transcription completed, billed by duration (bytes / 48 000);</item>
/// <item>response.create → response.done with token usage (<c>response.metadata.input_tokens</c> overrides the count);</item>
/// <item>translation appends → transcript deltas, no usage;</item>
/// <item><c>test.drop</c> → the connection is aborted.</item>
/// </list>
/// </summary>
public sealed class RealtimeUpstream : IAsyncDisposable
{
    private WebApplication? _app;

    public string Url { get; private set; } = string.Empty;

    public ConcurrentQueue<Connection> Connections { get; } = new();

    public sealed record Connection(string Provider, string Path, string Query, string? Authorization, string? Beta)
    {
        public ConcurrentQueue<JsonObject> Received { get; } = new();
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        _app = builder.Build();
        _app.UseWebSockets();
        _app.Map("/{provider}/v1/realtime", (HttpContext http, string provider) => HandleAsync(http, provider, translations: false));
        _app.Map("/{provider}/v1/realtime/translations", (HttpContext http, string provider) => HandleAsync(http, provider, translations: true));
        await _app.StartAsync();
        Url = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    /// <summary>The newest session opened at <paramref name="provider"/>.</summary>
    public Connection Last(string provider) => Connections.Last(c => c.Provider == provider);

    private async Task HandleAsync(HttpContext http, string provider, bool translations)
    {
        if (provider == "livefail")
        {
            http.Response.StatusCode = 503;
            return;
        }

        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = 400;
            return;
        }

        var connection = new Connection(provider, http.Request.Path, http.Request.QueryString.Value ?? string.Empty,
            http.Request.Headers.Authorization.ToString(), http.Request.Headers["OpenAI-Beta"].ToString() is { Length: > 0 } beta ? beta : null);
        Connections.Enqueue(connection);
        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        var session = new JsonObject { ["type"] = translations ? "translation" : "realtime", ["model"] = http.Request.Query["model"].ToString() };
        async Task Send(JsonObject evt) => await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(evt), WebSocketMessageType.Text, true, CancellationToken.None);
        await Send(new JsonObject { ["type"] = "session.created", ["session"] = session.DeepClone() });

        long audioBytes = 0;
        var buffer = new byte[4 * 1024 * 1024];
        try
        {
            while (true)
            {
                var length = 0;
                ValueWebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(buffer.AsMemory(length), CancellationToken.None);
                    length += received.Count;
                }
                while (!received.EndOfMessage);

                if (received.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return;
                }

                var evt = (JsonObject)JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, length))!;
                connection.Received.Enqueue(evt);
                switch (evt["type"]?.GetValue<string>())
                {
                    case "session.update" or "transcription_session.update":
                        foreach (var (name, value) in (JsonObject)evt["session"]!)
                        {
                            session[name] = value?.DeepClone();
                        }

                        await Send(new JsonObject { ["type"] = "session.updated", ["session"] = session.DeepClone() });
                        break;

                    case "input_audio_buffer.append" or "session.input_audio_buffer.append":
                        audioBytes += Convert.FromBase64String(evt["audio"]!.GetValue<string>()).Length;
                        if (translations)
                        {
                            await Send(new JsonObject { ["type"] = "session.output_transcript.delta", ["delta"] = "Hello " });
                        }

                        break;

                    case "input_audio_buffer.commit":
                        var seconds = audioBytes / 48_000m;
                        audioBytes = 0;
                        await Send(new JsonObject
                        {
                            ["type"] = "conversation.item.input_audio_transcription.completed",
                            ["item_id"] = "item_1",
                            ["transcript"] = "Hej från Umeå",
                            ["usage"] = new JsonObject { ["type"] = "duration", ["seconds"] = seconds },
                        });
                        break;

                    case "response.create":
                        var input = evt["response"]?["metadata"]?["input_tokens"]?.GetValue<long>() ?? 120;
                        await Send(new JsonObject
                        {
                            ["type"] = "response.done",
                            ["response"] = new JsonObject
                            {
                                ["status"] = "completed",
                                ["usage"] = new JsonObject
                                {
                                    ["total_tokens"] = input + 30, ["input_tokens"] = input, ["output_tokens"] = 30,
                                    ["input_token_details"] = new JsonObject { ["cached_tokens"] = 0, ["text_tokens"] = input - 100, ["audio_tokens"] = 100 },
                                    ["output_token_details"] = new JsonObject { ["text_tokens"] = 30, ["audio_tokens"] = 0 },
                                },
                            },
                        });
                        break;

                    case "test.drop":
                        socket.Abort();
                        return;
                }
            }
        }
        catch (WebSocketException)
        {
            // The gateway went away.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }
}
