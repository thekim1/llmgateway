using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;

namespace Ume.LlmGateway.Gateway.Tests;

public sealed class RealtimeTests(GatewayFixture fixture)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Live_transcription_is_relayed_with_the_alias_rewritten_and_billed_per_minute()
    {
        var key = await fixture.CreateKeyAsync();
        var alias = await fixture.CreateRouteAsync("live/whisper");
        using var socket = await fixture.ConnectRealtimeAsync(key, alias, query: "&intent=transcription", headers: new Dictionary<string, string> { ["OpenAI-Beta"] = "realtime=v1" });

        (await ReceiveAsync(socket))["type"]!.GetValue<string>().ShouldBe("session.created");
        await SendAsync(socket, new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = new JsonObject { ["type"] = "transcription", ["audio"] = new JsonObject { ["input"] = new JsonObject { ["transcription"] = new JsonObject { ["model"] = alias, ["language"] = "sv" } } } },
        });
        (await ReceiveAsync(socket))["type"]!.GetValue<string>().ShouldBe("session.updated");
        await AppendAudioAsync(socket, seconds: 2);
        await SendAsync(socket, new JsonObject { ["type"] = "input_audio_buffer.commit" });
        var completed = await ReceiveAsync(socket);
        completed["type"]!.GetValue<string>().ShouldBe("conversation.item.input_audio_transcription.completed");
        completed["transcript"]!.GetValue<string>().ShouldBe("Hej från Umeå");
        await CloseAsync(socket);

        var upstream = fixture.Realtime.Last("live");
        upstream.Path.ShouldBe("/live/v1/realtime");
        upstream.Query.ShouldBe("?intent=transcription"); // with intent the model goes in session.update, not the URL
        upstream.Authorization.ShouldBe("Bearer " + fixture.ProviderSecret);
        upstream.Beta.ShouldBe("realtime=v1");
        upstream.Received.First()["session"]!["audio"]!["input"]!["transcription"]!["model"]!.GetValue<string>().ShouldBe("live-whisper");

        var usage = await fixture.UsageForKeyAsync(key.Id);
        usage.Endpoint.ShouldBe(GatewayEndpoint.Realtime);
        usage.Outcome.ShouldBe(RequestOutcome.Success);
        usage.StatusCode.ShouldBe(200);
        usage.Streamed.ShouldBeTrue();
        usage.UpstreamModel.ShouldBe("live-whisper");
        usage.AudioSeconds.ShouldBe(2m);
        usage.CostUsd.ShouldBe(0.0002m); // 2 s at 0.006 USD per minute
    }

    [Fact]
    public async Task Live_interpreting_uses_the_translations_path_and_meters_audio_the_provider_does_not_report()
    {
        var key = await fixture.CreateKeyAsync();
        var alias = await fixture.CreateRouteAsync("live/interpret");
        using var socket = await fixture.ConnectRealtimeAsync(key, alias, "/v1/realtime/translations");

        await ReceiveAsync(socket); // session.created
        await SendAsync(socket, new JsonObject { ["type"] = "session.update", ["session"] = new JsonObject { ["audio"] = new JsonObject { ["output"] = new JsonObject { ["language"] = "en" } } } });
        await ReceiveAsync(socket); // session.updated
        await AppendAudioAsync(socket, seconds: 3, type: "session.input_audio_buffer.append");
        (await ReceiveAsync(socket))["type"]!.GetValue<string>().ShouldBe("session.output_transcript.delta");
        await CloseAsync(socket);

        var upstream = fixture.Realtime.Last("live");
        upstream.Path.ShouldBe("/live/v1/realtime/translations");
        upstream.Query.ShouldBe("?model=live-interpret");
        var usage = await fixture.UsageForKeyAsync(key.Id);
        usage.Endpoint.ShouldBe(GatewayEndpoint.RealtimeTranslations);
        usage.AudioSeconds.ShouldBe(3m); // counted from the appended PCM16 at 24 kHz
        usage.CostUsd.ShouldBe(0.001m); // 3 s at 0.02 USD per minute
    }

    [Fact]
    public async Task A_conversation_session_bills_audio_tokens_at_the_audio_price()
    {
        var key = await fixture.CreateKeyAsync();
        var alias = await fixture.CreateRouteAsync("live/realtime");
        using var socket = await fixture.ConnectRealtimeAsync(key, alias);

        await ReceiveAsync(socket);
        await SendAsync(socket, new JsonObject { ["type"] = "session.update", ["session"] = new JsonObject { ["model"] = "gpt-something-else", ["instructions"] = "Var kortfattad." } });
        (await ReceiveAsync(socket))["session"]!["model"]!.GetValue<string>().ShouldBe("live-realtime");
        await SendAsync(socket, new JsonObject { ["type"] = "response.create" });
        (await ReceiveAsync(socket))["type"]!.GetValue<string>().ShouldBe("response.done");
        await CloseAsync(socket);

        fixture.Realtime.Last("live").Query.ShouldBe("?model=live-realtime");
        var usage = await fixture.UsageForKeyAsync(key.Id);
        usage.InputTokens.ShouldBe(120);
        usage.OutputTokens.ShouldBe(30);
        // 20 text tokens at 4, 100 audio tokens at 32, 30 output tokens at 16 (USD per million)
        usage.CostUsd.ShouldBe(0.00376m);
    }

    [Fact]
    public async Task An_input_transcription_model_must_be_a_gateway_model_on_the_sessions_provider()
    {
        var key = await fixture.CreateKeyAsync();
        var alias = await fixture.CreateRouteAsync("live/realtime");
        var sameProvider = await fixture.CreateRouteAsync("live/whisper");
        var otherProvider = await fixture.CreateRouteAsync("liveonprem/whisper");
        using var socket = await fixture.ConnectRealtimeAsync(key, alias);
        await ReceiveAsync(socket);

        await SendAsync(socket, Transcription(otherProvider, "evt_1"));
        var refused = await ReceiveAsync(socket);
        refused["type"]!.GetValue<string>().ShouldBe("error");
        refused["error"]!["code"]!.GetValue<string>().ShouldBe("model_not_allowed");
        refused["error"]!["event_id"]!.GetValue<string>().ShouldBe("evt_1");

        await SendAsync(socket, Transcription(sameProvider, "evt_2"));
        (await ReceiveAsync(socket))["type"]!.GetValue<string>().ShouldBe("session.updated");
        await AppendAudioAsync(socket, seconds: 1);
        await SendAsync(socket, new JsonObject { ["type"] = "input_audio_buffer.commit" });
        await ReceiveAsync(socket);
        await CloseAsync(socket);

        var received = fixture.Realtime.Last("live").Received.Where(e => e["type"]!.GetValue<string>() == "session.update").ToList();
        received.Count.ShouldBe(1); // the refused update never left the gateway
        received[0]["session"]!["input_audio_transcription"]!["model"]!.GetValue<string>().ShouldBe("live-whisper");
        // The transcription's duration is billed at the whisper deployment's per-minute price.
        (await fixture.UsageForKeyAsync(key.Id)).CostUsd.ShouldBe(0.0001m);

        static JsonObject Transcription(string model, string eventId) => new()
        {
            ["type"] = "session.update",
            ["event_id"] = eventId,
            ["session"] = new JsonObject { ["input_audio_transcription"] = new JsonObject { ["model"] = model } },
        };
    }

    [Fact]
    public async Task Pii_in_text_events_follows_the_keys_policy()
    {
        var blocked = await fixture.CreateKeyAsync(k => k.PiiPolicy = PiiPolicy.Block);
        var alias = await fixture.CreateRouteAsync("live/realtime");
        using (var socket = await fixture.ConnectRealtimeAsync(blocked, alias))
        {
            await ReceiveAsync(socket);
            await SendAsync(socket, Message("Mitt personnummer är 19121212-1212"));
            var refused = await ReceiveAsync(socket);
            refused["error"]!["code"]!.GetValue<string>().ShouldBe("pii_blocked");
            await CloseAsync(socket);
        }

        fixture.Realtime.Last("live").Received.ShouldBeEmpty();
        var usage = await fixture.UsageForKeyAsync(blocked.Id);
        usage.PiiActionApplied.ShouldBe(PiiPolicy.Block);
        usage.PiiCategories.ShouldBe("Personnummer:1");

        var redacting = await fixture.CreateKeyAsync(k => k.PiiPolicy = PiiPolicy.Redact);
        using (var socket = await fixture.ConnectRealtimeAsync(redacting, alias))
        {
            await ReceiveAsync(socket);
            await SendAsync(socket, Message("Mitt personnummer är 19121212-1212"));
            await SendAsync(socket, new JsonObject { ["type"] = "response.create" });
            await ReceiveAsync(socket);
            await CloseAsync(socket);
        }

        var forwarded = fixture.Realtime.Last("live").Received.First()["item"]!["content"]![0]!["text"]!.GetValue<string>();
        forwarded.ShouldNotContain("19121212-1212");

        static JsonObject Message(string text) => new()
        {
            ["type"] = "conversation.item.create",
            ["item"] = new JsonObject
            {
                ["type"] = "message", ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = text }),
            },
        };
    }

    [Fact]
    public async Task A_failing_provider_falls_back_before_the_client_handshake_is_accepted()
    {
        var key = await fixture.CreateKeyAsync();
        var alias = await fixture.CreateRouteAsync("livefail/whisper", "live/whisper");
        using var socket = await fixture.ConnectRealtimeAsync(key, alias);
        (await ReceiveAsync(socket))["session"]!["model"]!.GetValue<string>().ShouldBe("live-whisper");
        await CloseAsync(socket);

        var usage = await fixture.UsageForKeyAsync(key.Id);
        usage.FallbackCount.ShouldBe(1);
        usage.ProviderName.ShouldBe("live");
    }

    [Fact]
    public async Task Sessions_are_refused_before_the_upgrade_with_the_usual_errors()
    {
        var key = await fixture.CreateKeyAsync();
        var whisper = await fixture.CreateRouteAsync("live/whisper");

        using var plain = await fixture.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/v1/realtime?model={whisper}") { Headers = { { "Authorization", "Bearer " + key.Secret } } }, Cancellation);
        plain.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await plain.Content.ReadAsStringAsync(Cancellation)).ShouldContain("WebSocket");

        (await Should.ThrowAsync<Exception>(() => fixture.ConnectRealtimeAsync(null, whisper))).Message.ShouldContain("401");
        (await Should.ThrowAsync<Exception>(() => fixture.ConnectRealtimeAsync(key, whisper, "/v1/realtime/translations"))).Message.ShouldContain("400");

        var imagesOnly = await fixture.CreateKeyAsync(k => k.AttachmentPolicy = AttachmentPolicy.ImagesOnly);
        (await Should.ThrowAsync<Exception>(() => fixture.ConnectRealtimeAsync(imagesOnly, whisper))).Message.ShouldContain("400");
        var refused = await fixture.UsageForKeyAsync(imagesOnly.Id);
        refused.ErrorCode.ShouldBe("attachment_not_allowed");
        refused.Endpoint.ShouldBe(GatewayEndpoint.Realtime);
    }

    [Fact]
    public async Task A_key_may_hold_only_its_share_of_open_sessions()
    {
        var key = await fixture.CreateKeyAsync();
        var alias = await fixture.CreateRouteAsync("live/whisper");
        using var first = await fixture.ConnectRealtimeAsync(key, alias);
        using var second = await fixture.ConnectRealtimeAsync(key, alias);

        (await Should.ThrowAsync<Exception>(() => fixture.ConnectRealtimeAsync(key, alias))).Message.ShouldContain("429");

        await ReceiveAsync(first);
        await CloseAsync(first);
        await WaitForAsync(async () =>
        {
            try
            {
                using var third = await fixture.ConnectRealtimeAsync(key, alias);
                await CloseAsync(third);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false; // the first session's slot is released once its accounting is done
            }
        });
    }

    [Fact]
    public async Task A_session_is_closed_when_its_budget_runs_out()
    {
        var key = await fixture.CreateKeyAsync();
        await fixture.AddBudgetAsync(BudgetScope.VirtualKey, key.Id, 10m);
        var alias = await fixture.CreateRouteAsync("live/realtime");
        using var socket = await fixture.ConnectRealtimeAsync(key, alias);
        await ReceiveAsync(socket);

        // A million input tokens cost 4 USD (40 SEK), far beyond the 10 SEK budget: the next check ends the session.
        await SendAsync(socket, new JsonObject { ["type"] = "response.create", ["response"] = new JsonObject { ["metadata"] = new JsonObject { ["input_tokens"] = 1_000_000 } } });
        (await ReceiveAsync(socket))["type"]!.GetValue<string>().ShouldBe("response.done");
        var error = await ReceiveAsync(socket, TimeSpan.FromSeconds(15));
        error["error"]!["code"]!.GetValue<string>().ShouldBe("budget_exceeded");
        (await ReceiveCloseAsync(socket)).ShouldBe(WebSocketCloseStatus.PolicyViolation);

        var usage = await fixture.UsageForKeyAsync(key.Id);
        usage.Outcome.ShouldBe(RequestOutcome.BudgetExceeded);
        usage.StatusCode.ShouldBe(402);
        usage.CostSek.ShouldBeGreaterThan(40m);
    }

    [Fact]
    public async Task A_dropped_provider_connection_ends_the_session_with_an_error_event()
    {
        var key = await fixture.CreateKeyAsync();
        var alias = await fixture.CreateRouteAsync("live/realtime");
        using var socket = await fixture.ConnectRealtimeAsync(key, alias);
        await ReceiveAsync(socket);
        await SendAsync(socket, new JsonObject { ["type"] = "test.drop" });

        (await ReceiveAsync(socket))["error"]!["code"]!.GetValue<string>().ShouldBe("provider_disconnected");
        (await ReceiveCloseAsync(socket)).ShouldBe(WebSocketCloseStatus.EndpointUnavailable);
        var usage = await fixture.UsageForKeyAsync(key.Id);
        usage.Outcome.ShouldBe(RequestOutcome.ProviderError);
        usage.ErrorCode.ShouldBe("stream_interrupted");
    }

    [Fact]
    public async Task Binary_frames_are_refused_so_audio_cannot_bypass_metering()
    {
        var key = await fixture.CreateKeyAsync();
        var alias = await fixture.CreateRouteAsync("live/whisper");
        using var socket = await fixture.ConnectRealtimeAsync(key, alias);
        await ReceiveAsync(socket);
        await socket.SendAsync(new byte[4800], WebSocketMessageType.Binary, true, Cancellation);

        (await ReceiveAsync(socket))["error"]!["code"]!.GetValue<string>().ShouldBe("invalid_request");
        await CloseAsync(socket);
        fixture.Realtime.Last("live").Received.ShouldBeEmpty();
    }

    private static async Task AppendAudioAsync(WebSocket socket, int seconds, string type = "input_audio_buffer.append")
    {
        var chunk = Convert.ToBase64String(new byte[4800]); // 100 ms of PCM16 at 24 kHz
        for (var i = 0; i < seconds * 10; i++)
        {
            await SendAsync(socket, new JsonObject { ["type"] = type, ["audio"] = chunk });
        }
    }

    private static Task SendAsync(WebSocket socket, JsonObject evt) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(evt), WebSocketMessageType.Text, true, Cancellation);

    private static async Task<JsonObject> ReceiveAsync(WebSocket socket, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
        var buffer = new byte[64 * 1024];
        var length = 0;
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), cts.Token);
            length += result.Count;
        }
        while (!result.EndOfMessage);

        result.MessageType.ShouldBe(WebSocketMessageType.Text);
        return (JsonObject)JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, length))!;
    }

    private static async Task<WebSocketCloseStatus?> ReceiveCloseAsync(WebSocket socket)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (socket.State == WebSocketState.CloseReceived)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token); // complete the handshake
                }

                return result.CloseStatus;
            }
        }
    }

    /// <summary>Closes from the client side and waits for the gateway's answer, reading whatever arrives meanwhile.</summary>
    private static async Task CloseAsync(WebSocket socket)
    {
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, Cancellation);
        await ReceiveCloseAsync(socket);
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100, Cancellation);
        }

        Assert.Fail("Condition not met in time.");
    }
}
