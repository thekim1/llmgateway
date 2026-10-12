using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Ume.LlmGateway.AdminApi.Tests;

/// <summary>
/// An MCP server built on the official SDK on a loopback port, at <c>{Url}/{name}/mcp</c> for any name. Each name has
/// its own tool list (<see cref="SetTools"/>), which tests change between syncs, and can require a header
/// (<see cref="RequireHeader"/>); a request without it gets 401.
/// </summary>
public sealed class McpTestServer : IAsyncDisposable
{
    public const string Instructions = "Testserverns instruktioner.";

    private WebApplication? _app;
    private readonly ConcurrentDictionary<string, Tool[]> _tools = new();
    private readonly ConcurrentDictionary<string, (string Name, string Value)> _required = new();

    public string Url { get; private set; } = string.Empty;

    public string Endpoint(string server) => $"{Url}/{server}/mcp";

    public void SetTools(string server, params Tool[] tools) => _tools[server] = tools;

    public void RequireHeader(string server, string name, string value) => _required[server] = (name, value);

    public static Tool Tool(string name, string description = "Söker ärenden.", string schema = """{"type":"object","properties":{"q":{"type":"string"}}}""") =>
        new() { Name = name, Description = description, InputSchema = JsonElement.Parse(schema), Annotations = new ToolAnnotations { ReadOnlyHint = true } };

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = "admin-test-upstream", Version = "1.0.0" };
                o.ServerInstructions = Instructions;
            })
            .WithHttpTransport(o => o.Stateless = true)
            .WithListToolsHandler((request, _) =>
            {
                var server = request.Services!.GetRequiredService<IHttpContextAccessor>().HttpContext!.Request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
                return ValueTask.FromResult(new ListToolsResult { Tools = [.. _tools.GetValueOrDefault(server) ?? []] });
            });
        _app = builder.Build();
        _app.Use(async (http, next) =>
        {
            var server = http.Request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
            if (_required.TryGetValue(server, out var header) && http.Request.Headers[header.Name] != header.Value)
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(http);
        });
        _app.MapMcp("/{server}/mcp");
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
