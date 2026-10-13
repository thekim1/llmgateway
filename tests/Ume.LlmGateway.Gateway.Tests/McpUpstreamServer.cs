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
/// <item><c>big</c>: about 100 KB of text;</item>
/// <item><c>link</c>: a resource link to <c>file:///notes/a.txt</c> and an embedded <c>file:///notes/c.txt</c>;</item>
/// <item><c>progress</c>: two progress notifications, then "done"; <c>slow</c>: 30 s unless cancelled (<see cref="SlowCancelled"/>);</item>
/// <item><c>tool_error</c>: a tool error (<c>isError</c>) with a text; <c>structured_only</c>: structured content and no content blocks;</item>
/// <item><c>mixed</c>: a BMP image, audio, a resource link and an embedded PNG blob.</item>
/// </list>
/// Resources <c>file:///notes/a.txt</c> (text with an e-mail address) and <c>b.bin</c> (a PNG blob) on the first list page,
/// <c>c.txt</c> on the second, a template <c>file:///cases/{number}</c>, and the prompt <c>summarize.case</c>.
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
            .WithCallToolHandler(CallAsync)
            .WithListResourcesHandler((request, _) => ValueTask.FromResult(request.Params?.Cursor == "page2"
                ? new ListResourcesResult { Resources = [new Resource { Uri = "file:///notes/c.txt", Name = "c" }] }
                : new ListResourcesResult
                {
                    Resources = [new Resource { Uri = "file:///notes/a.txt", Name = "a" }, new Resource { Uri = "file:///notes/b.bin", Name = "b" }],
                    NextCursor = "page2",
                }))
            .WithListResourceTemplatesHandler((_, _) => ValueTask.FromResult(new ListResourceTemplatesResult
            {
                ResourceTemplates = [new ResourceTemplate { UriTemplate = "file:///cases/{number}", Name = "case" }],
            }))
            .WithReadResourceHandler((request, _) => ValueTask.FromResult(request.Params!.Uri switch
            {
                "file:///notes/a.txt" => new ReadResourceResult
                {
                    Contents = [new TextResourceContents { Uri = "file:///notes/a.txt", MimeType = "text/plain", Text = "Kontakt: anna.svensson@example.se" }],
                },
                "file:///notes/b.bin" => new ReadResourceResult
                {
                    Contents = [BlobResourceContents.FromBytes(Convert.FromBase64String(PixelPng), "file:///notes/b.bin", "image/png")],
                },
                _ => throw new McpProtocolException("Resource not found", McpErrorCode.InvalidParams),
            }))
            .WithListPromptsHandler((_, _) => ValueTask.FromResult(new ListPromptsResult
            {
                Prompts = [new Prompt { Name = "summarize.case", Description = "Summarises a case.", Arguments = [new PromptArgument { Name = "number", Required = true }] }],
            }))
            .WithGetPromptHandler((request, _) => ValueTask.FromResult(request.Params!.Name == "summarize.case"
                ? new GetPromptResult
                {
                    Messages =
                    [
                        new PromptMessage
                        {
                            Role = Role.User,
                            Content = new TextContentBlock { Text = $"Sammanfatta ärende {request.Params.Arguments?["number"].GetString()}. Handläggare: bo@example.se" },
                        },
                    ],
                }
                : throw new McpProtocolException("Unknown prompt", McpErrorCode.InvalidParams)));
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

    /// <summary>Set when a <c>slow</c> call was cancelled by the gateway.</summary>
    public TaskCompletionSource SlowCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async ValueTask<CallToolResult> CallAsync(ModelContextProtocol.Server.RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var name = request.Params!.Name;
        if (name == "progress" && request.Params.ProgressToken is { } token)
        {
            await request.Server.NotifyProgressAsync(token, new ProgressNotificationValue { Progress = 1, Total = 2, Message = "Halvvägs" }, cancellationToken: cancellationToken);
            await request.Server.NotifyProgressAsync(token, new ProgressNotificationValue { Progress = 2, Total = 2 }, cancellationToken: cancellationToken);
            // A long-running tool: the result comes a while after the last progress.
            await Task.Delay(300, cancellationToken);
        }

        if (name == "slow")
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                SlowCancelled.TrySetResult();
                throw;
            }
        }

        return Call(name, request.Params.Arguments);
    }

    private static CallToolResult Call(string name, IDictionary<string, JsonElement>? arguments) => name switch
    {
        "progress" or "slow" => new CallToolResult { Content = [new TextContentBlock { Text = "done" }] },
        "link" => new CallToolResult
        {
            Content =
            [
                new ResourceLinkBlock { Uri = "file:///notes/a.txt", Name = "a" },
                new EmbeddedResourceBlock { Resource = new TextResourceContents { Uri = "file:///notes/c.txt", Text = "c" } },
            ],
            StructuredContent = JsonElement.Parse("""{"uri":"file:///notes/a.txt"}"""),
        },
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
        "tool_error" => new CallToolResult { Content = [new TextContentBlock { Text = "Ärendet är låst." }], IsError = true },
        "structured_only" => new CallToolResult { Content = [], StructuredContent = JsonElement.Parse("""{"count":2}""") },
        "mixed" => new CallToolResult
        {
            Content =
            [
                ImageContentBlock.FromBytes(new byte[10], "image/bmp"),
                AudioContentBlock.FromBytes(new byte[8], "audio/wav"),
                new ResourceLinkBlock { Uri = "file:///notes/a.txt", Name = "a" },
                new EmbeddedResourceBlock { Resource = BlobResourceContents.FromBytes(Convert.FromBase64String(PixelPng), "file:///notes/b.bin", "image/png") },
            ],
        },
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
