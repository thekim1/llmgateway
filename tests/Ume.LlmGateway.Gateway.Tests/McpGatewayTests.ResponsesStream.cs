using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.TestKit;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>Hosted <c>mcp</c> tools on <c>/v1/responses</c> with <c>stream: true</c>: one combined event stream over every turn.</summary>
public sealed partial class McpGatewayTests
{
    private async Task<(HttpResponseMessage Response, List<JsonObject> Events)> StreamAsync(TestKey key, JsonObject body)
    {
        body["stream"] = true;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        var response = await fixture.Client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        var events = new List<JsonObject>();
        foreach (var block in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n');
            if (lines.FirstOrDefault(l => l.StartsWith("data: ", StringComparison.Ordinal)) is not { } data)
            {
                continue;
            }

            var evt = JsonNode.Parse(data[6..])!.AsObject();
            lines.ShouldContain("event: " + evt["type"]!.GetValue<string>());
            events.Add(evt);
        }

        return (response, events);
    }

    private static string[] EventTypes(List<JsonObject> events) => [.. events.Select(e => e["type"]!.GetValue<string>())];

    private static JsonObject Final(List<JsonObject> events) => events[^1]["response"]!.AsObject();

    private static List<JsonObject> OfType(List<JsonObject> events, string type) => [.. events.Where(e => e["type"]!.GetValue<string>() == type)];

    /// <summary>
    /// One created/in_progress first, one final event last, <c>sequence_number</c> counting from 0, every item added once and
    /// done once at its index in the final output, and nothing of the provider's own function calls or envelopes.
    /// </summary>
    private static void ShouldBeOneStream(List<JsonObject> events)
    {
        var types = EventTypes(events);
        types[0].ShouldBe("response.created");
        types[1].ShouldBe("response.in_progress");
        types[^1].ShouldBeOneOf("response.completed", "response.incomplete");
        types.Count(t => t is "response.created" or "response.in_progress" or "response.completed" or "response.incomplete").ShouldBe(3);
        types.ShouldNotContain(t => t.StartsWith("response.function_call_arguments", StringComparison.Ordinal));
        events.Select(e => e["sequence_number"]!.GetValue<long>()).ShouldBe(Enumerable.Range(0, events.Count).Select(i => (long)i));

        var output = Final(events)["output"]!.AsArray();
        var added = OfType(events, "response.output_item.added");
        var done = OfType(events, "response.output_item.done");
        added.Select(e => e["output_index"]!.GetValue<int>()).Order().ShouldBe(Enumerable.Range(0, output.Count));
        done.Select(e => e["output_index"]!.GetValue<int>()).Order().ShouldBe(Enumerable.Range(0, output.Count));
        foreach (var evt in done)
        {
            JsonNode.DeepEquals(evt["item"], output[evt["output_index"]!.GetValue<int>()]).ShouldBeTrue(evt.ToJsonString());
        }

        foreach (var evt in added)
        {
            evt["item"]!["type"]!.GetValue<string>().ShouldBe(output[evt["output_index"]!.GetValue<int>()]!["type"]!.GetValue<string>());
        }
    }

    /// <summary>The response without the ids, timestamps and call ids that differ between two runs of the same script.</summary>
    private static JsonNode Normalized(JsonNode node)
    {
        var copy = node.DeepClone();
        void Strip(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o:
                    foreach (var name in new[] { "id", "call_id", "approval_request_id", "created_at" })
                    {
                        o.Remove(name);
                    }

                    foreach (var (_, value) in o)
                    {
                        Strip(value);
                    }

                    break;
                case JsonArray a:
                    foreach (var value in a)
                    {
                        Strip(value);
                    }

                    break;
            }
        }

        Strip(copy);
        return copy;
    }

    [Fact]
    public async Task A_streamed_loop_shows_list_tools_and_mcp_calls_as_one_stream()
    {
        var (key, server, provider) = await LoopSetupAsync();
        var (response, events) = await StreamAsync(key, Request(provider + "/chat", $$"""[[call:{{server.Slug}}__echo_args {"a":"1"}]]""", HostedMcp(server, "never")));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        response.Headers.GetValues("x-ume-model").Single().ShouldBe(provider + "/chat");
        ShouldBeOneStream(events);
        var final = Final(events);
        final["status"]!.GetValue<string>().ShouldBe("completed");
        Types(final).ShouldBe(["mcp_list_tools", "mcp_call", "message"]);
        final["tools"]![0]!["type"]!.GetValue<string>().ShouldBe("mcp");
        events[0]["response"]!["tools"]![0]!["type"]!.GetValue<string>().ShouldBe("mcp");
        events[0]["response"]!["output"]!.AsArray().ShouldBeEmpty();

        var list = Item(final, "mcp_list_tools");
        var listId = list["id"]!.GetValue<string>();
        OfType(events, "response.output_item.added")[0]["item"]!["tools"]!.AsArray().ShouldBeEmpty();
        OfType(events, "response.mcp_list_tools.in_progress").Single()["item_id"]!.GetValue<string>().ShouldBe(listId);
        OfType(events, "response.mcp_list_tools.completed").Single()["output_index"]!.GetValue<int>().ShouldBe(0);

        var call = Item(final, "mcp_call");
        var callId = call["id"]!.GetValue<string>();
        call["status"]!.GetValue<string>().ShouldBe("completed");
        var shell = OfType(events, "response.output_item.added").Single(e => e["output_index"]!.GetValue<int>() == 1)["item"]!;
        shell["type"]!.GetValue<string>().ShouldBe("mcp_call");
        shell["id"]!.GetValue<string>().ShouldBe(callId);
        shell["name"]!.GetValue<string>().ShouldBe("echo_args");
        var deltas = OfType(events, "response.mcp_call_arguments.delta");
        deltas.ShouldAllBe(e => e["item_id"]!.GetValue<string>() == callId && e["output_index"]!.GetValue<int>() == 1);
        string.Concat(deltas.Select(e => e["delta"]!.GetValue<string>())).ShouldBe("""{"a":"1"}""");
        OfType(events, "response.mcp_call_arguments.done").Single()["arguments"]!.GetValue<string>().ShouldBe("""{"a":"1"}""");
        foreach (var type in new[] { "response.mcp_call.in_progress", "response.mcp_call.completed" })
        {
            OfType(events, type).Single()["item_id"]!.GetValue<string>().ShouldBe(callId);
        }

        var types = EventTypes(events).ToList();
        types.IndexOf("response.mcp_call_arguments.done").ShouldBeLessThan(types.IndexOf("response.mcp_call.in_progress"));
        types.IndexOf("response.mcp_call.completed").ShouldBeLessThan(types.LastIndexOf("response.output_text.delta"));
        OfType(events, "response.output_text.delta").ShouldAllBe(e => e["output_index"]!.GetValue<int>() == 2);

        _responses.For(provider).ShouldAllBe(b => b["stream"]!.GetValue<bool>());
        var usage = await UsageOfKeyAsync(key, 3);
        usage.Where(u => u.Endpoint == GatewayEndpoint.Responses).ShouldAllBe(u => u.Outcome == RequestOutcome.Success && u.InputTokens == 100 && u.CostSek == 1.4m);
        usage.Count(u => u.Endpoint == GatewayEndpoint.McpToolCall).ShouldBe(1);
    }

    [Fact]
    public async Task The_final_streamed_response_equals_the_non_streamed_answer()
    {
        var (key, server, provider) = await LoopSetupAsync();
        var other = await ScriptProviderAsync();
        JsonObject Body(string name)
        {
            var body = Request(name + "/reasoning", $$"""[[call:{{server.Slug}}__echo_args {"b":"2"}]] [[call:{{server.Slug}}__lookup_case {"number":"KS-1"}]] [[call:no_such_tool {}]]""",
                HostedMcp(server, "never"));
            body["store"] = false;
            return body;
        }

        var (_, answer) = await RespondAsync(key, Body(provider));
        var (_, events) = await StreamAsync(key, Body(other));

        ShouldBeOneStream(events);
        Types(answer!).ShouldBe(["mcp_list_tools", "reasoning", "mcp_call", "mcp_call", "reasoning", "message"]);
        JsonNode.DeepEquals(Normalized(Final(events)), Normalized(answer!)).ShouldBeTrue($"{Final(events).ToJsonString()}\n{answer!.ToJsonString()}");
        events.ShouldAllBe(e => !e.ToJsonString().Contains("encrypted_content"));
        _responses.For(other)[1]["input"]!.ToJsonString().ShouldContain("enc_1");
    }

    [Fact]
    public async Task A_streamed_call_needing_approval_is_shown_whole_and_the_continuation_streams_the_answered_calls()
    {
        var (key, server, provider) = await LoopSetupAsync(McpApprovalPolicy.NeverForReadOnly);
        var (_, events) = await StreamAsync(key, Request(provider + "/chat",
            $$"""[[call:{{server.Slug}}__echo_args {"c":"3"}]] [[call:{{server.Slug}}__flaky_delete {"id":"7"}]]""", HostedMcp(server, "never")));

        ShouldBeOneStream(events);
        var final = Final(events);
        Types(final).ShouldBe(["mcp_list_tools", "mcp_call", "mcp_approval_request"]);
        final["id"]!.GetValue<string>().ShouldStartWith("resp_ume_");
        var request = Item(final, "mcp_approval_request");
        var addedRequest = OfType(events, "response.output_item.added").Single(e => e["item"]!["type"]!.GetValue<string>() == "mcp_approval_request");
        JsonNode.DeepEquals(addedRequest["item"], request).ShouldBeTrue();
        addedRequest["output_index"]!.GetValue<int>().ShouldBe(2);
        Item(final, "mcp_call")["status"]!.GetValue<string>().ShouldBe("calling");
        OfType(events, "response.mcp_call.in_progress").ShouldBeEmpty();
        OfType(events, "response.mcp_call_arguments.done").Single()["arguments"]!.GetValue<string>().ShouldBe("""{"c":"3"}""");
        _upstream.For(server.Slug).ShouldNotContain(r => r.Method == "tools/call");

        var (_, next) = await StreamAsync(key, new JsonObject
        {
            ["model"] = provider + "/chat", ["previous_response_id"] = final["id"]!.GetValue<string>(), ["tools"] = new JsonArray(HostedMcp(server, "never")),
            ["input"] = new JsonArray(new JsonObject { ["type"] = "mcp_approval_response", ["approval_request_id"] = request["id"]!.GetValue<string>(), ["approve"] = true }),
        });

        ShouldBeOneStream(next);
        Types(Final(next)).ShouldBe(["mcp_call", "mcp_call", "message"]);
        Final(next)["previous_response_id"]!.GetValue<string>().ShouldBe(final["id"]!.GetValue<string>());
        OfType(next, "response.mcp_call.completed").Select(e => e["output_index"]!.GetValue<int>()).ShouldBe([0, 1]);
        _upstream.For(server.Slug).Count(r => r.Method == "tools/call").ShouldBe(2);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_provider_stream_that_breaks_off_ends_the_stream_as_incomplete_without_falling_back(int turn)
    {
        var (key, server, provider) = await LoopSetupAsync();
        var backup = await ScriptProviderAsync();
        var route = await ScriptRouteAsync(provider + "/chat", backup + "/chat");
        var (response, events) = await StreamAsync(key, Request(route, $$"""[[break-turn:{{turn}}]] [[call:{{server.Slug}}__echo_args {"e":"5"}]]""", HostedMcp(server, "never")));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var types = EventTypes(events);
        types[0].ShouldBe("response.created");
        types[^1].ShouldBe("response.incomplete");
        events.Select(e => e["sequence_number"]!.GetValue<long>()).ShouldBe(Enumerable.Range(0, events.Count).Select(i => (long)i));
        var final = Final(events);
        final["status"]!.GetValue<string>().ShouldBe("incomplete");
        final["incomplete_details"]!["reason"]!.GetValue<string>().ShouldBe("provider_error");
        Types(final).ShouldBe(turn == 1 ? ["mcp_list_tools"] : ["mcp_list_tools", "mcp_call"]);
        _responses.For(provider).Count.ShouldBe(turn);
        _responses.For(backup).ShouldBeEmpty();
        var usage = await UsageOfKeyAsync(key, turn == 1 ? 1 : 3);
        var outcomes = usage.Where(u => u.Endpoint == GatewayEndpoint.Responses).Select(u => u.Outcome).ToList();
        outcomes.ShouldBe(turn == 1 ? [RequestOutcome.ProviderError] : [RequestOutcome.Success, RequestOutcome.ProviderError], ignoreOrder: true);
        // The provider read the whole request, so a broken turn is accounted with the estimated input, later turns too.
        usage.Single(u => u.Endpoint == GatewayEndpoint.Responses && u.Outcome == RequestOutcome.ProviderError).InputTokens.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_streamed_loop_falls_back_before_the_first_byte()
    {
        var (key, server, provider) = await LoopSetupAsync();
        var down = await ScriptProviderAsync(prefix: "down");
        var route = await ScriptRouteAsync(down + "/chat", provider + "/chat");
        var (response, events) = await StreamAsync(key, Request(route, $"[[call:{server.Slug}__echo_args {{}}]]", HostedMcp(server, "never")));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("x-ume-model").Single().ShouldBe(provider + "/chat");
        response.Headers.GetValues("x-ume-fallbacks").Single().ShouldBe("1");
        ShouldBeOneStream(events);
        Types(Final(events)).ShouldBe(["mcp_list_tools", "mcp_call", "message"]);
        _responses.For(down).Count.ShouldBe(1);
        _responses.For(provider).Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_streamed_loop_sends_keep_alive_comments_while_a_tool_runs()
    {
        var (key, server, provider) = await LoopSetupAsync();
        await using var gateway = fixture.Derive(s => s.Configure<Pipeline.GatewayOptions>(o => o.Mcp.Loop.KeepAliveSeconds = 1));
        using var client = gateway.CreateHttpsClient();
        var body = Request(provider + "/chat", $"[[call:{server.Slug}__slow {{}}]]", HostedMcp(server, "never"));
        body["stream"] = true;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        using var leave = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        leave.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, leave.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(leave.Token));
        var seen = new List<string>();
        while (await reader.ReadLineAsync(leave.Token) is { } line)
        {
            seen.Add(line);
            if (line == ": keep-alive")
            {
                break;
            }
        }

        seen.ShouldContain(": keep-alive");
        seen.ShouldContain("event: response.mcp_call.in_progress");
        await leave.CancelAsync();
        reader.Dispose();
        response.Dispose();
        await _upstream.SlowCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }
}
