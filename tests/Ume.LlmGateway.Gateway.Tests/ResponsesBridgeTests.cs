using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Providers;

namespace Ume.LlmGateway.Gateway.Tests;

public sealed class ResponsesBridgeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Hint = """{"error":{"message":"Function tools with reasoning_effort are not supported for gpt-6-astra in /v1/chat/completions. To use function tools, use /v1/responses or set reasoning_effort to 'none'.","type":"invalid_request_error","param":"reasoning_effort","code":null}}""";

    private const string Answer = """
        {"id":"resp_1","object":"response","created_at":1700,"model":"gpt-6-astra","status":"completed",
         "output":[{"type":"reasoning","id":"rs_1","summary":[]},
                   {"type":"message","id":"msg_1","role":"assistant","content":[{"type":"output_text","text":"Checking."}]},
                   {"type":"function_call","id":"fc_1","call_id":"call_1","name":"get_weather","arguments":"{\"city\":\"Umeå\"}"}],
         "usage":{"input_tokens":20,"input_tokens_details":{"cached_tokens":4},"output_tokens":30,"output_tokens_details":{"reasoning_tokens":25},"total_tokens":50}}
        """;

    [Theory]
    [InlineData("gpt-6-astra", true)]
    [InlineData("gpt-6.1-sol", true)]
    [InlineData("gpt-5.6-luna", true)]
    [InlineData("azure/gpt-6-luna-2026-09-22", true)]
    [InlineData("gpt-5.5", false)]
    [InlineData("gpt-5-mini", false)]
    [InlineData("o4-mini", false)]
    public void Known_models_are_gpt_5_6_and_gpt_6_onwards(string model, bool known)
    {
        ResponsesBridge.IsKnownModel(new ModelDeployment { Name = "m", UpstreamModel = model, ParameterProfile = ParameterProfile.OpenAIReasoning }).ShouldBe(known);
        ResponsesBridge.IsKnownModel(new ModelDeployment { Name = "m", UpstreamModel = model }).ShouldBeFalse(); // Standard: passed through as configured
    }

    [Theory]
    [InlineData("""{"tools":[{"type":"function","function":{"name":"f"}}]}""", true)]
    [InlineData("""{"tools":[{"type":"function","function":{"name":"f"}}],"reasoning_effort":"high"}""", true)]
    [InlineData("""{"tools":[{"type":"function","function":{"name":"f"}}],"reasoning_effort":"none"}""", false)]
    [InlineData("""{"tools":[]}""", false)]
    [InlineData("""{"messages":[]}""", false)]
    public void Only_tool_calls_with_reasoning_are_bridged(string body, bool wanted) =>
        ResponsesBridge.Wants(JsonNode.Parse(body)!.AsObject()).ShouldBe(wanted);

    [Fact]
    public void Request_maps_messages_tools_and_options()
    {
        var chat = JsonNode.Parse("""
            {"model":"gpt-6-astra","stream":true,"stream_options":{"include_usage":true},"max_completion_tokens":500,"reasoning_effort":"high",
             "parallel_tool_calls":false,"verbosity":"low","stop":["END"],"logit_bias":{"1":1},
             "response_format":{"type":"json_schema","json_schema":{"name":"weather","schema":{"type":"object"},"strict":true}},
             "messages":[
               {"role":"system","content":"Be brief."},
               {"role":"user","content":[{"type":"text","text":"Weather?"},{"type":"image_url","image_url":{"url":"data:image/png;base64,YWJj","detail":"low"}}]},
               {"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Umeå\"}"}}]},
               {"role":"tool","tool_call_id":"call_1","content":"Sunny"}],
             "tools":[{"type":"function","function":{"name":"get_weather","description":"Weather","parameters":{"type":"object"}}}],
             "tool_choice":{"type":"function","function":{"name":"get_weather"}}}
            """)!.AsObject();
        var output = ResponsesBridge.ToResponsesRequest(chat);

        output["model"]!.GetValue<string>().ShouldBe("gpt-6-astra");
        output["stream"]!.GetValue<bool>().ShouldBeTrue();
        output["store"]!.GetValue<bool>().ShouldBeFalse();
        output["max_output_tokens"]!.GetValue<int>().ShouldBe(500);
        output["reasoning"]!["effort"]!.GetValue<string>().ShouldBe("high");
        output["parallel_tool_calls"]!.GetValue<bool>().ShouldBeFalse();
        output["text"]!["verbosity"]!.GetValue<string>().ShouldBe("low");
        output["text"]!["format"]!["type"]!.GetValue<string>().ShouldBe("json_schema");
        output["text"]!["format"]!["name"]!.GetValue<string>().ShouldBe("weather");
        foreach (var dropped in (string[])["messages", "stream_options", "max_completion_tokens", "reasoning_effort", "stop", "logit_bias", "response_format", "verbosity"])
        {
            output.ContainsKey(dropped).ShouldBeFalse(dropped);
        }

        var input = output["input"]!.AsArray();
        input.Count.ShouldBe(4);
        input[0]!["role"]!.GetValue<string>().ShouldBe("system");
        input[1]!["content"]![0]!["type"]!.GetValue<string>().ShouldBe("input_text");
        input[1]!["content"]![1]!["image_url"]!.GetValue<string>().ShouldBe("data:image/png;base64,YWJj");
        input[1]!["content"]![1]!["detail"]!.GetValue<string>().ShouldBe("low");
        input[2]!["type"]!.GetValue<string>().ShouldBe("function_call");
        input[2]!["call_id"]!.GetValue<string>().ShouldBe("call_1");
        input[2]!.AsObject().ContainsKey("id").ShouldBeFalse();
        input[3]!["type"]!.GetValue<string>().ShouldBe("function_call_output");
        input[3]!["output"]!.GetValue<string>().ShouldBe("Sunny");

        var tool = output["tools"]![0]!;
        tool["name"]!.GetValue<string>().ShouldBe("get_weather");
        tool["strict"]!.GetValue<bool>().ShouldBeFalse();
        tool["parameters"]!["type"]!.GetValue<string>().ShouldBe("object");
        output["tool_choice"]!["name"]!.GetValue<string>().ShouldBe("get_weather");
    }

    [Theory]
    [InlineData("""{"n":2,"messages":[]}""", "n must be 1")]
    [InlineData("""{"modalities":["text","audio"],"messages":[]}""", "audio")]
    [InlineData("""{"messages":[{"role":"user","content":[{"type":"input_audio","input_audio":{"data":"YWJj","format":"wav"}}]}]}""", "input_audio")]
    public void Request_rejects_what_the_responses_api_cannot_carry(string body, string mentions) =>
        Should.Throw<NotSupportedException>(() => ResponsesBridge.ToResponsesRequest(JsonNode.Parse(body)!.AsObject())).Message.ShouldContain(mentions);

    [Fact]
    public void Response_maps_text_tool_calls_and_usage()
    {
        var output = ResponsesBridge.ToChatCompletion(JsonNode.Parse(Answer)!.AsObject());
        output["object"]!.GetValue<string>().ShouldBe("chat.completion");
        output["created"]!.GetValue<long>().ShouldBe(1700);
        var choice = output["choices"]![0]!;
        choice["finish_reason"]!.GetValue<string>().ShouldBe("tool_calls");
        choice["message"]!["content"]!.GetValue<string>().ShouldBe("Checking.");
        choice["message"]!["tool_calls"]![0]!["id"]!.GetValue<string>().ShouldBe("call_1");
        choice["message"]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>().ShouldBe("""{"city":"Umeå"}""");
        output["usage"]!["prompt_tokens"]!.GetValue<long>().ShouldBe(20);
        output["usage"]!["completion_tokens"]!.GetValue<long>().ShouldBe(30);
        output["usage"]!["prompt_tokens_details"]!["cached_tokens"]!.GetValue<long>().ShouldBe(4);
        output["usage"]!["completion_tokens_details"]!["reasoning_tokens"]!.GetValue<long>().ShouldBe(25);
    }

    [Fact]
    public void Incomplete_response_finishes_with_length()
    {
        var output = ResponsesBridge.ToChatCompletion(JsonNode.Parse("""{"id":"r","status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"output":[]}""")!.AsObject());
        output["choices"]![0]!["finish_reason"]!.GetValue<string>().ShouldBe("length");
        output["choices"]![0]!["message"]!["content"].ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stream_maps_text_tool_deltas_finish_and_usage(bool includeUsage)
    {
        var usage = new UsageAccumulator();
        var translator = new ResponsesStreamTranslator(usage, includeUsage);
        var result = StreamEvents().SelectMany(e => translator.Translate(new SseEvent(null, e))).ToArray();

        result[^1].Data.ShouldBe(SseEvent.Done);
        var chunks = result[..^1].Select(e => JsonNode.Parse(e.Data)!).ToArray();
        chunks.ShouldAllBe(c => c["object"]!.GetValue<string>() == "chat.completion.chunk" && c["id"]!.GetValue<string>() == "resp_1");
        chunks[0]["choices"]![0]!["delta"]!["role"]!.GetValue<string>().ShouldBe("assistant");
        chunks[1]["choices"]![0]!["delta"]!["content"]!.GetValue<string>().ShouldBe("Checking.");
        var start = chunks[2]["choices"]![0]!["delta"]!["tool_calls"]![0]!;
        start["index"]!.GetValue<int>().ShouldBe(0);
        start["id"]!.GetValue<string>().ShouldBe("call_1");
        start["function"]!["name"]!.GetValue<string>().ShouldBe("get_weather");
        chunks[3]["choices"]![0]!["delta"]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>().ShouldBe("""{"city":""");
        chunks[4]["choices"]![0]!["delta"]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>().ShouldBe("\"Umeå\"}");
        chunks[5]["choices"]![0]!["finish_reason"]!.GetValue<string>().ShouldBe("tool_calls");
        chunks.Any(c => c["usage"] is not null).ShouldBe(includeUsage);
        usage.ToTokenUsage(99).ShouldBe(new TokenUsage(20, 4, 30));
        usage.OutputCharacters.ShouldBe("Checking.".Length);
    }

    [Fact]
    public void Stream_failure_becomes_an_openai_error()
    {
        var translator = new ResponsesStreamTranslator(new UsageAccumulator(), includeUsage: false);
        var result = translator.Translate(new SseEvent("response.failed",
            """{"type":"response.failed","response":{"id":"r","status":"failed","error":{"code":"server_error","message":"Boom"}}}""")).ToArray();
        result.Length.ShouldBe(2);
        JsonNode.Parse(result[0].Data)!["error"]!["message"]!.GetValue<string>().ShouldBe("Boom");
        result[1].Data.ShouldBe(SseEvent.Done);
    }

    [Fact]
    public async Task Known_model_with_tools_goes_straight_to_responses()
    {
        using var handler = new ScriptedHandler(_ => Json(HttpStatusCode.OK, Answer));
        var adapter = new OpenAICompatibleAdapter(new ProviderHttpClient(handler));
        using var result = (ProviderJsonResult)await adapter.SendAsync(Call("gpt-6-astra", WithTools), Ct);

        handler.Paths.ShouldBe(["/openai/v1/responses"]);
        JsonNode.Parse(handler.Bodies[0])!["reasoning"].ShouldBeNull(); // default effort is the model's own
        JsonNode.Parse(result.Body.Span)!["choices"]![0]!["message"]!["tool_calls"]![0]!["id"]!.GetValue<string>().ShouldBe("call_1");
        result.Usage.ShouldBe(new TokenUsage(20, 4, 30));
    }

    [Theory]
    [InlineData("""{"model":"gpt-6-astra","messages":[{"role":"user","content":"Hi"}]}""")]
    [InlineData("""{"model":"gpt-6-luna","reasoning_effort":"none","messages":[{"role":"user","content":"Hi"}],"tools":[{"type":"function","function":{"name":"f"}}]}""")]
    public async Task Requests_these_models_take_on_chat_completions_stay_there(string body)
    {
        using var handler = new ScriptedHandler(_ => Json(HttpStatusCode.OK, """{"id":"c","choices":[],"usage":{"prompt_tokens":1,"completion_tokens":1}}"""));
        var adapter = new OpenAICompatibleAdapter(new ProviderHttpClient(handler));
        using var result = (ProviderJsonResult)await adapter.SendAsync(Call("gpt-6-astra", body), Ct);
        handler.Paths.ShouldBe(["/openai/v1/chat/completions"]);
    }

    [Fact]
    public async Task Provider_hint_bridges_a_deployment_with_its_own_name_and_is_remembered()
    {
        using var handler = new ScriptedHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal)
            ? Json(HttpStatusCode.BadRequest, Hint)
            : Json(HttpStatusCode.OK, Answer));
        var adapter = new OpenAICompatibleAdapter(new ProviderHttpClient(handler));
        var call = Call("prod-chat", WithTools);

        using (var first = (ProviderJsonResult)await adapter.SendAsync(call, Ct))
        {
            JsonNode.Parse(first.Body.Span)!["object"]!.GetValue<string>().ShouldBe("chat.completion");
        }

        using (await adapter.SendAsync(call, Ct) as ProviderJsonResult)
        {
        }

        handler.Paths.ShouldBe(["/openai/v1/chat/completions", "/openai/v1/responses", "/openai/v1/responses"]);
    }

    [Fact]
    public async Task Other_bad_requests_are_not_bridged()
    {
        using var handler = new ScriptedHandler(_ => Json(HttpStatusCode.BadRequest, """{"error":{"message":"Invalid schema for function 'f'","type":"invalid_request_error"}}"""));
        var adapter = new OpenAICompatibleAdapter(new ProviderHttpClient(handler));
        var failure = (await adapter.SendAsync(Call("prod-chat", WithTools), Ct)).ShouldBeOfType<ProviderFailure>();
        failure.StatusCode.ShouldBe(400);
        handler.Paths.ShouldBe(["/openai/v1/chat/completions"]);
    }

    [Fact]
    public async Task Untranslatable_content_is_a_client_error()
    {
        using var handler = new ScriptedHandler(_ => throw new InvalidOperationException("No request expected"));
        var adapter = new OpenAICompatibleAdapter(new ProviderHttpClient(handler));
        var body = """{"model":"gpt-6-astra","n":3,"messages":[],"tools":[{"type":"function","function":{"name":"f"}}]}""";
        var failure = (await adapter.SendAsync(Call("gpt-6-astra", body), Ct)).ShouldBeOfType<ProviderFailure>();
        failure.Kind.ShouldBe(FailureKind.ClientError);
        failure.Reason.ShouldBe("unsupported_content");
    }

    [Fact]
    public async Task Bridged_stream_reaches_the_client_as_chat_chunks()
    {
        var sse = string.Concat(StreamEvents().Select(e => $"data: {e}\n\n"));
        using var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var adapter = new OpenAICompatibleAdapter(new ProviderHttpClient(handler));
        var body = JsonNode.Parse(WithTools)!.AsObject();
        body["stream"] = true;
        using var result = (ProviderStreamResult)await adapter.SendAsync(Call("gpt-6-astra", body.ToJsonString(), stream: true), Ct);

        var events = new List<SseEvent>();
        await foreach (var evt in result.Events.WithCancellation(Ct))
        {
            events.Add(evt);
        }

        JsonNode.Parse(handler.Bodies[0])!["stream"]!.GetValue<bool>().ShouldBeTrue();
        events[^1].Data.ShouldBe(SseEvent.Done);
        events[..^1].Count(e => JsonNode.Parse(e.Data)!["choices"]?[0]?["delta"]?["tool_calls"] is not null).ShouldBe(3);
        result.Usage.ToTokenUsage(0).ShouldBe(new TokenUsage(20, 4, 30));
    }

    private const string WithTools = """
        {"model":"x","messages":[{"role":"user","content":"Weather in Umeå?"}],
         "tools":[{"type":"function","function":{"name":"get_weather","parameters":{"type":"object","properties":{"city":{"type":"string"}}}}}]}
        """;

    private static string[] StreamEvents() =>
    [
        """{"type":"response.created","response":{"id":"resp_1","model":"gpt-6-astra","created_at":1700,"status":"in_progress"}}""",
        """{"type":"response.output_item.added","output_index":0,"item":{"type":"reasoning","id":"rs_1"}}""",
        """{"type":"response.reasoning_summary_text.delta","item_id":"rs_1","delta":"Thinking"}""",
        """{"type":"response.output_text.delta","item_id":"msg_1","output_index":1,"delta":"Checking."}""",
        """{"type":"response.output_item.added","output_index":2,"item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"get_weather","arguments":""}}""",
        """{"type":"response.function_call_arguments.delta","item_id":"fc_1","output_index":2,"delta":"{\"city\":"}""",
        """{"type":"response.function_call_arguments.delta","item_id":"fc_1","output_index":2,"delta":"\"Umeå\"}"}""",
        """{"type":"response.function_call_arguments.done","item_id":"fc_1","output_index":2,"arguments":"{\"city\":\"Umeå\"}"}""",
        """{"type":"response.completed","response":{"id":"resp_1","status":"completed","usage":{"input_tokens":20,"input_tokens_details":{"cached_tokens":4},"output_tokens":30,"output_tokens_details":{"reasoning_tokens":25}}}}""",
    ];

    private static ProviderCall Call(string upstreamModel, string body, bool stream = false) => new(
        new ProviderAccount { Name = "foundry", BaseUrl = "https://foundry.example/openai/v1", Type = ProviderType.AzureAIFoundry },
        new ModelDeployment { Name = "foundry/" + upstreamModel, UpstreamModel = upstreamModel, ParameterProfile = ParameterProfile.OpenAIReasoning },
        GatewayEndpoint.ChatCompletions, JsonNode.Parse(body)!.AsObject(), stream, null, ClientRequestedStreamUsage: false);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return respond(request);
        }
    }
}
