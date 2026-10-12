using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>
/// What the gateway relies on in the official C# MCP SDK: a Streamable HTTP client to an upstream server, and a server
/// whose tools/list and tools/call are low-level handlers (no C# method tools), so it can proxy tools it only knows as
/// data. The upstream returns text, an image and structured content that must reach the client unchanged.
/// </summary>
public sealed class McpSdkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string PixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private static readonly Tool Lookup = new()
    {
        Name = "lookup.case",
        Title = "Look up a case",
        Description = "Finds a case by number.",
        InputSchema = JsonElement.Parse("""{"type":"object","properties":{"number":{"type":"string"}},"required":["number"]}"""),
        OutputSchema = JsonElement.Parse("""{"type":"object","properties":{"number":{"type":"string"},"open":{"type":"boolean"}}}"""),
        Annotations = new ToolAnnotations { ReadOnlyHint = true },
    };

    private static CallToolResult LookupResult(string number) => new()
    {
        Content =
        [
            new TextContentBlock { Text = $"Case {number} is open." },
            ImageContentBlock.FromBytes(Convert.FromBase64String(PixelPng), "image/png"),
        ],
        StructuredContent = JsonElement.Parse($$"""{"number":"{{number}}","open":true,"tags":["a","b"]}"""),
    };

    private static async Task<WebApplication> StartServerAsync(Action<IMcpServerBuilder> configure)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        configure(builder.Services.AddMcpServer().WithHttpTransport());
        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync(Ct);
        return app;
    }

    private static Task<McpClient> ConnectAsync(WebApplication server, string? protocolVersion = null) =>
        McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions { Endpoint = new Uri("http://localhost/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
                server.GetTestClient(), NullLoggerFactory.Instance, ownsHttpClient: false),
            new McpClientOptions { ProtocolVersion = protocolVersion }, NullLoggerFactory.Instance, Ct);

    private static Task<WebApplication> StartUpstreamAsync() => StartServerAsync(mcp => mcp
        .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult { Tools = [Lookup] }))
        .WithCallToolHandler((request, _) => ValueTask.FromResult(request.Params!.Name == Lookup.Name
            ? LookupResult(request.Params.Arguments!["number"].GetString()!)
            : throw new McpProtocolException("Tool not found", McpErrorCode.InvalidParams))));

    [Theory]
    [InlineData(null, "2026-07-28")]
    [InlineData("2025-11-25", "2025-11-25")]
    [InlineData("2025-06-18", "2025-06-18")]
    public async Task Client_lists_and_calls_tools_on_a_streamable_http_upstream(string? requested, string negotiated)
    {
        await using var upstream = await StartUpstreamAsync();
        await using var client = await ConnectAsync(upstream, requested);
        client.NegotiatedProtocolVersion.ShouldBe(negotiated);

        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        var tool = tools.ShouldHaveSingleItem().ProtocolTool;
        tool.Name.ShouldBe("lookup.case");
        tool.Annotations!.ReadOnlyHint.ShouldBe(true);
        tool.OutputSchema.ShouldNotBeNull();

        var result = await client.CallToolAsync("lookup.case", new Dictionary<string, object?> { ["number"] = "KS-1" }, cancellationToken: Ct);
        result.IsError.ShouldNotBe(true);
        result.Content.OfType<TextContentBlock>().Single().Text.ShouldBe("Case KS-1 is open.");
        var image = result.Content.OfType<ImageContentBlock>().Single();
        image.MimeType.ShouldBe("image/png");
        Convert.ToBase64String(image.DecodedData.Span).ShouldBe(PixelPng);
        result.StructuredContent!.Value.GetProperty("tags").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Low_level_handlers_proxy_renamed_tools_and_pass_results_through()
    {
        await using var upstream = await StartUpstreamAsync();
        await using var upstreamClient = await ConnectAsync(upstream);
        var upstreamTools = (await upstreamClient.ListToolsAsync(cancellationToken: Ct)).Select(t => t.ProtocolTool).ToList();
        var byExposedName = upstreamTools.ToDictionary(t => "demo__" + t.Name.Replace('.', '_'), t => t.Name);

        await using var proxy = await StartServerAsync(mcp => mcp
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools = [.. upstreamTools.Select(t => new Tool
                {
                    Name = byExposedName.Single(e => e.Value == t.Name).Key,
                    Title = t.Title,
                    Description = t.Description,
                    InputSchema = t.InputSchema,
                    OutputSchema = t.OutputSchema,
                    Annotations = t.Annotations,
                })],
            }))
            .WithCallToolHandler(async (request, cancellationToken) =>
                byExposedName.TryGetValue(request.Params!.Name, out var upstreamName)
                    ? await upstreamClient.CallToolAsync(new CallToolRequestParams { Name = upstreamName, Arguments = request.Params.Arguments }, cancellationToken)
                    : throw new McpProtocolException("Tool not found", McpErrorCode.InvalidParams)));
        await using var client = await ConnectAsync(proxy);

        var tool = (await client.ListToolsAsync(cancellationToken: Ct)).ShouldHaveSingleItem().ProtocolTool;
        tool.Name.ShouldBe("demo__lookup_case");
        tool.Annotations!.ReadOnlyHint.ShouldBe(true);
        JsonNode.DeepEquals(JsonSerializer.SerializeToNode(tool.InputSchema), JsonSerializer.SerializeToNode(Lookup.InputSchema)).ShouldBeTrue();

        var proxied = await client.CallToolAsync("demo__lookup_case", new Dictionary<string, object?> { ["number"] = "KS-2" }, cancellationToken: Ct);
        var direct = await upstreamClient.CallToolAsync("lookup.case", new Dictionary<string, object?> { ["number"] = "KS-2" }, cancellationToken: Ct);
        JsonNode.DeepEquals(
            JsonSerializer.SerializeToNode(proxied, McpJsonUtilities.DefaultOptions),
            JsonSerializer.SerializeToNode(direct, McpJsonUtilities.DefaultOptions)).ShouldBeTrue();
        proxied.Content.OfType<ImageContentBlock>().ShouldHaveSingleItem();

        var unknown = await Should.ThrowAsync<McpProtocolException>(() =>
            client.CallToolAsync("lookup.case", new Dictionary<string, object?> { ["number"] = "KS-3" }, cancellationToken: Ct).AsTask());
        unknown.ErrorCode.ShouldBe(McpErrorCode.InvalidParams);
    }

    [Fact]
    public async Task A_message_filter_can_answer_tools_call_with_raw_json()
    {
        var raw = JsonNode.Parse("""
            {"content":[{"type":"text","text":"hi"},{"type":"x-future-block","payload":{"k":1}}],
             "structuredContent":{"anything":[1,2,3]},"isError":false,"_meta":{"x":"y"},"vendorField":true}
            """)!;
        await using var proxy = await StartServerAsync(mcp => mcp
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult { Tools = [Lookup] }))
            .WithCallToolHandler((_, _) => throw new InvalidOperationException("The filter answers tools/call."))
            .WithMessageFilters(filters => filters.AddIncomingFilter(next => async (context, cancellationToken) =>
            {
                if (context.JsonRpcMessage is JsonRpcRequest { Method: RequestMethods.ToolsCall } request)
                {
                    await context.Server.SendMessageAsync(new JsonRpcResponse { Id = request.Id, Result = raw.DeepClone() }, cancellationToken);
                    return;
                }

                await next(context, cancellationToken);
            })));
        await using var client = await ConnectAsync(proxy);

        var response = await client.SendRequestAsync(new JsonRpcRequest
        {
            Method = RequestMethods.ToolsCall,
            Params = new JsonObject { ["name"] = "lookup.case", ["arguments"] = new JsonObject { ["number"] = "KS-4" } },
        }, Ct);

        var result = response.Result!.AsObject();
        result["_meta"]!.AsObject().Remove("io.modelcontextprotocol/serverInfo").ShouldBeTrue();
        JsonNode.DeepEquals(result, raw).ShouldBeTrue();

        // The typed result model rejects content block types it does not know, so a pure proxy forwards raw JSON.
        await Should.ThrowAsync<JsonException>(() =>
            client.CallToolAsync("lookup.case", new Dictionary<string, object?> { ["number"] = "KS-5" }, cancellationToken: Ct).AsTask());
    }
}
