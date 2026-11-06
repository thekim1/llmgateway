using System.Net;
using System.Text.Json.Nodes;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>
/// Function tools pass through an OpenAI-compatible provider unchanged and in order: the definitions, the client's
/// choices, the assistant's calls and the tool results on the way up, and the calls in JSON and streamed answers on the
/// way down. The translated paths (Claude, the Responses bridge) are covered in <c>ProviderUnitTests</c> and
/// <c>ResponsesBridgeTests</c>; request policies cannot set tools (<c>RequestPolicyTests</c>).
/// </summary>
public sealed class ToolPassthroughTests(GatewayFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject Conversation(string marker, string model) => new()
    {
        ["model"] = model,
        ["messages"] = new JsonArray(
            new JsonObject { ["role"] = "user", ["content"] = marker + " What is the weather in Umeå and Luleå, and the time?" },
            new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = null,
                ["tool_calls"] = new JsonArray(
                    Call("call_1", "get_weather", """{"city":"Umeå"}"""),
                    Call("call_2", "get_weather", """{"city":"Luleå"}""")),
            },
            new JsonObject { ["role"] = "tool", ["tool_call_id"] = "call_1", ["content"] = """{"celsius":-3}""" },
            new JsonObject { ["role"] = "tool", ["tool_call_id"] = "call_2", ["content"] = """{"celsius":-9}""" },
            new JsonObject { ["role"] = "assistant", ["content"] = null, ["tool_calls"] = new JsonArray(Call("call_3", "get_time", "{}")) },
            new JsonObject { ["role"] = "tool", ["tool_call_id"] = "call_3", ["content"] = "12:00" }),
        ["tools"] = new JsonArray(Tool("get_weather"), Tool("get_time"), Tool("search_documents")),
        ["tool_choice"] = "auto",
        ["parallel_tool_calls"] = true,
    };

    private static JsonObject Call(string id, string name, string arguments) => new()
    {
        ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments },
    };

    private static JsonObject Tool(string name) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = "Test tool " + name,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["city"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("city"),
            },
        },
    };

    [Fact]
    public async Task Tools_calls_and_results_reach_the_provider_unchanged_and_in_order()
    {
        var marker = "TOOLS-" + Guid.NewGuid().ToString("N");
        var sent = Conversation(marker, "eu/ok");
        using var response = await fixture.SendRawAsync(await fixture.CreateKeyAsync(), "/v1/chat/completions", sent.ToJsonString());
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var upstream = JsonNode.Parse(await fixture.UpstreamBodyAsync("v1", marker))!;
        upstream["model"]!.GetValue<string>().ShouldBe("eu-ok"); // the only rewrite: the provider's model name
        foreach (var field in new[] { "messages", "tools", "tool_choice", "parallel_tool_calls" })
        {
            JsonNode.DeepEquals(upstream[field], sent[field]).ShouldBeTrue(field);
        }
    }

    [Fact]
    public async Task Tool_choice_naming_a_function_is_kept()
    {
        var marker = "CHOICE-" + Guid.NewGuid().ToString("N");
        var sent = Conversation(marker, "eu/ok");
        sent["tool_choice"] = new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = "get_time" } };
        sent["parallel_tool_calls"] = false;
        using var response = await fixture.SendRawAsync(await fixture.CreateKeyAsync(), "/v1/chat/completions", sent.ToJsonString());
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var upstream = JsonNode.Parse(await fixture.UpstreamBodyAsync("v1", marker))!;
        JsonNode.DeepEquals(upstream["tool_choice"], sent["tool_choice"]).ShouldBeTrue();
        upstream["parallel_tool_calls"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task Tool_calls_in_an_answer_come_back_unchanged()
    {
        var marker = "ANSWER-" + Guid.NewGuid().ToString("N");
        var calls = new JsonArray(Call("call_a", "get_weather", """{"city":"Umeå"}"""), Call("call_b", "get_weather", """{"city":"Kiruna"}"""));
        var answer = new JsonObject
        {
            ["id"] = "chatcmpl-tools",
            ["object"] = "chat.completion",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = null, ["tool_calls"] = calls.DeepClone() },
                ["finish_reason"] = "tool_calls",
            }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 50, ["completion_tokens"] = 10 },
        };
        fixture.Upstream.Given(Request.Create().WithPath("/v1/*").UsingPost().WithBody(new WildcardMatcher($"*{marker}*")))
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(answer.ToJsonString()));

        using var response = await fixture.SendRawAsync(await fixture.CreateKeyAsync(), "/v1/chat/completions", Conversation(marker, "eu/ok").ToJsonString());
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var choice = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["choices"]![0]!;
        JsonNode.DeepEquals(choice["message"]!["tool_calls"], calls).ShouldBeTrue();
        choice["finish_reason"]!.GetValue<string>().ShouldBe("tool_calls");
    }

    [Fact]
    public async Task Streamed_tool_call_deltas_come_back_unchanged_and_in_order()
    {
        var marker = "STREAM-" + Guid.NewGuid().ToString("N");
        JsonObject Chunk(JsonObject delta, string? finish = null) => new()
        {
            ["id"] = "chatcmpl-stream",
            ["object"] = "chat.completion.chunk",
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish }),
        };
        JsonObject ToolDelta(int index, string? id, string? name, string arguments) => new()
        {
            ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["index"] = index, ["id"] = id, ["type"] = id is null ? null : "function",
                ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments },
            }),
        };
        JsonObject[] chunks =
        [
            Chunk(new JsonObject { ["role"] = "assistant", ["content"] = null }),
            Chunk(ToolDelta(0, "call_x", "get_weather", "")),
            Chunk(ToolDelta(0, null, null, "{\"city\":")),
            Chunk(ToolDelta(0, null, null, "\"Umeå\"}")),
            Chunk(ToolDelta(1, "call_y", "get_time", "{}")),
            Chunk(new JsonObject(), "tool_calls"),
        ];
        var sse = string.Concat(chunks.Select(c => "data: " + c.ToJsonString() + "\n\n")) + "data: [DONE]\n\n";
        fixture.Upstream.Given(Request.Create().WithPath("/v1/*").UsingPost().WithBody(new WildcardMatcher($"*{marker}*")))
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "text/event-stream").WithBody(sse));

        var request = Conversation(marker, "eu/ok");
        request["stream"] = true;
        using var response = await fixture.SendRawAsync(await fixture.CreateKeyAsync(), "/v1/chat/completions", request.ToJsonString());
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var received = (await response.Content.ReadAsStringAsync(Ct))
            .Split('\n')
            .Where(l => l.StartsWith("data: {", StringComparison.Ordinal))
            .Select(l => JsonNode.Parse(l["data: ".Length..])!)
            .Where(c => c["choices"] is JsonArray { Count: > 0 })
            .ToList();

        received.Select(c => c["choices"]![0]!["delta"]!).ShouldBe(chunks.Select(c => c["choices"]![0]!["delta"]!), new NodeComparer());
        received[^1]["choices"]![0]!["finish_reason"]!.GetValue<string>().ShouldBe("tool_calls");
    }

    [Fact]
    public async Task Responses_api_tools_are_passed_through()
    {
        var marker = "RESP-" + Guid.NewGuid().ToString("N");
        var tools = new JsonArray(
            new JsonObject { ["type"] = "function", ["name"] = "get_weather", ["parameters"] = new JsonObject { ["type"] = "object" } },
            new JsonObject { ["type"] = "function", ["name"] = "get_time", ["parameters"] = new JsonObject { ["type"] = "object" } });
        var input = new JsonArray(
            new JsonObject { ["role"] = "user", ["content"] = marker },
            new JsonObject { ["type"] = "function_call", ["call_id"] = "c1", ["name"] = "get_weather", ["arguments"] = """{"city":"Umeå"}""" },
            new JsonObject { ["type"] = "function_call_output", ["call_id"] = "c1", ["output"] = "-3" });
        var sent = new JsonObject { ["model"] = "eu/ok", ["input"] = input, ["tools"] = tools, ["tool_choice"] = "required" };
        using var response = await fixture.SendRawAsync(await fixture.CreateKeyAsync(), "/v1/responses", sent.ToJsonString());
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var upstream = JsonNode.Parse(await fixture.UpstreamBodyAsync("v1", marker))!;
        JsonNode.DeepEquals(upstream["tools"], tools).ShouldBeTrue();
        JsonNode.DeepEquals(upstream["input"], input).ShouldBeTrue();
        upstream["tool_choice"]!.GetValue<string>().ShouldBe("required");
    }

    [Fact]
    public async Task Embeddings_size_and_encoding_are_passed_through()
    {
        var marker = "EMB-" + Guid.NewGuid().ToString("N");
        var sent = new JsonObject { ["model"] = "eu/embedding", ["input"] = new JsonArray(marker, "two"), ["dimensions"] = 1024, ["encoding_format"] = "base64" };
        using var response = await fixture.SendRawAsync(await fixture.CreateKeyAsync(), "/v1/embeddings", sent.ToJsonString());
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var upstream = JsonNode.Parse(await fixture.UpstreamBodyAsync("v1", marker))!;
        upstream["dimensions"]!.GetValue<int>().ShouldBe(1024);
        upstream["encoding_format"]!.GetValue<string>().ShouldBe("base64");
        JsonNode.DeepEquals(upstream["input"], sent["input"]).ShouldBeTrue();
    }

    private sealed class NodeComparer : IEqualityComparer<JsonNode>
    {
        public bool Equals(JsonNode? x, JsonNode? y) => JsonNode.DeepEquals(x, y);

        public int GetHashCode(JsonNode obj) => 0;
    }
}
