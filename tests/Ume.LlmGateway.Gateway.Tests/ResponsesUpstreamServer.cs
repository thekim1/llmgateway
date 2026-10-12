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
            var (status, answer) = _scripts.GetOrAdd(name, _ => new FakeResponsesScript()).Respond(body);
            return Results.Json(answer, statusCode: status);
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
