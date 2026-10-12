using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Security;
using Ume.LlmGateway.TestKit;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>Hosted <c>mcp</c> tools on <c>/v1/responses</c>: the gateway's tool loop against <see cref="ResponsesUpstreamServer"/>.</summary>
public sealed partial class McpGatewayTests
{
    private readonly ResponsesUpstreamServer _responses = new();

    /// <summary>A Responses provider of its own (models <c>{name}/chat</c> and <c>{name}/reasoning</c>), 1.4 SEK per fake turn.</summary>
    private async Task<string> ScriptProviderAsync(DataResidency residency = DataResidency.Eu, string prefix = "r")
    {
        var name = prefix + Guid.NewGuid().ToString("N")[..10];
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var provider = new ProviderAccount
        {
            Name = name, Type = ProviderType.OpenAICompatible, Residency = residency, BaseUrl = $"{_responses.Url}/{name}/v1",
            AuthMode = ProviderAuthMode.Bearer, EncryptedCredential = scope.ServiceProvider.GetRequiredService<CredentialProtector>().Protect("secret"),
            Capabilities = ProviderCapabilities.ChatCompletions | ProviderCapabilities.Responses | ProviderCapabilities.Streaming, TimeoutSeconds = 10, CreatedAt = DateTimeOffset.UtcNow,
        };
        foreach (var (model, upstream, profile) in new[] { ("chat", "fake-chat", ParameterProfile.Standard), ("reasoning", "fake-reasoning", ParameterProfile.OpenAIReasoning) })
        {
            provider.Deployments.Add(new ModelDeployment
            {
                Name = $"{name}/{model}", UpstreamModel = upstream, Kind = ModelKind.Chat, ParameterProfile = profile,
                Prices = [new ModelPrice { InputPerMillionUsd = 1000, CachedInputPerMillionUsd = 1000, OutputPerMillionUsd = 2000, EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-1) }],
            });
        }

        db.ProviderAccounts.Add(provider);
        await db.SaveChangesAsync(Ct);
        return name;
    }

    private async Task<string> ScriptRouteAsync(params string[] models)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var ids = new List<Guid>();
        foreach (var model in models)
        {
            ids.Add((await db.ModelDeployments.SingleAsync(d => d.Name == model, Ct)).Id);
        }

        var route = new RouteAlias
        {
            Name = "test/" + Guid.NewGuid(), Kind = ModelKind.Chat,
            Targets = [.. ids.Select((id, i) => new RouteTarget { ModelDeploymentId = id, Priority = i })],
        };
        db.RouteAliases.Add(route);
        await db.SaveChangesAsync(Ct);
        return route.Name;
    }

    private async Task<(TestKey Key, McpServer Server, string Provider)> LoopSetupAsync(McpApprovalPolicy policy = McpApprovalPolicy.Never, Action<VirtualKey>? key = null,
        DataResidency residency = DataResidency.Eu)
    {
        var testKey = await fixture.CreateKeyAsync(key);
        var server = await AddServerAsync(null,
            ("echo_args", McpToolApprovalState.Approved, EchoDefinition), ("lookup.case", McpToolApprovalState.Approved, LookupDefinition),
            ("flaky_delete", McpToolApprovalState.Approved, DeleteDefinition), ("contact", McpToolApprovalState.Approved, ReadDefinition),
            ("slow", McpToolApprovalState.Approved, ReadDefinition));
        await GrantPolicyAsync(testKey, server, policy);
        return (testKey, server, await ScriptProviderAsync(residency));
    }

    private async Task GrantPolicyAsync(TestKey key, McpServer server, McpApprovalPolicy policy)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        db.VirtualKeyMcpGrants.Add(new VirtualKeyMcpGrant
        {
            VirtualKeyId = key.Id, McpServerId = server.Id, AllowedTools = ["*"], ApprovalPolicy = policy, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
    }

    private static JsonObject HostedMcp(McpServer server, JsonNode? requireApproval = null)
    {
        var tool = new JsonObject { ["type"] = "mcp", ["server_label"] = server.Slug };
        if (requireApproval is not null)
        {
            tool["require_approval"] = requireApproval;
        }

        return tool;
    }

    private static JsonObject Request(string model, string prompt, params JsonNode[] tools) =>
        new() { ["model"] = model, ["input"] = prompt, ["tools"] = new JsonArray(tools) };

    private async Task<(HttpResponseMessage Response, JsonObject? Body)> RespondAsync(TestKey key, JsonObject body, CancellationToken? cancellation = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        var response = await fixture.Client.SendAsync(request, cancellation ?? Ct);
        var text = await response.Content.ReadAsStringAsync(cancellation ?? Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text) as JsonObject);
    }

    private async Task<List<UsageRecord>> UsageOfKeyAsync(TestKey key, int expected)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            await fixture.Services.GetRequiredService<Pipeline.UsageWriter>().FlushAsync(cts.Token);
            await using var scope = fixture.Services.CreateAsyncScope();
            var records = await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().UsageRecords.AsNoTracking()
                .Where(u => u.VirtualKeyId == key.Id).OrderBy(u => u.Timestamp).ToListAsync(cts.Token);
            if (records.Count >= expected)
            {
                return records;
            }

            await Task.Delay(50, cts.Token);
        }
    }

    private static string[] Types(JsonObject body) => [.. body["output"]!.AsArray().Select(i => i!["type"]!.GetValue<string>())];

    private static JsonObject Item(JsonObject body, string type, int index = 0) =>
        body["output"]!.AsArray().Where(i => i!["type"]!.GetValue<string>() == type).ElementAt(index)!.AsObject();

    private static string Text(JsonObject body) => Item(body, "message")["content"]![0]!["text"]!.GetValue<string>();

    [Fact]
    public async Task Parallel_mcp_calls_run_in_a_loop_with_summed_usage_per_turn_accounting_and_headers()
    {
        var (key, server, provider) = await LoopSetupAsync();
        var body = Request(provider + "/chat", $$"""Prüfe [[call:{{server.Slug}}__echo_args {"a":"1"}]] [[call:{{server.Slug}}__lookup_case {"number":"KS-1"}]]""",
            HostedMcp(server, "never"));
        body["tool_choice"] = "required";
        body["max_tool_calls"] = 5;
        var (response, answer) = await RespondAsync(key, body);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Types(answer!).ShouldBe(["mcp_list_tools", "mcp_call", "mcp_call", "message"]);
        Item(answer!, "mcp_list_tools")["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ShouldContain("lookup.case");
        var echo = Item(answer!, "mcp_call");
        echo["server_label"]!.GetValue<string>().ShouldBe(server.Slug);
        echo["name"]!.GetValue<string>().ShouldBe("echo_args");
        echo["arguments"]!.GetValue<string>().ShouldBe("""{"a":"1"}""");
        echo["status"]!.GetValue<string>().ShouldBe("completed");
        Item(answer!, "mcp_call", 1)["output"]!.GetValue<string>().ShouldStartWith("Case KS-1 is open.");
        Text(answer!).ShouldStartWith("Tool results: ");
        answer!["status"]!.GetValue<string>().ShouldBe("completed");
        answer["id"]!.GetValue<string>().ShouldStartWith("resp_");
        answer["id"]!.GetValue<string>().ShouldNotStartWith("resp_ume_");
        answer["tools"]![0]!["type"]!.GetValue<string>().ShouldBe("mcp");
        answer["tool_choice"]!.GetValue<string>().ShouldBe("required");
        answer["max_tool_calls"]!.GetValue<int>().ShouldBe(5);
        answer["usage"]!.ToJsonString().ShouldBe("""{"input_tokens":200,"input_tokens_details":{"cached_tokens":20},"output_tokens":40,"output_tokens_details":{"reasoning_tokens":0},"total_tokens":240}""");
        response.Headers.GetValues("x-ume-turns").Single().ShouldBe("2");
        response.Headers.GetValues("x-ume-cost-sek").Single().ShouldBe("2.800000");
        response.Headers.GetValues("x-ume-model").Single().ShouldBe(provider + "/chat");

        var sent = _responses.For(provider);
        sent.Count.ShouldBe(2);
        sent.ShouldAllBe(b => b["tools"]!.AsArray().All(t => t!["type"]!.GetValue<string>() == "function") && !b.ContainsKey("max_tool_calls"));
        sent[0]["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ShouldContain($"{server.Slug}__lookup_case");
        sent[0]["tools"]![0]!["strict"]!.GetValue<bool>().ShouldBeFalse();
        sent[0]["tool_choice"]!.GetValue<string>().ShouldBe("required");
        sent[1]["tool_choice"]!.GetValue<string>().ShouldBe("auto");
        sent[1]["input"]!.AsArray().Count(i => i!["type"]?.GetValue<string>() == "function_call_output").ShouldBe(2);

        var usage = await UsageOfKeyAsync(key, 4);
        var requestId = response.Headers.GetValues("x-request-id").Single();
        var turns = usage.Where(u => u.Endpoint == GatewayEndpoint.Responses).ToList();
        turns.Count.ShouldBe(2);
        turns.Single(u => u.RequestId == requestId).ParentRequestId.ShouldBeNull();
        turns.Single(u => u.RequestId != requestId).ParentRequestId.ShouldBe(requestId);
        turns.ShouldAllBe(u => u.Outcome == RequestOutcome.Success && u.InputTokens == 100 && u.CostSek == 1.4m);
        var tools = usage.Where(u => u.Endpoint == GatewayEndpoint.McpToolCall).ToList();
        tools.Select(u => u.McpTool).ShouldBe(["echo_args", "lookup.case"], ignoreOrder: true);
        tools.ShouldAllBe(u => u.ParentRequestId == requestId && u.McpServerId == server.Id && u.Outcome == RequestOutcome.Success);
    }

    [Fact]
    public async Task Requests_without_mcp_tools_take_the_normal_path()
    {
        var (key, _, provider) = await LoopSetupAsync();
        var body = Request(provider + "/chat", "[[call:get_weather {}]]", new JsonObject { ["type"] = "function", ["name"] = "get_weather", ["parameters"] = new JsonObject() });
        var (response, answer) = await RespondAsync(key, body);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.Contains("x-ume-turns").ShouldBeFalse();
        Types(answer!).ShouldBe(["function_call"]);
        _responses.For(provider).Single()["tools"]!.ToJsonString().ShouldBe(body["tools"]!.ToJsonString());
    }

    [Fact]
    public async Task A_call_to_the_callers_own_function_ends_the_loop_and_the_gateway_response_continues_it()
    {
        var (key, server, provider) = await LoopSetupAsync();
        var weather = new JsonObject { ["type"] = "function", ["name"] = "get_weather", ["parameters"] = new JsonObject { ["type"] = "object" } };
        var (_, first) = await RespondAsync(key, Request(provider + "/chat",
            $$"""[[call:get_weather {"city":"Umeå"}]] [[call:{{server.Slug}}__echo_args {"b":"2"}]]""", HostedMcp(server, "never"), weather));

        Types(first!).ShouldBe(["mcp_list_tools", "function_call", "mcp_call"]);
        Item(first!, "mcp_call")["status"]!.GetValue<string>().ShouldBe("calling");
        var id = first!["id"]!.GetValue<string>();
        id.ShouldStartWith("resp_ume_");
        _upstream.For(server.Slug).ShouldNotContain(r => r.Method == "tools/call"); // deferred until the caller answers

        var callId = Item(first!, "function_call")["call_id"]!.GetValue<string>();
        var next = new JsonObject
        {
            ["model"] = provider + "/chat", ["previous_response_id"] = id, ["tools"] = new JsonArray(HostedMcp(server, "never"), weather.DeepClone()),
            ["input"] = new JsonArray(new JsonObject { ["type"] = "function_call_output", ["call_id"] = callId, ["output"] = "Sol" }),
        };
        var (response, second) = await RespondAsync(key, next);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Types(second!).ShouldBe(["mcp_call", "message"]); // no mcp_list_tools on a continuation
        Item(second!, "mcp_call")["output"]!.GetValue<string>().ShouldContain("\"b\"");
        Text(second!).ShouldContain("Sol");
        second!["previous_response_id"]!.GetValue<string>().ShouldBe(id);
        _upstream.For(server.Slug).Count(r => r.Method == "tools/call").ShouldBe(1);
        var usage = await UsageOfKeyAsync(key, 3);
        usage.Single(u => u.RequestId == response.Headers.GetValues("x-request-id").Single()).ParentRequestId
            .ShouldBe(usage.OrderBy(u => u.Timestamp).First().RequestId);

        (await RespondAsync(key, next)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound); // used once
    }

    [Fact]
    public async Task Mcp_list_tools_is_left_out_when_the_input_already_has_it_and_allowed_tools_narrow_the_offer()
    {
        var (key, server, provider) = await LoopSetupAsync();
        var tool = HostedMcp(server, "never");
        tool["allowed_tools"] = new JsonArray("echo_args");
        var (_, first) = await RespondAsync(key, Request(provider + "/chat", $"[[call:{server.Slug}__lookup_case {{}}]]", tool));

        Item(first!, "mcp_list_tools")["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ShouldBe(["echo_args"]);
        _responses.For(provider)[0]["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ShouldBe([$"{server.Slug}__echo_args"]);
        Types(first!).ShouldBe(["mcp_list_tools", "message"]); // not offered: answered to the model as unknown, never run
        Text(first!).ShouldBe($"Tool results: Error: Unknown tool: '{server.Slug}__lookup_case'");
        _upstream.For(server.Slug).ShouldBeEmpty();

        var replay = new JsonObject
        {
            ["model"] = provider + "/chat", ["tools"] = new JsonArray(tool.DeepClone()),
            ["input"] = new JsonArray(Item(first!, "mcp_list_tools").DeepClone(), new JsonObject { ["role"] = "user", ["content"] = "Hej" }),
        };
        var (_, second) = await RespondAsync(key, replay);
        Types(second!).ShouldBe(["message"]);
        _responses.For(provider)[1]["input"]!.AsArray().ShouldNotContain(i => i!["type"] != null && i["type"]!.GetValue<string>() == "mcp_list_tools");
    }

    [Fact]
    public async Task Unusable_mcp_tools_are_refused()
    {
        var (key, server, provider) = await LoopSetupAsync();
        async Task<(HttpStatusCode, string)> Refused(Action<JsonObject> change, JsonObject? tool = null)
        {
            var body = Request(provider + "/chat", "Hej", tool ?? HostedMcp(server, "never"));
            change(body);
            var (response, answer) = await RespondAsync(key, body);
            return (response.StatusCode, answer!["error"]!["code"]!.GetValue<string>());
        }

        var wrongUrl = HostedMcp(server);
        wrongUrl["server_url"] = "https://evil.example/mcp";
        (await Refused(_ => { }, wrongUrl)).ShouldBe((HttpStatusCode.BadRequest, "invalid_request"));
        var sameUrl = HostedMcp(server, "never");
        sameUrl["server_url"] = server.Url;
        (await RespondAsync(key, Request(provider + "/chat", "Hej", sameUrl))).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var headers = HostedMcp(server);
        headers["headers"] = new JsonObject { ["Authorization"] = "Bearer x" };
        (await Refused(_ => { }, headers)).ShouldBe((HttpStatusCode.BadRequest, "invalid_request"));
        var authorization = HostedMcp(server);
        authorization["authorization"] = "token";
        (await Refused(_ => { }, authorization)).ShouldBe((HttpStatusCode.BadRequest, "invalid_request"));
        (await Refused(_ => { }, new JsonObject { ["type"] = "mcp", ["server_label"] = "nosuchserver" })).ShouldBe((HttpStatusCode.NotFound, "mcp_server_not_found"));
        (await Refused(b => b["tools"]!.AsArray().Add(new JsonObject { ["type"] = "function", ["name"] = $"{server.Slug}__echo_args" })))
            .ShouldBe((HttpStatusCode.BadRequest, "invalid_request"));
        (await Refused(b => b["previous_response_id"] = "resp_ume_0123")).ShouldBe((HttpStatusCode.NotFound, "mcp_continuation_not_found"));
        _responses.For(provider).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Calls_to_functions_nobody_offers_are_answered_to_the_model_and_count_as_tool_calls()
    {
        var (key, server, provider) = await LoopSetupAsync();
        var (response, answer) = await RespondAsync(key, Request(provider + "/chat", "[[call:no_such_tool {}]]", HostedMcp(server, "never")));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Types(answer!).ShouldBe(["mcp_list_tools", "message"]);
        Text(answer!).ShouldBe("Tool results: Error: Unknown tool: 'no_such_tool'");
        var usage = await UsageOfKeyAsync(key, 2);
        usage.ShouldAllBe(u => u.Endpoint == GatewayEndpoint.Responses);
        usage.Count.ShouldBe(2);

        var limited = Request(provider + "/chat", "[[call:no_such_tool {}]] [[call:other_unknown {}]]", HostedMcp(server, "never"));
        limited["max_tool_calls"] = 1;
        var (_, stopped) = await RespondAsync(key, limited);
        stopped!["incomplete_details"]!["reason"]!.GetValue<string>().ShouldBe("max_tool_calls");
        Types(stopped).ShouldBe(["mcp_list_tools"]);
        _upstream.For(server.Slug).ShouldBeEmpty();
    }

    [Fact]
    public async Task Items_of_the_providers_own_hosted_tools_pass_through_untouched()
    {
        var (key, server, provider) = await LoopSetupAsync();
        var (response, answer) = await RespondAsync(key, Request(provider + "/chat", $"[[web_search]] [[call:{server.Slug}__echo_args {{\"w\":\"1\"}}]]",
            HostedMcp(server, "never"), new JsonObject { ["type"] = "web_search" }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Types(answer!).ShouldBe(["mcp_list_tools", "web_search_call", "mcp_call", "message"]);
        var search = Item(answer!, "web_search_call");
        JsonNode.DeepEquals(search["action"], new JsonObject { ["type"] = "search", ["query"] = "Umeå" }).ShouldBeTrue();
        var sent = _responses.For(provider);
        sent.ShouldAllBe(b => b["tools"]!.AsArray().Count(t => t!["type"]!.GetValue<string>() == "web_search") == 1);
        sent[1]["input"]!.AsArray().Single(i => i!["type"] != null && i["type"]!.GetValue<string>() == "web_search_call")!["id"]!.GetValue<string>()
            .ShouldBe(search["id"]!.GetValue<string>());
        answer!["tools"]!.AsArray().Select(t => t!["type"]!.GetValue<string>()).ShouldBe(["mcp", "web_search"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_turn_with_a_call_needing_approval_runs_none_and_runs_them_after_the_answer(bool approve)
    {
        // The grant needs approval for tools that are not read-only (flaky_delete); the request alone would need none.
        var (key, server, provider) = await LoopSetupAsync(McpApprovalPolicy.NeverForReadOnly);
        var (response, first) = await RespondAsync(key, Request(provider + "/chat",
            $$"""[[call:{{server.Slug}}__echo_args {"c":"3"}]] [[call:{{server.Slug}}__flaky_delete {"id":"7"}]]""", HostedMcp(server, "never")));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Types(first!).ShouldBe(["mcp_list_tools", "mcp_call", "mcp_approval_request"]);
        var request = Item(first!, "mcp_approval_request");
        request["name"]!.GetValue<string>().ShouldBe("flaky_delete");
        request["arguments"]!.GetValue<string>().ShouldBe("""{"id":"7"}""");
        request["id"]!.GetValue<string>().ShouldStartWith("mcpr_");
        Item(first!, "mcp_call")["status"]!.GetValue<string>().ShouldBe("calling");
        first!["status"]!.GetValue<string>().ShouldBe("completed");
        _upstream.For(server.Slug).ShouldNotContain(r => r.Method == "tools/call");

        var answer = new JsonObject { ["type"] = "mcp_approval_response", ["approval_request_id"] = request["id"]!.GetValue<string>(), ["approve"] = approve };
        if (!approve)
        {
            answer["reason"] = "Inte nu";
        }

        var (_, second) = await RespondAsync(key, new JsonObject
        {
            ["model"] = provider + "/chat", ["previous_response_id"] = first["id"]!.GetValue<string>(),
            ["tools"] = new JsonArray(HostedMcp(server, "never")), ["input"] = new JsonArray(answer),
        });

        Types(second!).ShouldBe(["mcp_call", "mcp_call", "message"]);
        var delete = second!["output"]!.AsArray().Single(i => i!["name"]?.GetValue<string>() == "flaky_delete")!;
        delete["approval_request_id"]!.GetValue<string>().ShouldBe(request["id"]!.GetValue<string>());
        _upstream.For(server.Slug).Count(r => r.Tool == "echo_args").ShouldBe(1);
        _upstream.For(server.Slug).Count(r => r.Tool == "flaky_delete").ShouldBe(approve ? 1 : 0);
        if (approve)
        {
            delete["output"]!.GetValue<string>().ShouldBe("ok");
        }
        else
        {
            delete["error"]!["message"]!.GetValue<string>().ShouldBe("The user denied this tool call. Reason: Inte nu");
            _responses.For(provider)[^1]["input"]!.ToJsonString().ShouldContain("Error: The user denied this tool call.");
        }
    }

    [Fact]
    public async Task A_replayed_approval_is_checked_against_the_stored_call_used_once_and_bound_to_the_key()
    {
        var (key, server, provider) = await LoopSetupAsync(McpApprovalPolicy.Always);
        var prompt = $$"""[[call:{{server.Slug}}__echo_args {"d":"4"}]]""";
        var body = Request(provider + "/chat", prompt, HostedMcp(server));
        body["store"] = false;
        var (_, first) = await RespondAsync(key, body);
        var request = Item(first!, "mcp_approval_request");

        JsonObject Replay(Action<JsonObject>? tamper = null)
        {
            var resent = request.DeepClone().AsObject();
            tamper?.Invoke(resent);
            return new JsonObject
            {
                ["model"] = provider + "/chat", ["store"] = false, ["tools"] = new JsonArray(HostedMcp(server)),
                ["input"] = new JsonArray(
                    new JsonObject { ["role"] = "user", ["content"] = prompt },
                    Item(first!, "mcp_list_tools").DeepClone(),
                    resent,
                    new JsonObject { ["type"] = "mcp_approval_response", ["approval_request_id"] = request["id"]!.GetValue<string>(), ["approve"] = true }),
            };
        }

        var (tampered, tamperedBody) = await RespondAsync(key, Replay(r => r["arguments"] = """{"d":"5"}"""));
        tampered.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        tamperedBody!["error"]!["code"]!.GetValue<string>().ShouldBe("mcp_approval_mismatch");

        var other = await fixture.CreateKeyAsync();
        await GrantPolicyAsync(other, server, McpApprovalPolicy.Always);
        var (stolen, stolenBody) = await RespondAsync(other, Replay());
        stolen.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        stolenBody!["error"]!["code"]!.GetValue<string>().ShouldBe("mcp_approval_not_found");
        _upstream.For(server.Slug).ShouldNotContain(r => r.Method == "tools/call");

        var (ok, done) = await RespondAsync(key, Replay());
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        Types(done!).ShouldBe(["mcp_call", "message"]);
        Item(done!, "mcp_call")["output"]!.GetValue<string>().ShouldContain("\"d\"");

        (await RespondAsync(key, Replay())).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        _upstream.For(server.Slug).Count(r => r.Method == "tools/call").ShouldBe(1);
    }

    [Fact]
    public async Task Max_tool_calls_stops_before_any_call_of_the_turn_runs()
    {
        var (key, server, provider) = await LoopSetupAsync();
        var body = Request(provider + "/chat", $"[[call:{server.Slug}__echo_args {{}}]] [[call:{server.Slug}__lookup_case {{\"number\":\"KS-2\"}}]]", HostedMcp(server, "never"));
        body["max_tool_calls"] = 1;
        var (_, answer) = await RespondAsync(key, body);

        answer!["status"]!.GetValue<string>().ShouldBe("incomplete");
        answer["incomplete_details"]!["reason"]!.GetValue<string>().ShouldBe("max_tool_calls");
        answer["output"]!.AsArray().Where(i => i!["type"]!.GetValue<string>() == "mcp_call")
            .ShouldAllBe(i => i!["status"]!.GetValue<string>() == "incomplete" && i["error"]!["message"]!.GetValue<string>() == "Not run: max_tool_calls.");
        answer["id"]!.GetValue<string>().ShouldStartWith("resp_ume_");
        _upstream.For(server.Slug).ShouldBeEmpty();
    }

    [Fact]
    public async Task Max_turns_and_the_time_limit_end_the_loop_as_incomplete()
    {
        var (key, server, provider) = await LoopSetupAsync();
        await using (var gateway = fixture.Derive(s => s.Configure<Pipeline.GatewayOptions>(o => o.Mcp.Loop.MaxTurns = 3)))
        {
            using var client = gateway.CreateHttpsClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
            {
                Content = new StringContent(Request(provider + "/chat", $"[[loop:{server.Slug}__echo_args]]", HostedMcp(server, "never")).ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
            using var response = await client.SendAsync(request, Ct);
            var answer = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
            answer["incomplete_details"]!["reason"]!.GetValue<string>().ShouldBe("max_turns");
            response.Headers.GetValues("x-ume-turns").Single().ShouldBe("3");
            _upstream.For(server.Slug).Count(r => r.Method == "tools/call").ShouldBe(2);
        }

        await using (var gateway = fixture.Derive(s => s.AddSingleton<TimeProvider>(new JumpingTime())))
        {
            using var client = gateway.CreateHttpsClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
            {
                Content = new StringContent(Request(provider + "/chat", $"[[loop:{server.Slug}__lookup_case]]", HostedMcp(server, "never")).ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
            using var response = await client.SendAsync(request, Ct);
            JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["incomplete_details"]!["reason"]!.GetValue<string>().ShouldBe("time_limit");
        }
    }

    /// <summary>Every timestamp is ten minutes after the one before, so any wall-clock limit is reached at the first check.</summary>
    private sealed class JumpingTime : TimeProvider
    {
        private long _jumps;

        public override long GetTimestamp() => base.GetTimestamp() + (Interlocked.Increment(ref _jumps) * TimestampFrequency * 600);
    }

    [Fact]
    public async Task A_budget_that_cannot_hold_the_next_turn_ends_the_loop_before_the_calls_run()
    {
        var (key, server, provider) = await LoopSetupAsync();
        await fixture.AddBudgetAsync(BudgetScope.VirtualKey, key.Id, 2.2m);
        var body = Request(provider + "/chat", $"[[call:{server.Slug}__echo_args {{}}]]", HostedMcp(server, "never"));
        body["max_output_tokens"] = 10;
        var (response, answer) = await RespondAsync(key, body);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        answer!["incomplete_details"]!["reason"]!.GetValue<string>().ShouldBe("budget_exceeded");
        Item(answer, "mcp_call")["error"]!["message"]!.GetValue<string>().ShouldBe("Not run: budget_exceeded.");
        _upstream.For(server.Slug).ShouldBeEmpty();
        _responses.For(provider).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_tool_call_the_budget_cannot_pay_for_is_reported_to_the_model_and_ends_the_loop()
    {
        var (key, server, provider) = await LoopSetupAsync();
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            (await db.McpTools.SingleAsync(t => t.McpServerId == server.Id && t.Name == "echo_args", Ct)).PriceSek = 100m;
            await db.SaveChangesAsync(Ct);
        }

        await fixture.AddBudgetAsync(BudgetScope.VirtualKey, key.Id, 10m);
        var body = Request(provider + "/chat", $"[[call:{server.Slug}__echo_args {{}}]]", HostedMcp(server, "never"));
        body["max_output_tokens"] = 10;
        var (_, answer) = await RespondAsync(key, body);

        answer!["status"]!.GetValue<string>().ShouldBe("incomplete");
        answer["incomplete_details"]!["reason"]!.GetValue<string>().ShouldBe("budget_exceeded");
        Item(answer, "mcp_call")["error"]!["message"]!.GetValue<string>().ShouldContain("budget");
        Text(answer).ShouldStartWith("Tool results: Error: ");
        _upstream.For(server.Slug).ShouldNotContain(r => r.Method == "tools/call");
        var usage = await UsageOfKeyAsync(key, 3);
        usage.Single(u => u.Endpoint == GatewayEndpoint.McpToolCall).Outcome.ShouldBe(RequestOutcome.BudgetExceeded);
    }

    [Fact]
    public async Task A_provider_failure_after_turn_one_returns_what_was_done_without_falling_back()
    {
        var (key, server, provider) = await LoopSetupAsync();
        var backup = await ScriptProviderAsync();
        var route = await ScriptRouteAsync(provider + "/chat", backup + "/chat");
        var (response, answer) = await RespondAsync(key, Request(route, $"[[fail-turn:2]] [[call:{server.Slug}__echo_args {{\"e\":\"5\"}}]]", HostedMcp(server, "never")));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        answer!["status"]!.GetValue<string>().ShouldBe("incomplete");
        answer["incomplete_details"]!["reason"]!.GetValue<string>().ShouldBe("provider_error");
        Item(answer, "mcp_call")["status"]!.GetValue<string>().ShouldBe("completed");
        _responses.For(provider).Count.ShouldBe(2);
        _responses.For(backup).ShouldBeEmpty();
        var usage = await UsageOfKeyAsync(key, 3);
        usage.Where(u => u.Endpoint == GatewayEndpoint.Responses).Select(u => u.Outcome).ShouldBe([RequestOutcome.Success, RequestOutcome.ProviderError], ignoreOrder: true);
    }

    [Fact]
    public async Task A_client_that_leaves_stops_the_loop_and_every_step_is_accounted_once()
    {
        var (key, server, provider) = await LoopSetupAsync();
        using var leave = new CancellationTokenSource();
        var call = RespondAsync(key, Request(provider + "/chat", $"[[call:{server.Slug}__slow {{}}]]", HostedMcp(server, "never")), leave.Token);
        while (!_upstream.For(server.Slug).Any(r => r.Tool == "slow"))
        {
            await Task.Delay(20, Ct);
        }

        await leave.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(call);
        await _upstream.SlowCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var usage = await UsageOfKeyAsync(key, 2);
        await Task.Delay(200, Ct);
        usage = await UsageOfKeyAsync(key, 2);
        usage.Count.ShouldBe(2);
        usage.Single(u => u.Endpoint == GatewayEndpoint.Responses).Outcome.ShouldBe(RequestOutcome.Success);
        usage.Single(u => u.Endpoint == GatewayEndpoint.McpToolCall).StatusCode.ShouldBe(499);
        _responses.For(provider).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Reasoning_items_are_replayed_with_encrypted_content_when_store_is_off()
    {
        var (key, server, provider) = await LoopSetupAsync();
        var body = Request(provider + "/reasoning", $"[[call:{server.Slug}__echo_args {{}}]]", HostedMcp(server, "never"));
        body["store"] = false;
        var (response, answer) = await RespondAsync(key, body);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Types(answer!).ShouldBe(["mcp_list_tools", "reasoning", "mcp_call", "reasoning", "message"]);
        answer!["output"]!.AsArray().Where(i => i!["type"]!.GetValue<string>() == "reasoning").ShouldAllBe(i => !i!.AsObject().ContainsKey("encrypted_content"));
        answer["usage"]!["output_tokens_details"]!["reasoning_tokens"]!.GetValue<int>().ShouldBe(10);
        var sent = _responses.For(provider);
        sent.ShouldAllBe(b => b["include"]!.AsArray().Any(i => i!.GetValue<string>() == "reasoning.encrypted_content"));
        sent[1]["input"]!.AsArray().Single(i => i!["type"] != null && i["type"]!.GetValue<string>() == "reasoning")!["encrypted_content"]!.GetValue<string>().ShouldBe("enc_1");
    }

    [Fact]
    public async Task Personal_data_in_a_tool_result_is_redacted_or_withheld_before_the_next_turn()
    {
        var (redactKey, server, provider) = await LoopSetupAsync(key: k => k.PiiPolicy = PiiPolicy.Redact);
        var (response, answer) = await RespondAsync(redactKey, Request(provider + "/chat", $"[[call:{server.Slug}__contact {{}}]]", HostedMcp(server, "never")));
        Item(answer!, "mcp_call")["output"]!.GetValue<string>().ShouldBe("Ring [TELEFON]");
        _responses.For(provider)[1].ToJsonString().ShouldNotContain("070-123");
        response.Headers.GetValues("x-ume-pii").Single().ShouldBe("redacted");

        var rerouteKey = await fixture.CreateKeyAsync(k => k.PiiPolicy = PiiPolicy.RerouteToOnPrem);
        await GrantPolicyAsync(rerouteKey, server, McpApprovalPolicy.Never);
        var (_, withheld) = await RespondAsync(rerouteKey, Request(provider + "/chat", $"[[call:{server.Slug}__contact {{}}]]", HostedMcp(server, "never")));
        Item(withheld!, "mcp_call")["error"]!["message"]!.GetValue<string>().ShouldStartWith("Result withheld");
        var turn2 = _responses.For(provider)[^1].ToJsonString();
        turn2.ShouldNotContain("070-123");
        turn2.ShouldContain("Result withheld");
    }
}
