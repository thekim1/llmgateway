using System.Text;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Gateway.Pipeline.Realtime;
using Ume.LlmGateway.Infrastructure.Providers;

namespace Ume.LlmGateway.Gateway.Tests;

public sealed class RealtimeProtocolTests
{
    [Theory]
    [InlineData("""{"type":"input_audio_buffer.append","audio":"AAAAAAAA"}""", RealtimeClientEvent.AudioAppend, 6)]
    [InlineData("""{"audio":"AAAAAA==","event_id":"e1","type":"session.input_audio_buffer.append"}""", RealtimeClientEvent.AudioAppend, 4)]
    [InlineData("""{"type":"input_audio_buffer.append","audio":"AAA+AA=="}""", RealtimeClientEvent.AudioAppend, 4)]
    [InlineData("""{"type":"session.update","session":{"model":"x"}}""", RealtimeClientEvent.SessionUpdate, 0)]
    [InlineData("""{"type":"transcription_session.update","session":{}}""", RealtimeClientEvent.SessionUpdate, 0)]
    [InlineData("""{"type":"conversation.item.create","audio":"AAAA"}""", RealtimeClientEvent.ConversationItemCreate, 0)]
    [InlineData("""{"type":"response.create"}""", RealtimeClientEvent.ResponseCreate, 0)]
    [InlineData("""{"type":"input_audio_buffer.commit"}""", RealtimeClientEvent.Other, 0)]
    [InlineData("""{"type":"input_audio_buffer.append","audio":""", RealtimeClientEvent.Other, 0)]
    [InlineData("""[1,2]""", RealtimeClientEvent.Other, 0)]
    public void Client_events_are_classified_and_audio_measured_without_decoding(string json, RealtimeClientEvent kind, long audioBytes)
    {
        var (actualKind, actualAudio) = RealtimeEvents.InspectClient(Encoding.UTF8.GetBytes(json));
        actualKind.ShouldBe(kind);
        actualAudio.ShouldBe(audioBytes);
    }

    [Theory]
    [InlineData("""{"type":"response.output_audio.delta","delta":"AAAA"}""", RealtimeServerEvent.AudioDelta)]
    [InlineData("""{"event_id":"e","type":"response.audio.delta","delta":"AAAA"}""", RealtimeServerEvent.AudioDelta)]
    [InlineData("""{"type":"session.output_audio.delta","delta":"AAAA"}""", RealtimeServerEvent.AudioDelta)]
    [InlineData("""{"type":"session.updated","session":{}}""", RealtimeServerEvent.SessionState)]
    [InlineData("""{"type":"conversation.item.input_audio_transcription.completed","usage":{}}""", RealtimeServerEvent.InputTranscriptionCompleted)]
    [InlineData("""{"type":"response.done"}""", RealtimeServerEvent.Other)]
    [InlineData("""not json""", RealtimeServerEvent.Other)]
    public void Server_events_are_classified(string json, RealtimeServerEvent kind) =>
        RealtimeEvents.InspectServer(Encoding.UTF8.GetBytes(json)).ShouldBe(kind);

    [Theory]
    [InlineData("""{"audio":{"input":{"format":{"type":"audio/pcm","rate":24000}}}}""", 48_000)]
    [InlineData("""{"audio":{"input":{"format":{"type":"audio/pcm","rate":16000}}}}""", 32_000)]
    [InlineData("""{"audio":{"input":{"format":{"type":"audio/pcmu"}}}}""", 8_000)]
    [InlineData("""{"input_audio_format":"pcm16"}""", 48_000)]
    [InlineData("""{"input_audio_format":"g711_alaw"}""", 8_000)]
    [InlineData("""{"instructions":"x"}""", null)]
    public void The_input_format_gives_the_audio_rate(string session, int? bytesPerSecond) =>
        RealtimeEvents.InputBytesPerSecond(JsonNode.Parse(session) as JsonObject).ShouldBe(bytesPerSecond);

    [Fact]
    public void Usage_is_read_from_response_done_and_from_transcription_events()
    {
        RealtimeEvents.TryReadUsage((JsonObject)JsonNode.Parse("""
            {"type":"response.done","response":{"usage":{"input_tokens":120,"output_tokens":30,
             "input_token_details":{"cached_tokens":10,"text_tokens":20,"audio_tokens":100},"output_token_details":{"text_tokens":10,"audio_tokens":20}}}}
            """)!, out var conversation).ShouldBeTrue();
        conversation.ShouldBe(new TokenUsage(120, 10, 30, AudioInputTokens: 100, AudioOutputTokens: 20));

        RealtimeEvents.TryReadUsage((JsonObject)JsonNode.Parse("""{"type":"conversation.item.input_audio_transcription.completed","usage":{"type":"duration","seconds":2.5}}""")!, out var duration).ShouldBeTrue();
        duration.AudioSeconds.ShouldBe(2.5m);
    }

    [Fact]
    public void The_meter_prefers_reported_duration_and_keeps_input_transcription_apart()
    {
        var meter = new RealtimeMeter();
        meter.AddAudio(48_000);
        meter.SetInputBytesPerSecond(8_000);
        meter.AddAudio(8_000);
        meter.Snapshot().Session.AudioSeconds.ShouldBe(2m); // 1 s of PCM16 at 24 kHz, then 1 s of G.711

        meter.Add(new TokenUsage(100, 0, 10), transcriptionModel: false);
        meter.Add(new TokenUsage(0, 0, 0, 1.5m), transcriptionModel: true);
        var (session, transcription) = meter.Snapshot();
        session.ShouldBe(new TokenUsage(100, 0, 10, 2m));
        transcription.AudioSeconds.ShouldBe(1.5m);

        meter.Add(new TokenUsage(0, 0, 0, 5m), transcriptionModel: false);
        meter.Snapshot().Session.AudioSeconds.ShouldBe(5m); // the provider's own count wins
    }

    [Fact]
    public void Conversation_items_count_their_audio_and_session_models_are_the_gateways()
    {
        var session = new ModelDeployment { Name = "p/realtime", UpstreamModel = "gpt-realtime", Kind = ModelKind.Realtime };
        var policy = new RealtimeEventPolicy(session, _ => null, PiiPolicy.Off, onPrem: false);

        var item = (JsonObject)JsonNode.Parse("""{"type":"conversation.item.create","item":{"content":[{"type":"input_audio","audio":"AAAAAAAA"}]}}""")!;
        policy.Apply(item, RealtimeClientEvent.ConversationItemCreate).AudioBytes.ShouldBe(6);

        var update = (JsonObject)JsonNode.Parse("""{"type":"session.update","session":{"model":"gpt-4o-realtime-preview"}}""")!;
        policy.Apply(update, RealtimeClientEvent.SessionUpdate).Rejection.ShouldBeNull();
        update["session"]!["model"]!.GetValue<string>().ShouldBe("gpt-realtime");

        var unknown = (JsonObject)JsonNode.Parse("""{"type":"session.update","session":{"audio":{"input":{"transcription":{"model":"whisper-1"}}}}}""")!;
        policy.Apply(unknown, RealtimeClientEvent.SessionUpdate).Rejection!.Code.ShouldBe("model_not_allowed");
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", "", "wss://api.openai.com/v1/realtime?model=gpt-realtime")]
    [InlineData("https://res.openai.azure.com/openai/v1", "", "wss://res.openai.azure.com/openai/v1/realtime?model=gpt-realtime")]
    [InlineData("https://res.services.ai.azure.com/voice-live?api-version=2026-04-10", "", "wss://res.services.ai.azure.com/voice-live/realtime?api-version=2026-04-10&model=gpt-realtime")]
    [InlineData("http://vllm.local:8000/v1", "", "ws://vllm.local:8000/v1/realtime?model=gpt-realtime")]
    [InlineData("https://api.openai.com/v1", "intent=transcription", "wss://api.openai.com/v1/realtime?intent=transcription")]
    [InlineData("https://res.example/x?api-version=1", "api-version=2&api-key=secret&model=other&language=sv", "wss://res.example/x/realtime?api-version=1&language=sv&model=gpt-realtime")]
    public void The_upstream_session_url_is_the_base_url_plus_realtime(string baseUrl, string clientQuery, string expected)
    {
        var query = clientQuery.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=')).Select(p => KeyValuePair.Create(p[0], p[1])).ToList();
        RealtimeConnector.BuildUri(baseUrl, GatewayEndpoint.Realtime, "gpt-realtime", query).ShouldBe(new Uri(expected));
    }

    [Fact]
    public void Translations_have_their_own_path() =>
        RealtimeConnector.BuildUri("https://api.openai.com/v1", GatewayEndpoint.RealtimeTranslations, "gpt-realtime-translate", [])
            .ShouldBe(new Uri("wss://api.openai.com/v1/realtime/translations?model=gpt-realtime-translate"));

    [Fact]
    public void Http_paths_keep_a_query_string_on_the_base_url() =>
        ProviderTransport.BuildUri("https://res.openai.azure.com/openai?api-version=2025-04-01-preview", "chat/completions")
            .ShouldBe(new Uri("https://res.openai.azure.com/openai/chat/completions?api-version=2025-04-01-preview"));
}
