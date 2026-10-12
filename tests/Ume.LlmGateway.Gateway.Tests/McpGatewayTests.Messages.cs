using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>Anthropic's MCP connector on <c>/v1/messages</c>: the gateway's tool loop against <see cref="MessagesUpstreamServer"/>.</summary>
public sealed partial class McpGatewayTests
{
    private const string ConnectorBeta = "mcp-client-2025-11-20";

    private readonly MessagesUpstreamServer _messages = new();

    /// <summary>An Anthropic provider of its own (model <c>{name}/claude</c>).</summary>
    private async Task<string> MessagesProviderAsync()
    {
        var name = "m" + Guid.NewGuid().ToString("N")[..10];
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var provider = new ProviderAccount
        {
            Name = name, Type = ProviderType.Anthropic, Residency = DataResidency.Eu, BaseUrl = $"{_messages.Url}/{name}/v1",
            AuthMode = ProviderAuthMode.XApiKeyHeader, EncryptedCredential = scope.ServiceProvider.GetRequiredService<CredentialProtector>().Protect("secret"),
            Capabilities = ProviderCapabilities.ChatCompletions | ProviderCapabilities.AnthropicMessages | ProviderCapabilities.Streaming, TimeoutSeconds = 10,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        provider.Deployments.Add(new ModelDeployment
        {
            Name = $"{name}/claude", UpstreamModel = "fake-claude", Kind = ModelKind.Chat, ParameterProfile = ParameterProfile.Standard,
            Prices = [new ModelPrice { InputPerMillionUsd = 1000, CachedInputPerMillionUsd = 1000, OutputPerMillionUsd = 2000, EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-1) }],
        });
        db.ProviderAccounts.Add(provider);
        await db.SaveChangesAsync(Ct);
        return name;
    }

    private async Task<(TestKey Key, McpServer Server, string Provider)> ConnectorSetupAsync(McpApprovalPolicy policy = McpApprovalPolicy.Never)
    {
        var (key, server, _) = await LoopSetupAsync(policy);
        return (key, server, await MessagesProviderAsync());
    }

    private static JsonObject ConnectorRequest(string model, string prompt, McpServer server, JsonObject? toolset = null, params JsonNode[] tools)
    {
        var allTools = new JsonArray(toolset ?? new JsonObject { ["type"] = "mcp_toolset", ["mcp_server_name"] = server.Slug });
        foreach (var tool in tools)
        {
            allTools.Add(tool);
        }

        return new JsonObject
        {
            ["model"] = model, ["max_tokens"] = 1024,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
            ["mcp_servers"] = new JsonArray(new JsonObject { ["type"] = "url", ["url"] = server.Url, ["name"] = server.Slug }),
            ["tools"] = allTools,
        };
    }

    private async Task<(HttpResponseMessage Response, JsonObject? Body)> MessagesAsync(TestKey key, JsonObject body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Add("x-api-key", key.Secret);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Headers.Add("anthropic-beta", ConnectorBeta);
        var response = await fixture.Client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text) as JsonObject);
    }

    private static string[] BlockTypes(JsonObject message) => [.. message["content"]!.AsArray().Select(b => b!["type"]!.GetValue<string>())];

    private static JsonObject Block(JsonObject message, string type, int index = 0) =>
        message["content"]!.AsArray().Where(b => b!["type"]!.GetValue<string>() == type).ElementAt(index)!.AsObject();

    private static string LastText(JsonObject message) =>
        message["content"]!.AsArray().Last(b => b!["type"]!.GetValue<string>() == "text")!["text"]!.GetValue<string>();

    [Fact]
    public async Task Connector_calls_run_in_a_loop_shown_as_mcp_blocks_with_summed_usage_and_per_turn_accounting()
    {
        var (key, server, provider) = await ConnectorSetupAsync();
        var (response, answer) = await MessagesAsync(key, ConnectorRequest(provider + "/claude",
            $$"""Prüfe [[call:{{server.Slug}}__echo_args {"a":"1"}]] [[call:{{server.Slug}}__lookup_case {"number":"KS-1"}]]""", server));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        BlockTypes(answer!).ShouldBe(["text", "mcp_tool_use", "mcp_tool_result", "mcp_tool_use", "mcp_tool_result", "text"]);
        var use = Block(answer!, "mcp_tool_use");
        use["name"]!.GetValue<string>().ShouldBe("echo_args");
        use["server_name"]!.GetValue<string>().ShouldBe(server.Slug);
        use["input"]!.ToJsonString().ShouldBe("""{"a":"1"}""");
        var result = Block(answer!, "mcp_tool_result", 1);
        result["tool_use_id"]!.GetValue<string>().ShouldBe(Block(answer!, "mcp_tool_use", 1)["id"]!.GetValue<string>());
        result["is_error"]!.GetValue<bool>().ShouldBeFalse();
        result["content"]![0]!["text"]!.GetValue<string>().ShouldStartWith("Case KS-1 is open.");
        LastText(answer!).ShouldStartWith("Tool results: ");
        answer!["stop_reason"]!.GetValue<string>().ShouldBe("end_turn");
        answer["usage"]!.ToJsonString().ShouldBe("""{"input_tokens":200,"cache_creation_input_tokens":0,"cache_read_input_tokens":20,"output_tokens":40}""");
        response.Headers.GetValues("x-ume-turns").Single().ShouldBe("2");
        response.Headers.Contains("x-ume-mcp-stop").ShouldBeFalse();
        response.Headers.GetValues("x-ume-model").Single().ShouldBe(provider + "/claude");

        var sent = _messages.For(provider);
        sent.Count.ShouldBe(2);
        sent.ShouldAllBe(b => !b.ContainsKey("mcp_servers") && b["tools"]!.AsArray().All(t => t!["type"] == null && t["input_schema"] != null));
        _messages.BetaHeaders(provider).ShouldAllBe(h => h == null);
        sent[0]["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ShouldContain($"{server.Slug}__lookup_case");
        sent[1]["messages"]!.AsArray().Select(m => m!["role"]!.GetValue<string>()).ShouldBe(["user", "assistant", "user"]);
        sent[1]["messages"]![2]!["content"]!.AsArray().ShouldAllBe(b => b!["type"]!.GetValue<string>() == "tool_result");

        var usage = await UsageOfKeyAsync(key, 4);
        var requestId = response.Headers.GetValues("x-request-id").Single();
        var turns = usage.Where(u => u.Endpoint == GatewayEndpoint.AnthropicMessages).ToList();
        turns.Count.ShouldBe(2);
        turns.Single(u => u.RequestId == requestId).ParentRequestId.ShouldBeNull();
        turns.Single(u => u.RequestId != requestId).ParentRequestId.ShouldBe(requestId);
        turns.ShouldAllBe(u => u.Outcome == RequestOutcome.Success && u.CostSek > 0);
        var tools = usage.Where(u => u.Endpoint == GatewayEndpoint.McpToolCall).ToList();
        tools.Select(u => u.McpTool).ShouldBe(["echo_args", "lookup.case"], ignoreOrder: true);
        tools.ShouldAllBe(u => u.ParentRequestId == requestId && u.McpServerId == server.Id && u.Outcome == RequestOutcome.Success);
    }

    [Fact]
    public async Task Unusable_connector_servers_are_refused_before_anything_is_sent()
    {
        var (key, server, provider) = await ConnectorSetupAsync();
        async Task<(HttpStatusCode, string)> Refused(Action<JsonObject> change)
        {
            var body = ConnectorRequest(provider + "/claude", "Hej", server);
            change(body);
            var (response, answer) = await MessagesAsync(key, body);
            answer!["type"]!.GetValue<string>().ShouldBe("error");
            return (response.StatusCode, answer["error"]!["type"]!.GetValue<string>());
        }

        (await Refused(b => b["mcp_servers"]![0]!["url"] = "https://evil.example/mcp")).ShouldBe((HttpStatusCode.BadRequest, "invalid_request_error"));
        (await Refused(b => b["mcp_servers"]![0]!["authorization_token"] = "token")).ShouldBe((HttpStatusCode.BadRequest, "invalid_request_error"));
        (await Refused(b => b["mcp_servers"]![0]!["name"] = "nosuchserver")).ShouldBe((HttpStatusCode.BadRequest, "invalid_request_error")); // toolset names another server
        (await Refused(b =>
        {
            b["mcp_servers"]![0]!["name"] = "nosuchserver";
            b["tools"]![0]!["mcp_server_name"] = "nosuchserver";
        })).ShouldBe((HttpStatusCode.NotFound, "not_found_error"));
        (await Refused(b => b["stream"] = true)).ShouldBe((HttpStatusCode.BadRequest, "invalid_request_error"));
        (await Refused(b => b["tools"]!.AsArray().Add(new JsonObject { ["name"] = $"{server.Slug}__echo_args", ["input_schema"] = new JsonObject() })))
            .ShouldBe((HttpStatusCode.BadRequest, "invalid_request_error"));

        var ungranted = await AddServerAsync(null, ("x", McpToolApprovalState.Approved, EchoDefinition));
        var (notGranted, _) = await MessagesAsync(key, ConnectorRequest(provider + "/claude", "Hej", ungranted));
        notGranted.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        _messages.For(provider).ShouldBeEmpty();

        var withoutUrl = ConnectorRequest(provider + "/claude", "Hej", server);
        withoutUrl["mcp_servers"]![0]!.AsObject().Remove("url");
        (await MessagesAsync(key, withoutUrl)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Tools_that_need_approval_are_not_offered_and_toolset_configs_narrow_the_offer()
    {
        var (key, server, provider) = await ConnectorSetupAsync(McpApprovalPolicy.NeverForReadOnly);
        var (_, first) = await MessagesAsync(key, ConnectorRequest(provider + "/claude", $"[[call:{server.Slug}__flaky_delete {{}}]]", server));

        _messages.For(provider)[0]["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>())
            .ShouldBe([$"{server.Slug}__contact", $"{server.Slug}__echo_args", $"{server.Slug}__lookup_case", $"{server.Slug}__slow"]);
        BlockTypes(first!).ShouldBe(["text", "text"]); // not offered: answered to the model as unknown, never run
        LastText(first!).ShouldBe($"Tool results: (error) Unknown tool: '{server.Slug}__flaky_delete'");
        _upstream.For(server.Slug).ShouldBeEmpty();

        var toolset = new JsonObject
        {
            ["type"] = "mcp_toolset", ["mcp_server_name"] = server.Slug, ["default_config"] = new JsonObject { ["enabled"] = false },
            ["configs"] = new JsonObject { ["echo_args"] = new JsonObject { ["enabled"] = true }, ["flaky_delete"] = new JsonObject { ["enabled"] = true } },
            ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
        };
        await MessagesAsync(key, ConnectorRequest(provider + "/claude", "Hej", server, toolset));
        var offered = _messages.For(provider)[^1]["tools"]!.AsArray();
        offered.Select(t => t!["name"]!.GetValue<string>()).ShouldBe([$"{server.Slug}__echo_args"]);
        offered[0]!["cache_control"]!["type"]!.GetValue<string>().ShouldBe("ephemeral");
    }

    [Fact]
    public async Task A_call_to_the_callers_own_tool_ends_the_loop_and_the_history_is_replayed_as_tool_blocks()
    {
        var (key, server, provider) = await ConnectorSetupAsync();
        var weather = new JsonObject { ["name"] = "get_weather", ["input_schema"] = new JsonObject { ["type"] = "object" } };
        var request = ConnectorRequest(provider + "/claude", $$"""[[call:get_weather {"city":"Umeå"}]] [[call:{{server.Slug}}__echo_args {"b":"2"}]]""", server, null, weather);
        var (response, first) = await MessagesAsync(key, request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        BlockTypes(first!).ShouldBe(["text", "tool_use", "mcp_tool_use", "mcp_tool_result"]);
        first!["stop_reason"]!.GetValue<string>().ShouldBe("tool_use");
        response.Headers.GetValues("x-ume-turns").Single().ShouldBe("1");
        _upstream.For(server.Slug).Count(r => r.Method == "tools/call").ShouldBe(1);

        var callId = Block(first!, "tool_use")["id"]!.GetValue<string>();
        var next = (JsonObject)request.DeepClone();
        var messages = next["messages"]!.AsArray();
        var assistant = first.DeepClone().AsObject();
        assistant["content"]!.AsArray().Insert(0, new JsonObject { ["type"] = "mcp_tool_listing", ["mcp_server_name"] = server.Slug, ["tools"] = new JsonArray() });
        messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = assistant["content"]!.DeepClone() });
        messages.Add(new JsonObject
        {
            ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = callId, ["content"] = "Sol" }),
        });
        var (_, second) = await MessagesAsync(key, next);

        LastText(second!).ShouldContain("Sol");
        LastText(second!).ShouldContain("\"b\"");
        var sent = _messages.For(provider)[^1]["messages"]!.AsArray();
        sent.Select(m => m!["role"]!.GetValue<string>()).ShouldBe(["user", "assistant", "user"]);
        sent[1]!["content"]!.AsArray().Select(b => b!["type"]!.GetValue<string>()).ShouldBe(["text", "tool_use", "tool_use"]);
        sent[1]!["content"]![2]!["name"]!.GetValue<string>().ShouldBe($"{server.Slug}__echo_args");
        sent[2]!["content"]!.AsArray().Select(b => b!["type"]!.GetValue<string>()).ShouldBe(["tool_result", "tool_result"]);
    }

    [Fact]
    public async Task Earlier_connector_turns_are_split_into_tool_use_and_tool_result_messages()
    {
        var (key, server, provider) = await ConnectorSetupAsync();
        var request = ConnectorRequest(provider + "/claude", $$"""[[call:{{server.Slug}}__echo_args {"c":"3"}]]""", server);
        var (_, first) = await MessagesAsync(key, request);

        var next = (JsonObject)request.DeepClone();
        next["messages"]!.AsArray().Add(new JsonObject { ["role"] = "assistant", ["content"] = first!["content"]!.DeepClone() });
        next["messages"]!.AsArray().Add(new JsonObject { ["role"] = "user", ["content"] = "Und weiter?" });
        var (response, second) = await MessagesAsync(key, next);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        LastText(second!).ShouldBe("Hej!");
        var sent = _messages.For(provider)[^1]["messages"]!.AsArray();
        sent.Select(m => m!["role"]!.GetValue<string>()).ShouldBe(["user", "assistant", "user", "assistant", "user"]);
        sent[1]!["content"]!.AsArray().Select(b => b!["type"]!.GetValue<string>()).ShouldBe(["text", "tool_use"]);
        sent[2]!["content"]![0]!["type"]!.GetValue<string>().ShouldBe("tool_result");
        sent[3]!["content"]!.AsArray().Select(b => b!["type"]!.GetValue<string>()).ShouldBe(["text"]);

        var foreign = (JsonObject)next.DeepClone();
        foreign["messages"]![1]!["content"]![1]!["server_name"] = "other";
        var (refused, _) = await MessagesAsync(key, foreign);
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var unanswered = (JsonObject)next.DeepClone();
        unanswered["messages"]![1]!["content"]!.AsArray().RemoveAt(2);
        (await MessagesAsync(key, unanswered)).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_limit_stops_the_loop_with_pause_turn_and_the_calls_shown_as_not_run()
    {
        var (key, server, provider) = await ConnectorSetupAsync();
        var prompt = string.Join(" ", Enumerable.Range(0, 11).Select(i => $$"""[[call:{{server.Slug}}__echo_args {"i":{{i}}}]]"""));
        var (response, answer) = await MessagesAsync(key, ConnectorRequest(provider + "/claude", prompt, server));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        answer!["stop_reason"]!.GetValue<string>().ShouldBe("pause_turn");
        response.Headers.GetValues("x-ume-mcp-stop").Single().ShouldBe("max_tool_calls");
        BlockTypes(answer).Count(t => t == "mcp_tool_result").ShouldBe(11);
        Block(answer, "mcp_tool_result")["is_error"]!.GetValue<bool>().ShouldBeTrue();
        Block(answer, "mcp_tool_result")["content"]![0]!["text"]!.GetValue<string>().ShouldBe("Not run: max_tool_calls.");
        _upstream.For(server.Slug).ShouldBeEmpty();
        _messages.For(provider).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_provider_failure_after_turn_one_keeps_what_ran_and_pauses()
    {
        var (key, server, provider) = await ConnectorSetupAsync();
        var (response, answer) = await MessagesAsync(key, ConnectorRequest(provider + "/claude", $"[[call:{server.Slug}__echo_args {{}}]] [[fail-turn:2]]", server));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        answer!["stop_reason"]!.GetValue<string>().ShouldBe("pause_turn");
        response.Headers.GetValues("x-ume-mcp-stop").Single().ShouldBe("provider_error");
        BlockTypes(answer).ShouldBe(["text", "mcp_tool_use", "mcp_tool_result"]);
        var usage = await UsageOfKeyAsync(key, 3);
        usage.Where(u => u.Endpoint == GatewayEndpoint.AnthropicMessages).Select(u => u.Outcome).ShouldBe([RequestOutcome.Success, RequestOutcome.ProviderError], ignoreOrder: true);
    }

    [Fact]
    public async Task Messages_requests_without_the_connector_take_the_normal_path()
    {
        var (key, _, provider) = await ConnectorSetupAsync();
        var body = new JsonObject
        {
            ["model"] = provider + "/claude", ["max_tokens"] = 100, ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "[[call:get_weather {}]]" }),
            ["tools"] = new JsonArray(new JsonObject { ["name"] = "get_weather", ["input_schema"] = new JsonObject { ["type"] = "object" } }),
        };
        var (response, answer) = await MessagesAsync(key, body);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.Contains("x-ume-turns").ShouldBeFalse();
        BlockTypes(answer!).ShouldBe(["text", "tool_use"]);
        _messages.For(provider).Single()["tools"]!.ToJsonString().ShouldBe(body["tools"]!.ToJsonString());
    }
}
