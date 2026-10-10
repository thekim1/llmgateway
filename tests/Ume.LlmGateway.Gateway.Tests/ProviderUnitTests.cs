using System.Text;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Providers;

namespace Ume.LlmGateway.Gateway.Tests;

public sealed class ProviderUnitTests
{
    [Fact]
    public void Anthropic_request_maps_system_tools_images_and_consecutive_roles()
    {
        var chat = JsonNode.Parse("""
            {"model":"alias","messages":[
              {"role":"system","content":"Instructions"},
              {"role":"developer","content":"More instructions"},
              {"role":"user","content":[{"type":"text","text":"Question"},{"type":"image_url","image_url":{"url":"data:image/png;base64,YWJj"}}]},
              {"role":"assistant","content":null,"tool_calls":[{"id":"call1","function":{"name":"lookup","arguments":"{\"value\":1}"}}]},
              {"role":"tool","tool_call_id":"call1","content":"Result"},
              {"role":"user","content":"Next question"}],
             "max_completion_tokens":123,"stream":true,"stop":["END"],
             "tools":[{"type":"function","function":{"name":"lookup","description":"Lookup","parameters":{"type":"object"}}}],
             "tool_choice":{"type":"function","function":{"name":"lookup"}}}
            """)!.AsObject();
        var output = AnthropicTranslator.ToMessagesRequest(chat, "claude-new");
        output["model"]!.GetValue<string>().ShouldBe("claude-new");
        output["system"]!.GetValue<string>().ShouldBe("Instructions\n\nMore instructions");
        output["max_tokens"]!.GetValue<int>().ShouldBe(123);
        output["stream"]!.GetValue<bool>().ShouldBeTrue();
        output["messages"]!.AsArray().Count.ShouldBe(3);
        output["messages"]![0]!["content"]![1]!["source"]!["data"]!.GetValue<string>().ShouldBe("YWJj");
        output["messages"]![1]!["content"]![0]!["input"]!["value"]!.GetValue<int>().ShouldBe(1);
        output["messages"]![2]!["content"]!.AsArray().Count.ShouldBe(2);
        output["tool_choice"]!["type"]!.GetValue<string>().ShouldBe("tool");
        output["tools"]![0]!["input_schema"]!["type"]!.GetValue<string>().ShouldBe("object");
        chat["model"]!.GetValue<string>().ShouldBe("alias");
    }

    [Fact]
    public void Anthropic_request_maps_pdf_and_text_files_to_documents()
    {
        var chat = JsonNode.Parse("""
            {"model":"alias","messages":[{"role":"user","content":[
              {"type":"file","file":{"filename":"beslut.pdf","file_data":"data:application/pdf;base64,JVBERi0="}},
              {"type":"file","file":{"filename":"notes.txt","file_data":"data:text/plain;base64,aGVqIGTDpQ=="}},
              {"type":"file","file":{"filename":"bare.pdf","file_data":"JVBERi0="}}]}]}
            """)!.AsObject();
        var blocks = AnthropicTranslator.ToMessagesRequest(chat, "claude")["messages"]![0]!["content"]!.AsArray();
        blocks.Count.ShouldBe(3);
        blocks[0]!["type"]!.GetValue<string>().ShouldBe("document");
        blocks[0]!["title"]!.GetValue<string>().ShouldBe("beslut.pdf");
        blocks[0]!["source"]!.ToJsonString().ShouldBe("""{"type":"base64","media_type":"application/pdf","data":"JVBERi0="}""");
        blocks[1]!["source"]!["type"]!.GetValue<string>().ShouldBe("text");
        blocks[1]!["source"]!["data"]!.GetValue<string>().ShouldBe("hej då");
        blocks[2]!["source"]!["media_type"]!.GetValue<string>().ShouldBe("application/pdf");
    }

    [Theory]
    [InlineData("""{"type":"input_audio","input_audio":{"data":"YWJj","format":"wav"}}""", "ljud")]
    [InlineData("""{"type":"video_url","video_url":{"url":"https://x/a.mp4"}}""", "video")]
    [InlineData("""{"type":"file","file":{"file_id":"file-abc"}}""", "file_id")]
    [InlineData("""{"type":"file","file":{"filename":"a.docx","file_data":"data:application/vnd.openxmlformats-officedocument.wordprocessingml.document;base64,UEs="}}""", "wordprocessingml")]
    [InlineData("""{"type":"something_new"}""", "something_new")]
    public void Anthropic_request_rejects_content_it_cannot_carry(string part, string mentions)
    {
        var chat = JsonNode.Parse($$"""{"model":"alias","messages":[{"role":"user","content":[{"type":"text","text":"Hej"},{{part}}]}]}""")!.AsObject();
        Should.Throw<NotSupportedException>(() => AnthropicTranslator.ToMessagesRequest(chat, "claude")).Message.ShouldContain(mentions);
    }

    [Theory]
    [InlineData("end_turn", "stop")]
    [InlineData("stop_sequence", "stop")]
    [InlineData("max_tokens", "length")]
    [InlineData("tool_use", "tool_calls")]
    [InlineData("refusal", "content_filter")]
    public void Anthropic_response_maps_tools_usage_and_stop_reason(string stopReason, string expected)
    {
        var message = JsonNode.Parse("""
            {"id":"msg1","model":"claude","content":[{"type":"text","text":"Answer"},{"type":"tool_use","id":"t1","name":"lookup","input":{"id":1}}],
             "usage":{"input_tokens":10,"cache_read_input_tokens":5,"cache_creation_input_tokens":2,"output_tokens":3}}
            """)!.AsObject();
        message["stop_reason"] = stopReason;
        var output = AnthropicTranslator.ToChatCompletion(message, 123);
        output["created"]!.GetValue<long>().ShouldBe(123);
        output["choices"]![0]!["finish_reason"]!.GetValue<string>().ShouldBe(expected);
        output["choices"]![0]!["message"]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>().ShouldBe("""{"id":1}""");
        output["usage"]!["prompt_tokens"]!.GetValue<long>().ShouldBe(17);
        output["usage"]!["prompt_tokens_details"]!["cached_tokens"]!.GetValue<long>().ShouldBe(5);
    }

    [Theory]
    [InlineData("""{"prompt_tokens":100,"completion_tokens":20,"prompt_tokens_details":{"cached_tokens":10}}""", 100, 10, 20)]
    [InlineData("""{"input_tokens":30,"output_tokens":4,"input_tokens_details":{"cached_tokens":5}}""", 30, 5, 4)]
    [InlineData("""{"input_tokens":10,"output_tokens":3,"cache_read_input_tokens":5,"cache_creation_input_tokens":2}""", 17, 5, 3)]
    [InlineData("""{"input_tokens":10,"output_tokens":3}""", 10, 0, 3)]
    public void Usage_parser_reads_each_provider_format(string json, long input, long cached, long output)
    {
        UsageParser.TryRead(JsonNode.Parse(json), out var usage).ShouldBeTrue();
        usage.ShouldBe(new TokenUsage(input, cached, output));
    }

    [Fact]
    public void Usage_parser_reads_speech_usage_by_duration_and_by_tokens()
    {
        UsageParser.TryRead(JsonNode.Parse("""{"type":"duration","seconds":12.5}"""), out var duration).ShouldBeTrue();
        duration.ShouldBe(new TokenUsage(0, 0, 0, 12.5m));

        UsageParser.TryRead(JsonNode.Parse("""{"type":"tokens","input_tokens":1000,"output_tokens":50,"input_token_details":{"audio_tokens":1000}}"""), out var tokens).ShouldBeTrue();
        tokens.ShouldBe(new TokenUsage(1000, 0, 50, AudioInputTokens: 1000));
    }

    [Theory]
    [InlineData("""{"prompt_tokens":100,"completion_tokens":40,"prompt_tokens_details":{"audio_tokens":60},"completion_tokens_details":{"audio_tokens":30}}""")]
    [InlineData("""{"input_tokens":100,"output_tokens":40,"input_token_details":{"audio_tokens":60},"output_token_details":{"audio_tokens":30}}""")]
    [InlineData("""{"input_tokens":100,"output_tokens":40,"input_tokens_details":{"audio_tokens":60},"output_tokens_details":{"audio_tokens":30}}""")]
    public void Usage_parser_reads_the_audio_part_of_the_tokens(string json)
    {
        UsageParser.TryRead(JsonNode.Parse(json), out var usage).ShouldBeTrue();
        usage.ShouldBe(new TokenUsage(100, 0, 40, AudioInputTokens: 60, AudioOutputTokens: 30));
    }

    [Theory]
    [InlineData("""{"text":"Hej","usage":{"type":"duration","seconds":7}}""", 0, 7, true)]
    [InlineData("""{"task":"transcribe","language":"swedish","duration":8.25,"text":"Hej","segments":[]}""", 0, 8.25, true)]
    [InlineData("""{"text":"Hej","usage":{"type":"tokens","input_tokens":100,"output_tokens":5}}""", 100, 0, true)]
    [InlineData("""{"text":"Hej"}""", 0, 0, false)]
    public void Transcription_usage_comes_from_usage_or_verbose_duration(string json, long input, double seconds, bool reported)
    {
        var (usage, wasReported) = ProviderJson.ReadTranscriptionUsage(Encoding.UTF8.GetBytes(json));
        wasReported.ShouldBe(reported);
        usage.InputTokens.ShouldBe(input);
        usage.AudioSeconds.ShouldBe((decimal)seconds);
    }

    [Fact]
    public void Stream_usage_falls_back_to_the_estimated_audio_length()
    {
        var acc = new UsageAccumulator();
        acc.ToTokenUsage(17, 1.5m).AudioSeconds.ShouldBe(1.5m);
        UsageParser.Apply(acc, new TokenUsage(1000, 0, 50));
        acc.ToTokenUsage(17, 1.5m).ShouldBe(new TokenUsage(1000, 0, 50, 1.5m));
        UsageParser.Apply(acc, new TokenUsage(0, 0, 0, 9m));
        acc.ToTokenUsage(17, 1.5m).AudioSeconds.ShouldBe(9m);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void Usage_parser_does_not_invent_usage(string json)
    {
        UsageParser.TryRead(JsonNode.Parse(json), out var usage).ShouldBeFalse();
        usage.ShouldBe(default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Anthropic_stream_maps_text_tool_deltas_finish_and_usage(bool includeUsage)
    {
        var usage = new UsageAccumulator();
        var translator = new AnthropicStreamTranslator(usage, includeUsage, 42);
        var events = new[]
        {
            """{"type":"message_start","message":{"id":"m1","model":"claude","usage":{"input_tokens":10,"cache_read_input_tokens":5,"output_tokens":0}}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Answer"}}""",
            """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"t1","name":"lookup"}}""",
            """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"id\":1}"}}""",
            """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":8}}""",
            """{"type":"message_stop"}""",
        };
        var result = events.SelectMany(e => translator.Translate(new SseEvent(null, e))).ToArray();
        result[^1].Data.ShouldBe(SseEvent.Done);
        var chunks = result[..^1].Select(e => JsonNode.Parse(e.Data)!).ToArray();
        chunks[0]["choices"]![0]!["delta"]!["role"]!.GetValue<string>().ShouldBe("assistant");
        chunks[1]["choices"]![0]!["delta"]!["content"]!.GetValue<string>().ShouldBe("Answer");
        chunks[2]["choices"]![0]!["delta"]!["tool_calls"]![0]!["index"]!.GetValue<int>().ShouldBe(0);
        chunks[3]["choices"]![0]!["delta"]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>().ShouldBe("""{"id":1}""");
        chunks.Any(c => c["usage"] is not null).ShouldBe(includeUsage);
        usage.ToTokenUsage(99).ShouldBe(new TokenUsage(15, 5, 8));
    }

    [Fact]
    public async Task Sse_reader_handles_comments_crlf_multiline_and_final_event()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(": heartbeat\r\nevent: delta\r\ndata: first\r\ndata: second\r\n\r\nid: 2\ndata: [DONE]"));
        var events = new List<SseEvent>();
        await foreach (var evt in SseReader.ReadAsync(stream, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }
        events.ShouldBe([new SseEvent("delta", "first\nsecond"), new SseEvent(null, "[DONE]")]);
        events[0].Format().ShouldBe("event: delta\ndata: first\ndata: second\n\n");
    }

    [Fact]
    public async Task Sse_reader_preserves_empty_data_lines()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data:\ndata: next\n\ndata:\n\n"));
        var events = new List<SseEvent>();
        await foreach (var evt in SseReader.ReadAsync(stream, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }
        events.ShouldBe([new SseEvent(null, "\nnext"), new SseEvent(null, "")]);
    }
}
