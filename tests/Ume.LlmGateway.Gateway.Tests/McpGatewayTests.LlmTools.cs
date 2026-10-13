using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Persistence;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary><c>GET /v1/mcp/tools</c> and <c>POST /v1/mcp/tools/call</c>: MCP tools in the tool shapes of the LLM APIs.</summary>
public sealed partial class McpGatewayTests
{
    private static readonly string[] Formats = ["openai", "responses", "anthropic"];

    private async Task<(TestKey Key, McpServer Server)> LlmToolServerAsync(Action<VirtualKey>? key = null)
    {
        var testKey = await fixture.CreateKeyAsync(key);
        var server = await AddServerAsync(null,
            ("lookup.case", McpToolApprovalState.Approved, LookupDefinition), ("echo_args", McpToolApprovalState.Approved, EchoDefinition),
            ("tool_error", McpToolApprovalState.Approved, ReadDefinition), ("structured_only", McpToolApprovalState.Approved, ReadDefinition),
            ("mixed", McpToolApprovalState.Approved, ReadDefinition), ("contact", McpToolApprovalState.Approved, ReadDefinition),
            ("flaky_delete", McpToolApprovalState.Approved, DeleteDefinition));
        await GrantAsync(testKey, server, "*");
        return (testKey, server);
    }

    private async Task<(HttpResponseMessage Response, JsonObject? Body)> LlmToolsAsync(TestKey? key, string? format)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, format is null ? "/v1/mcp/tools" : "/v1/mcp/tools?format=" + format);
        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        }

        var response = await fixture.Client.SendAsync(request, Ct);
        return (response, JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct)) as JsonObject);
    }

    private async Task<(HttpResponseMessage Response, JsonObject? Body)> LlmCallAsync(TestKey? key, JsonObject call, IDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/mcp/tools/call") { Content = new StringContent(call.ToJsonString(), Encoding.UTF8, "application/json") };
        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        }

        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        var response = await fixture.Client.SendAsync(request, Ct);
        return (response, JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct)) as JsonObject);
    }

    /// <summary>A tool call as a model produces it in <paramref name="format"/>.</summary>
    private static JsonObject ToolCall(string format, string name, JsonObject? arguments = null, string id = "call_1") => format switch
    {
        "responses" => new JsonObject { ["type"] = "function_call", ["call_id"] = id, ["name"] = name, ["arguments"] = (arguments ?? []).ToJsonString() },
        "anthropic" => new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = arguments ?? [] },
        _ => new JsonObject
        {
            ["id"] = id, ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = name, ["arguments"] = (arguments ?? []).ToJsonString() },
        },
    };

    private static string ToolName(string format, JsonNode tool) =>
        (format == "openai" ? tool["function"]!["name"] : tool["name"])!.GetValue<string>();

    private static int PixelBytes => Convert.FromBase64String(McpUpstreamServer.PixelPng).Length;

    [Fact]
    public async Task The_llm_tool_list_shows_what_tools_list_shows_in_each_format()
    {
        var key = await fixture.CreateKeyAsync();
        var all = await AddServerAsync(null,
            ("lookup.case", McpToolApprovalState.Approved, LookupDefinition), ("pending", McpToolApprovalState.Pending, EchoDefinition),
            ("rejected", McpToolApprovalState.Rejected, EchoDefinition), ("echo_args", McpToolApprovalState.Approved, EchoDefinition));
        var partial = await AddServerAsync(null, ("a", McpToolApprovalState.Approved, EchoDefinition), ("b", McpToolApprovalState.Approved, EchoDefinition));
        var disabled = await AddServerAsync(s => s.IsEnabled = false, ("x", McpToolApprovalState.Approved, EchoDefinition));
        await GrantAsync(key, all, "*");
        await GrantAsync(key, partial, "a");
        await GrantAsync(key, disabled, "*");
        var expected = await ListAsync(key);
        expected.Length.ShouldBe(3);

        foreach (var format in Formats)
        {
            var (response, body) = await LlmToolsAsync(key, format == "openai" ? null : format);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Headers.CacheControl!.NoStore.ShouldBeTrue();
            body!["object"]!.GetValue<string>().ShouldBe("list");
            body["format"]!.GetValue<string>().ShouldBe(format);
            var tools = body["data"]!.AsArray();
            tools.Select(t => ToolName(format, t!)).ShouldBe(expected);

            var lookup = tools.Single(t => ToolName(format, t!) == $"{all.Slug}__lookup_case")!;
            switch (format)
            {
                case "openai":
                    lookup["type"]!.GetValue<string>().ShouldBe("function");
                    lookup["function"]!["description"]!.GetValue<string>().ShouldBe("Finds a case.");
                    lookup["function"]!["parameters"]!["required"]![0]!.GetValue<string>().ShouldBe("number");
                    lookup["function"]!.AsObject().ContainsKey("strict").ShouldBeFalse();
                    break;
                case "responses":
                    lookup["type"]!.GetValue<string>().ShouldBe("function");
                    lookup["description"]!.GetValue<string>().ShouldBe("Finds a case.");
                    lookup["parameters"]!["required"]![0]!.GetValue<string>().ShouldBe("number");
                    lookup["strict"]!.GetValue<bool>().ShouldBeFalse();
                    break;
                default:
                    lookup.AsObject().ContainsKey("type").ShouldBeFalse();
                    lookup["description"]!.GetValue<string>().ShouldBe("Finds a case.");
                    lookup["input_schema"]!["required"]![0]!.GetValue<string>().ShouldBe("number");
                    break;
            }
        }

        (await LlmToolsAsync(key, "gemini")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await LlmToolsAsync(await fixture.CreateKeyAsync(), null)).Body!["data"]!.AsArray().ShouldBeEmpty();
        _upstream.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_llm_tool_endpoints_need_a_key()
    {
        var (list, listBody) = await LlmToolsAsync(null, null);
        list.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        list.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer");
        listBody!["error"]!["code"]!.GetValue<string>().ShouldBe("invalid_api_key");

        var (call, callBody) = await LlmCallAsync(null, ToolCall("openai", "x__y"));
        call.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        callBody!["error"]!["code"]!.GetValue<string>().ShouldBe("invalid_api_key");

        var revoked = await fixture.CreateKeyAsync(k => k.RevokedAt = DateTimeOffset.UtcNow.AddMinutes(-1));
        (await LlmToolsAsync(revoked, null)).Response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LlmCallAsync(revoked, ToolCall("openai", "x__y"))).Response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_listed_tool_called_in_each_format_is_answered_in_that_format()
    {
        var (key, server) = await LlmToolServerAsync();
        var placeholder = $"[image: image/png, {PixelBytes} bytes]";
        foreach (var format in Formats)
        {
            var (_, list) = await LlmToolsAsync(key, format);
            var name = list!["data"]!.AsArray().Select(t => ToolName(format, t!)).Single(n => n.EndsWith("__lookup_case", StringComparison.Ordinal));
            var (response, body) = await LlmCallAsync(key, ToolCall(format, name, new JsonObject { ["number"] = "KS-1" }, "call_" + format));

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            body.ShouldNotBeNull();
            body.ToJsonString().ShouldNotContain("\"tags\""); // structuredContent is not repeated next to text
            switch (format)
            {
                case "openai":
                    body.Select(p => p.Key).ShouldBe(["role", "tool_call_id", "content"]);
                    body["role"]!.GetValue<string>().ShouldBe("tool");
                    body["tool_call_id"]!.GetValue<string>().ShouldBe("call_openai");
                    body["content"]!.GetValue<string>().ShouldBe("Case KS-1 is open.\n\n" + placeholder);
                    break;
                case "responses":
                    body["type"]!.GetValue<string>().ShouldBe("function_call_output");
                    body["call_id"]!.GetValue<string>().ShouldBe("call_responses");
                    var output = body["output"]!.AsArray();
                    output[0]!.ToJsonString().ShouldBe("""{"type":"input_text","text":"Case KS-1 is open."}""");
                    output[1]!["type"]!.GetValue<string>().ShouldBe("input_image");
                    output[1]!["image_url"]!.GetValue<string>().ShouldBe("data:image/png;base64," + McpUpstreamServer.PixelPng);
                    break;
                default:
                    body["type"]!.GetValue<string>().ShouldBe("tool_result");
                    body["tool_use_id"]!.GetValue<string>().ShouldBe("call_anthropic");
                    body.ContainsKey("is_error").ShouldBeFalse();
                    var content = body["content"]!.AsArray();
                    content[0]!.ToJsonString().ShouldBe("""{"type":"text","text":"Case KS-1 is open."}""");
                    content[1]!["type"]!.GetValue<string>().ShouldBe("image");
                    content[1]!["source"]!.ToJsonString().ShouldBe($$"""{"type":"base64","media_type":"image/png","data":"{{McpUpstreamServer.PixelPng}}"}""");
                    break;
            }
        }

        _upstream.For(server.Slug).Count(r => r.Tool == "lookup.case").ShouldBe(3);
        _upstream.For(server.Slug).Where(r => r.Tool == "lookup.case").ShouldAllBe(r => r.Body.Contains("\"number\":\"KS-1\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tool_errors_structured_content_and_other_blocks_are_mapped_per_format()
    {
        var (key, server) = await LlmToolServerAsync();

        var (_, chatError) = await LlmCallAsync(key, ToolCall("openai", $"{server.Slug}__tool_error"));
        chatError!["content"]!.GetValue<string>().ShouldBe("Error: Ärendet är låst.");
        var (_, responsesError) = await LlmCallAsync(key, ToolCall("responses", $"{server.Slug}__tool_error"));
        responsesError!["output"]!.GetValue<string>().ShouldBe("Error: Ärendet är låst.");
        var (errorResponse, anthropicError) = await LlmCallAsync(key, ToolCall("anthropic", $"{server.Slug}__tool_error"));
        anthropicError!["is_error"]!.GetValue<bool>().ShouldBeTrue();
        anthropicError["content"]![0]!["text"]!.GetValue<string>().ShouldBe("Ärendet är låst.");
        (await fixture.UsageAsync(errorResponse)).Outcome.ShouldBe(RequestOutcome.Success);

        var (_, structured) = await LlmCallAsync(key, ToolCall("openai", $"{server.Slug}__structured_only"));
        structured!["content"]!.GetValue<string>().ShouldBe("""{"count":2}""");

        var (_, mixed) = await LlmCallAsync(key, ToolCall("anthropic", $"{server.Slug}__mixed"));
        mixed!["content"]!.AsArray().Select(b => b!["text"]!.GetValue<string>()).ShouldBe(
        [
            "[image: image/bmp, 10 bytes]",
            "[audio: audio/wav, 8 bytes]",
            $"[resource link: mcp+ume://{server.Slug}/file:///notes/a.txt (a)]",
            $"[resource: mcp+ume://{server.Slug}/file:///notes/b.bin, image/png, {PixelBytes} bytes]",
        ]);
        mixed.ToJsonString().ShouldNotContain(McpUpstreamServer.PixelPng);
    }

    [Fact]
    public async Task An_llm_tool_call_of_a_hidden_tool_looks_exactly_like_an_unknown_tool()
    {
        var key = await fixture.CreateKeyAsync();
        var server = await AddServerAsync(null, ("granted", McpToolApprovalState.Approved, EchoDefinition),
            ("other", McpToolApprovalState.Approved, EchoDefinition), ("pending", McpToolApprovalState.Pending, EchoDefinition));
        var ungrantedServer = await AddServerAsync(null, ("x", McpToolApprovalState.Approved, EchoDefinition));
        await GrantAsync(key, server, "granted");

        async Task<string> RefusalAsync(string name)
        {
            var (response, body) = await LlmCallAsync(key, ToolCall("openai", name));
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            var error = body!["error"]!.AsObject();
            error.Remove("request_id");
            return error.ToJsonString().Replace(name, "NAME", StringComparison.Ordinal);
        }

        var unknown = await RefusalAsync("no_such_tool");
        JsonNode.Parse(unknown)!["code"]!.GetValue<string>().ShouldBe("mcp_tool_not_found");
        JsonNode.Parse(unknown)!["message"]!.GetValue<string>().ShouldBe("Unknown tool: 'NAME'");
        (await RefusalAsync($"{server.Slug}__other")).ShouldBe(unknown);
        (await RefusalAsync($"{server.Slug}__pending")).ShouldBe(unknown);
        (await RefusalAsync($"{ungrantedServer.Slug}__x")).ShouldBe(unknown);
        (await RefusalAsync("granted")).ShouldBe(unknown); // the server's own name is not the exposed name
        _upstream.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_llm_tool_call_is_accounted_like_tools_call_on_mcp()
    {
        var (key, server) = await LlmToolServerAsync();
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            (await db.McpTools.SingleAsync(t => t.McpServerId == server.Id && t.Name == "lookup.case", Ct)).PriceSek = 1.5m;
            await db.SaveChangesAsync(Ct);
        }

        await fixture.AddBudgetAsync(BudgetScope.VirtualKey, key.Id, 4m);
        var (rpc, _) = await CallAsync(key, $"{server.Slug}__lookup_case", new JsonObject { ["number"] = "KS-1" });
        var (rest, _) = await LlmCallAsync(key, ToolCall("responses", $"{server.Slug}__lookup_case", new JsonObject { ["number"] = "KS-2" }));
        rest.StatusCode.ShouldBe(HttpStatusCode.OK);
        rest.Headers.GetValues("x-ume-cost-sek").Single().ShouldBe("1.500000");

        var viaMcp = await fixture.UsageAsync(rpc);
        var viaRest = await fixture.UsageAsync(rest);
        viaMcp.Endpoint.ShouldBe(GatewayEndpoint.Mcp);
        viaRest.Endpoint.ShouldBe(GatewayEndpoint.McpToolCall);
        foreach (var usage in new[] { viaMcp, viaRest })
        {
            usage.McpServerId.ShouldBe(server.Id);
            usage.McpMethod.ShouldBe("tools/call");
            usage.McpTool.ShouldBe("lookup.case");
            usage.RequestedModel.ShouldBe($"mcp:{server.Slug}/lookup.case");
            usage.Outcome.ShouldBe(RequestOutcome.Success);
            usage.StatusCode.ShouldBe(200);
            usage.CostSek.ShouldBe(1.5m);
            usage.ProviderAccountId.ShouldBeNull();
            usage.ParentRequestId.ShouldBeNull();
        }

        // 3 of 4 SEK is spent: the next 1.5 SEK call does not fit.
        var (refused, body) = await LlmCallAsync(key, ToolCall("anthropic", $"{server.Slug}__lookup_case", new JsonObject { ["number"] = "KS-3" }));
        refused.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        body!["error"]!["code"]!.GetValue<string>().ShouldBe("budget_exceeded");
        var usageRefused = await fixture.UsageAsync(refused);
        usageRefused.Outcome.ShouldBe(RequestOutcome.BudgetExceeded);
        usageRefused.StatusCode.ShouldBe(402);
        _upstream.For(server.Slug).Count(r => r.Method == "tools/call").ShouldBe(2);
    }

    [Fact]
    public async Task The_mcp_call_rate_limit_is_shared_with_mcp_and_answers_429()
    {
        var (key, server) = await LlmToolServerAsync(k => k.McpCallsPerMinute = 2);
        (await CallAsync(key, $"{server.Slug}__echo_args")).Body!["result"].ShouldNotBeNull();
        (await LlmCallAsync(key, ToolCall("openai", $"{server.Slug}__echo_args"))).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (response, body) = await LlmCallAsync(key, ToolCall("openai", $"{server.Slug}__echo_args"));
        response.StatusCode.ShouldBe((HttpStatusCode)429);
        body!["error"]!["code"]!.GetValue<string>().ShouldBe("rate_limited");
        response.Headers.RetryAfter.ShouldNotBeNull();
        var usage = await fixture.UsageAsync(response);
        usage.Outcome.ShouldBe(RequestOutcome.RateLimited);
        usage.StatusCode.ShouldBe(429);
    }

    [Fact]
    public async Task The_pii_policy_applies_to_llm_tool_call_arguments_and_results()
    {
        var (redactKey, server) = await LlmToolServerAsync(k => k.PiiPolicy = PiiPolicy.Redact);
        var blockKey = await fixture.CreateKeyAsync(k => k.PiiPolicy = PiiPolicy.Block);
        await GrantAsync(blockKey, server, "*");

        var (redacted, body) = await LlmCallAsync(redactKey, ToolCall("openai", $"{server.Slug}__echo_args", new JsonObject { ["note"] = "Mejla anna.svensson@example.se" }));
        _upstream.For(server.Slug).Single(r => r.Tool == "echo_args").Body.ShouldNotContain("anna.svensson@example.se");
        body!["content"]!.GetValue<string>().ShouldContain("[E-POST]");
        redacted.Headers.GetValues("x-ume-pii").Single().ShouldBe("redacted");
        (await fixture.UsageAsync(redacted)).PiiCategories.ShouldBe("Email:1");

        var (contact, contactBody) = await LlmCallAsync(redactKey, ToolCall("anthropic", $"{server.Slug}__contact"));
        contactBody!["content"]![0]!["text"]!.GetValue<string>().ShouldBe("Ring [TELEFON]");
        (await fixture.UsageAsync(contact)).PiiActionApplied.ShouldBe(PiiPolicy.Redact);

        var (blockedArguments, argumentsBody) = await LlmCallAsync(blockKey, ToolCall("openai", $"{server.Slug}__echo_args", new JsonObject { ["note"] = "anna@example.se" }));
        blockedArguments.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        argumentsBody!["error"]!["code"]!.GetValue<string>().ShouldBe("pii_blocked");
        _upstream.For(server.Slug).Count(r => r.Tool == "echo_args").ShouldBe(1);

        var (blockedResult, resultBody) = await LlmCallAsync(blockKey, ToolCall("responses", $"{server.Slug}__contact"));
        blockedResult.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        resultBody!["error"]!["code"]!.GetValue<string>().ShouldBe("pii_blocked");
        resultBody.ToJsonString().ShouldNotContain("070");
        var usage = await fixture.UsageAsync(blockedResult);
        usage.Outcome.ShouldBe(RequestOutcome.PiiBlocked);
        usage.StatusCode.ShouldBe(400);
        usage.Endpoint.ShouldBe(GatewayEndpoint.McpToolCall);
    }

    [Fact]
    public async Task Bad_llm_tool_calls_and_server_failures_are_http_errors()
    {
        var (key, server) = await LlmToolServerAsync();

        var (invalid, invalidBody) = await LlmCallAsync(key, ToolCall("openai", $"{server.Slug}__lookup_case", new JsonObject { ["number"] = 5 }));
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        invalidBody!["error"]!["code"]!.GetValue<string>().ShouldBe("mcp_invalid_arguments");
        invalidBody["error"]!["message"]!.GetValue<string>().ShouldContain("/number");

        var notJson = ToolCall("responses", $"{server.Slug}__echo_args");
        notJson["arguments"] = "{not json";
        (await LlmCallAsync(key, notJson)).Body!["error"]!["code"]!.GetValue<string>().ShouldBe("mcp_invalid_arguments");

        var (shape, shapeBody) = await LlmCallAsync(key, new JsonObject { ["type"] = "message", ["name"] = $"{server.Slug}__echo_args" });
        shape.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        shapeBody!["error"]!["code"]!.GetValue<string>().ShouldBe("invalid_request");
        (await fixture.UsageAsync(shape)).Endpoint.ShouldBe(GatewayEndpoint.McpToolCall);
        var noId = ToolCall("anthropic", $"{server.Slug}__echo_args");
        noId.Remove("id");
        (await LlmCallAsync(key, noId)).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _upstream.For(server.Slug).ShouldNotContain(r => r.Method == "tools/call");

        _upstream.FailOnce(server.Slug, "flaky_delete");
        var (failed, failedBody) = await LlmCallAsync(key, ToolCall("anthropic", $"{server.Slug}__flaky_delete"));
        failed.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        failedBody!["error"]!["code"]!.GetValue<string>().ShouldBe("mcp_upstream_failed");
        var usage = await fixture.UsageAsync(failed);
        usage.Outcome.ShouldBe(RequestOutcome.ProviderError);
        usage.StatusCode.ShouldBe(502);
        _upstream.For(server.Slug).Count(r => r.Tool == "flaky_delete").ShouldBe(1);
    }

    [Fact]
    public async Task The_parent_request_id_links_a_call_to_the_clients_llm_request()
    {
        var (key, server) = await LlmToolServerAsync();
        var (chat, _) = await LlmCallAsync(key, ToolCall("openai", $"{server.Slug}__echo_args"),
            new Dictionary<string, string> { ["x-ume-parent-request-id"] = "req_0192aa5e3c7d7b8f9a0b1c2d3e4f5a6b" });
        chat.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fixture.UsageAsync(chat)).ParentRequestId.ShouldBe("req_0192aa5e3c7d7b8f9a0b1c2d3e4f5a6b");

        foreach (var bad in new[] { new string('a', 65), "req 1", "req_1<script>", "id/1" })
        {
            var (response, body) = await LlmCallAsync(key, ToolCall("openai", $"{server.Slug}__echo_args"),
                new Dictionary<string, string> { ["x-ume-parent-request-id"] = bad });
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            body!["error"]!["code"]!.GetValue<string>().ShouldBe("invalid_request");
            (await fixture.UsageAsync(response)).ParentRequestId.ShouldBeNull();
        }

        _upstream.For(server.Slug).Count(r => r.Tool == "echo_args").ShouldBe(1);
    }
}
