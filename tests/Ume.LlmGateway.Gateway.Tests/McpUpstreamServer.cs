using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>
/// An MCP server built on the official SDK on a loopback port, at <c>{Url}/{name}/mcp</c> for any name, so every test
/// has its own server address and request log. Tools (low-level handlers, as data):
/// <list type="bullet">
/// <item><c>lookup.case</c> (read-only): text, an image and structured content;</item>
/// <item><c>echo_args</c> (read-only): its arguments back as text and structured content;</item>
/// <item><c>flaky_read</c> (read-only) and <c>flaky_delete</c> (destructive): "ok", or HTTP 500 once after <see cref="FailOnce"/>;</item>
/// <item><c>contact</c>: a phone number in text and structured content;</item>
/// <item><c>fail_pii</c>: a JSON-RPC error whose long message contains an e-mail address;</item>
/// <item><c>big</c>: about 100 KB of text.</item>
/// </list>
/// </summary>
public sealed class McpUpstreamServer : IAsyncDisposable
{
    public const string PixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private WebApplication? _app;
    private readonly ConcurrentDictionary<(string Server, string Tool), bool> _failOnce = new();

    public string Url { get; private set; } = string.Empty;

    public ConcurrentQueue<Request> Requests { get; } = new();

    /// <summary>One HTTP request the server received: its JSON-RPC method, tool name and headers.</summary>
    public sealed record Request(string Server, string? Method, string? Tool, IReadOnlyDictionary<string, string> Headers, string Body);

    public string Endpoint(string server) => $"{Url}/{server}/mcp";

    public IReadOnlyList<Request> For(string server) => [.. Requests.Where(r => r.Server == server)];

    /// <summary>The next <c>tools/call</c> of <paramref name="tool"/> on <paramref name="server"/> fails with HTTP 500.</summary>
    public void FailOnce(string server, string tool) => _failOnce[(server, tool)] = true;

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddMcpServer(o => o.ServerInfo = new Implementation { Name = "test-upstream", Version = "1.0.0" })
            .WithHttpTransport()
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult { Tools = [new Tool { Name = "lookup.case", InputSchema = JsonElement.Parse("""{"type":"object"}""") }] }))
            .WithCallToolHandler((request, _) => ValueTask.FromResult(Call(request.Params!.Name, request.Params.Arguments)));
        _app = builder.Build();
        _app.Use(async (http, next) =>
        {
            if (HttpMethods.IsPost(http.Request.Method))
            {
                http.Request.EnableBuffering();
                var body = await new StreamReader(http.Request.Body).ReadToEndAsync(http.RequestAborted);
                http.Request.Body.Position = 0;
                var json = JsonNode.Parse(body) as JsonObject;
                var method = json?["method"]?.GetValue<string>();
                var tool = method == "tools/call" ? json?["params"]?["name"]?.GetValue<string>() : null;
                var server = http.Request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
                Requests.Enqueue(new Request(server, method, tool,
                    http.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase), body));
                if (tool is not null && _failOnce.TryRemove((server, tool), out _))
                {
                    http.Response.StatusCode = 500;
                    return;
                }
            }

            await next(http);
        });
        _app.MapMcp("/{server}/mcp");
        await _app.StartAsync();
        Url = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    private static CallToolResult Call(string name, IDictionary<string, JsonElement>? arguments) => name switch
    {
        "lookup.case" => new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = $"Case {arguments?["number"].GetString()} is open." },
                ImageContentBlock.FromBytes(Convert.FromBase64String(PixelPng), "image/png"),
            ],
            StructuredContent = JsonElement.Parse($$"""{"number":"{{arguments?["number"].GetString()}}","open":true,"tags":["a","b"]}"""),
        },
        "echo_args" => new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(arguments) }],
            StructuredContent = JsonSerializer.SerializeToElement(arguments),
        },
        "flaky_read" or "flaky_delete" => new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] },
        "contact" => new CallToolResult
        {
            Content = [new TextContentBlock { Text = "Ring 070-123 45 67" }],
            StructuredContent = JsonElement.Parse("""{"phone":"070-123 45 67"}"""),
        },
        "fail_pii" => throw new McpProtocolException("Inget ärende för anna.svensson@example.se. " + new string('x', 1000), McpErrorCode.InvalidParams),
        "big" => new CallToolResult { Content = [new TextContentBlock { Text = new string('x', 100_000) }] },
        _ => throw new McpProtocolException($"Unknown tool: '{name}'", McpErrorCode.InvalidParams),
    };

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }
}
