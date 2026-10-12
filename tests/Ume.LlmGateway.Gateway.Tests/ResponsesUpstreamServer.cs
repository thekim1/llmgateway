using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ume.LlmGateway.FakeLlm;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>
/// A Responses API provider on a loopback port that answers with <see cref="FakeResponsesScript"/>, at
/// <c>{Url}/{name}/v1/responses</c> for any name, each name with its own script (and stored responses) and request log.
/// <c>stream: true</c> is answered as SSE events; a name starting with <c>down</c> answers 503.
/// </summary>
public sealed class ResponsesUpstreamServer : IAsyncDisposable
{
    private WebApplication? _app;
    private readonly ConcurrentDictionary<string, FakeResponsesScript> _scripts = new(StringComparer.Ordinal);

    public string Url { get; private set; } = string.Empty;

    /// <summary>The bodies the gateway sent, per provider name, in order.</summary>
    public ConcurrentDictionary<string, ConcurrentQueue<JsonObject>> Requests { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<JsonObject> For(string name) => Requests.TryGetValue(name, out var requests) ? [.. requests] : [];

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        _app = builder.Build();
        _app.MapPost("/{name}/v1/responses", async (HttpContext http, string name) =>
        {
            var body = (await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted))!.AsObject();
            Requests.GetOrAdd(name, _ => new()).Enqueue(body.DeepClone().AsObject());
            if (name.StartsWith("down", StringComparison.Ordinal))
            {
                return Results.Json(new JsonObject { ["error"] = new JsonObject { ["type"] = "server_error", ["message"] = "Down." } }, statusCode: 503);
            }

            var script = _scripts.GetOrAdd(name, _ => new FakeResponsesScript());
            if (body["stream"] is not JsonValue stream || !stream.GetValue<bool>())
            {
                var (status, answer) = script.Respond(body);
                return Results.Json(answer, statusCode: status);
            }

            var (streamStatus, error, events, breaksOff) = script.Stream(body);
            if (events is null)
            {
                return Results.Json(error, statusCode: streamStatus);
            }

            http.Response.ContentType = "text/event-stream";
            foreach (var evt in events)
            {
                await http.Response.WriteAsync($"event: {evt["type"]!.GetValue<string>()}\ndata: {evt.ToJsonString()}\n\n", http.RequestAborted);
                await http.Response.Body.FlushAsync(http.RequestAborted);
            }

            if (breaksOff)
            {
                // A reset discards what the gateway has not read yet; give it the events first.
                await Task.Delay(300, http.RequestAborted);
                http.Abort();
            }

            return Results.Empty;
        });
        await _app.StartAsync();
        Url = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }
}
