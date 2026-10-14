using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Fake LLM provider for demos and automated tests. Implements just enough of the OpenAI (chat, embeddings,
// responses, models, audio transcriptions/translations) and Anthropic (messages) APIs. Speech to text pretends the
// upload is 128 kbps audio: fake-whisper reports usage by duration like whisper-1, fake-transcribe by tokens and
// streams like gpt-4o-transcribe. Behaviour is controlled by the requested model name:
//   fake-fail-503 → always 503, fake-fail-429 → always 429, fake-slow → 3 s delay, anything else → answer.
// Chat, responses and messages answers can be given a real provider's timing (time to first token, pace per word,
// jitter, longer answers, stalls) with the FakeLlm:Latency section; see FakeLatency. Off by default.
// With stream_options.include_usage, chat chunks carry "usage":null as OpenAI's do.
// Images (/v1/images/generations, /v1/images/edits): fake-image bills per picture, fake-image-token by tokens.
// Live audio (WebSocket, OpenAI Realtime protocol) on /v1/realtime and /v1/realtime/translations: transcription
// sessions answer each committed buffer (fake-whisper by duration, fake-transcribe by tokens), fake-realtime answers
// response.create in text, and translation sessions (fake-interpret) send transcript deltas per second of audio.
// NOT for production. It echoes the last user message so demos can show PII redaction.
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
var latency = builder.Configuration.GetSection(Ume.LlmGateway.FakeLlm.FakeLatency.SectionName).Get<Ume.LlmGateway.FakeLlm.FakeLatency>()
    ?? new Ume.LlmGateway.FakeLlm.FakeLatency();
var app = builder.Build();
app.MapDefaultEndpoints();
app.UseWebSockets();

app.MapGet("/", () => "Ume fake LLM – endast för test och demo.");
app.MapGet("/v1/models", () => Results.Json(new
{
    @object = "list",
    data = new[] { "fake-chat", "fake-embed", "fake-claude", "fake-whisper", "fake-transcribe", "fake-realtime", "fake-interpret", "fake-fail-503", "fake-fail-429", "fake-slow" }
        .Select(id => new { id, @object = "model", owned_by = "ume-fake" }),
}));

app.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
{
    var body = await ReadAsync(ctx);
    var model = body["model"]?.GetValue<string>() ?? "fake-chat";
    if (await FailureAsync(ctx, model) is { } failure)
    {
        return failure;
    }

    var prompt = LastUserText(body["messages"]);
    var answer = latency.Pad(Answer(model, prompt));
    var promptTokens = Tokens(AllText(body["messages"]));
    var completionTokens = Tokens(answer);
    var id = "chatcmpl-" + Guid.NewGuid().ToString("N")[..12];
    var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    if (body["stream"]?.GetValue<bool>() == true)
    {
        var includeUsage = body["stream_options"]?["include_usage"]?.GetValue<bool>() == true;
        Dictionary<string, object?> Chunk(object delta, string? finishReason)
        {
            var chunk = new Dictionary<string, object?>
            {
                ["id"] = id, ["object"] = "chat.completion.chunk", ["created"] = created, ["model"] = model,
                ["choices"] = new[] { new { index = 0, delta, finish_reason = finishReason } },
            };
            if (includeUsage)
            {
                chunk["usage"] = null; // as OpenAI and Azure send it in every chunk but the last
            }

            return chunk;
        }

        await StartSse(ctx);
        await Sse(ctx, null, Chunk(new { role = "assistant", content = "" }, null));
        await StreamWordsAsync(latency, answer, promptTokens, word => Sse(ctx, null, Chunk(new { content = word }, null)), ctx.RequestAborted);
        await Sse(ctx, null, Chunk(new { }, "stop"));
        if (includeUsage)
        {
            await Sse(ctx, null, new { id, @object = "chat.completion.chunk", created, model, choices = Array.Empty<object>(), usage = new { prompt_tokens = promptTokens, completion_tokens = completionTokens, total_tokens = promptTokens + completionTokens } });
        }

        await ctx.Response.WriteAsync("data: [DONE]\n\n");
        return Results.Empty;
    }

    await latency.WholeAnswerAsync(Words(answer).Count(), promptTokens, ctx.RequestAborted);
    return Results.Json(new
    {
        id,
        @object = "chat.completion",
        created,
        model,
        choices = new[] { new { index = 0, message = new { role = "assistant", content = answer }, finish_reason = "stop" } },
        usage = new { prompt_tokens = promptTokens, completion_tokens = completionTokens, total_tokens = promptTokens + completionTokens, prompt_tokens_details = new { cached_tokens = 0 } },
    });
});

app.MapPost("/v1/embeddings", async (HttpContext ctx) =>
{
    var body = await ReadAsync(ctx);
    var model = body["model"]?.GetValue<string>() ?? "fake-embed";
    if (await FailureAsync(ctx, model) is { } failure)
    {
        return failure;
    }

    var inputs = body["input"] switch
    {
        JsonArray a => a.Select(n => n?.ToString() ?? string.Empty).ToList(),
        { } n => [n.ToString()],
        _ => [string.Empty],
    };
    var tokens = inputs.Sum(Tokens);
    return Results.Json(new
    {
        @object = "list",
        model,
        data = inputs.Select((text, i) => new { @object = "embedding", index = i, embedding = Vector(text) }),
        usage = new { prompt_tokens = tokens, total_tokens = tokens },
    });
});

app.MapPost("/v1/responses", async (HttpContext ctx) =>
{
    var body = await ReadAsync(ctx);
    var model = body["model"]?.GetValue<string>() ?? "fake-chat";
    if (await FailureAsync(ctx, model) is { } failure)
    {
        return failure;
    }

    var input = body["input"] is JsonValue v ? v.ToString() : AllText(body["input"]);
    var answer = latency.Pad(Answer(model, input));
    var usage = new { input_tokens = Tokens(input), output_tokens = Tokens(answer), total_tokens = Tokens(input) + Tokens(answer), input_tokens_details = new { cached_tokens = 0 } };
    var id = "resp_" + Guid.NewGuid().ToString("N")[..12];
    var response = new
    {
        id,
        @object = "response",
        status = "completed",
        model,
        output = new[] { new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = answer } } } },
        usage,
    };

    if (body["stream"]?.GetValue<bool>() == true)
    {
        await StartSse(ctx);
        await Sse(ctx, "response.created", new { type = "response.created", response = new { id, status = "in_progress", model } });
        await StreamWordsAsync(latency, answer, Tokens(input), word => Sse(ctx, "response.output_text.delta", new { type = "response.output_text.delta", delta = word }), ctx.RequestAborted);
        await Sse(ctx, "response.completed", new { type = "response.completed", response });
        return Results.Empty;
    }

    await latency.WholeAnswerAsync(Words(answer).Count(), Tokens(input), ctx.RequestAborted);
    return Results.Json(response);
});

app.MapPost("/v1/messages", async (HttpContext ctx) =>
{
    var body = await ReadAsync(ctx);
    var model = body["model"]?.GetValue<string>() ?? "fake-claude";
    if (await FailureAsync(ctx, model, anthropic: true) is { } failure)
    {
        return failure;
    }

    var prompt = LastUserText(body["messages"]);
    var answer = latency.Pad(Answer(model, prompt));
    var inputTokens = Tokens(AllText(body["messages"]) + body["system"]);
    var outputTokens = Tokens(answer);
    var id = "msg_" + Guid.NewGuid().ToString("N")[..12];

    if (body["stream"]?.GetValue<bool>() == true)
    {
        await StartSse(ctx);
        await Sse(ctx, "message_start", new { type = "message_start", message = new { id, type = "message", role = "assistant", model, content = Array.Empty<object>(), usage = new { input_tokens = inputTokens, output_tokens = 1, cache_read_input_tokens = 0, cache_creation_input_tokens = 0 } } });
        await Sse(ctx, "content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } });
        await StreamWordsAsync(latency, answer, inputTokens, word => Sse(ctx, "content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = word } }), ctx.RequestAborted);

        await Sse(ctx, "content_block_stop", new { type = "content_block_stop", index = 0 });
        await Sse(ctx, "message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn" }, usage = new { output_tokens = outputTokens } });
        await Sse(ctx, "message_stop", new { type = "message_stop" });
        return Results.Empty;
    }

    await latency.WholeAnswerAsync(Words(answer).Count(), inputTokens, ctx.RequestAborted);
    return Results.Json(new
    {
        id,
        type = "message",
        role = "assistant",
        model,
        content = new[] { new { type = "text", text = answer } },
        stop_reason = "end_turn",
        usage = new { input_tokens = inputTokens, output_tokens = outputTokens, cache_read_input_tokens = 0, cache_creation_input_tokens = 0 },
    });
});

// Cast to Delegate: a lambda taking only HttpContext would bind as a RequestDelegate and drop the IResult.
app.MapPost("/v1/audio/transcriptions", (Delegate)((HttpContext ctx) => TranscribeAsync(ctx, translate: false)));
app.MapPost("/v1/audio/translations", (Delegate)((HttpContext ctx) => TranscribeAsync(ctx, translate: true)));

app.MapPost("/v1/images/generations", (Delegate)((HttpContext ctx) => ImagesAsync(ctx, edit: false)));
app.MapPost("/v1/images/edits", (Delegate)((HttpContext ctx) => ImagesAsync(ctx, edit: true)));

app.MapGet("/v1/realtime", (Delegate)((HttpContext ctx) => RealtimeAsync(ctx, translations: false)));
app.MapGet("/v1/realtime/translations", (Delegate)((HttpContext ctx) => RealtimeAsync(ctx, translations: true)));

app.Run();

static async Task<IResult> RealtimeAsync(HttpContext ctx, bool translations)
{
    var model = ctx.Request.Query["model"].ToString() is { Length: > 0 } m ? m : translations ? "fake-interpret" : "fake-whisper";
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        return Results.Json(new { error = new { message = "Expected a WebSocket upgrade", type = "invalid_request_error" } }, statusCode: 400);
    }

    if (await FailureAsync(ctx, model) is { } failure)
    {
        return failure;
    }

    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    var ct = ctx.RequestAborted;
    var session = new JsonObject
    {
        ["id"] = "sess_" + Guid.NewGuid().ToString("N")[..12],
        ["type"] = translations ? "translation" : model.Contains("realtime", StringComparison.Ordinal) ? "realtime" : "transcription",
        ["model"] = model,
        ["audio"] = new JsonObject { ["input"] = new JsonObject { ["format"] = new JsonObject { ["type"] = "audio/pcm", ["rate"] = 24_000 } } },
    };
    async Task Send(object payload) =>
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload), System.Net.WebSockets.WebSocketMessageType.Text, true, ct);
    await Send(new { type = "session.created", event_id = "event_1", session });

    long buffered = 0, translated = 0;
    var buffer = new byte[1024 * 1024];
    while (socket.State == System.Net.WebSockets.WebSocketState.Open)
    {
        var length = 0;
        System.Net.WebSockets.ValueWebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(buffer.AsMemory(length), ct);
            length += received.Count;
        }
        while (!received.EndOfMessage && length < buffer.Length);

        if (received.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
        {
            await socket.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            break;
        }

        if (JsonNode.Parse(buffer.AsSpan(0, length)) is not JsonObject evt)
        {
            continue;
        }

        switch (evt["type"]?.GetValue<string>())
        {
            case "session.update" or "transcription_session.update":
                if (evt["session"] is JsonObject update)
                {
                    foreach (var (name, value) in update)
                    {
                        session[name] = value?.DeepClone();
                    }
                }

                await Send(new { type = "session.updated", event_id = "event_2", session });
                break;

            case "input_audio_buffer.append" or "session.input_audio_buffer.append":
                buffered += (evt["audio"]?.GetValue<string>().Length ?? 0) * 3 / 4;
                if (translations && buffered - translated >= 48_000)
                {
                    translated = buffered;
                    await Send(new { type = "session.input_transcript.delta", delta = "(tal) " });
                    await Send(new { type = "session.output_transcript.delta", delta = "(tolkning) " });
                }

                break;

            case "input_audio_buffer.commit":
                var seconds = Math.Round(buffered / 48_000.0, 3);
                buffered = 0;
                var item = "item_" + Guid.NewGuid().ToString("N")[..12];
                var transcript = $"Testtranskribering av {seconds:0.0} s från {model}.";
                var tokens = (int)Math.Ceiling(seconds * 17);
                object usage = model.Contains("transcribe", StringComparison.Ordinal)
                    ? new { type = "tokens", input_tokens = tokens, output_tokens = Tokens(transcript), total_tokens = tokens + Tokens(transcript), input_token_details = new { audio_tokens = tokens, text_tokens = 0 } }
                    : new { type = "duration", seconds };
                await Send(new { type = "input_audio_buffer.committed", item_id = item });
                await Send(new { type = "conversation.item.input_audio_transcription.delta", item_id = item, content_index = 0, delta = transcript });
                await Send(new { type = "conversation.item.input_audio_transcription.completed", item_id = item, content_index = 0, transcript, usage });
                break;

            case "response.create":
                var answer = "Test answer from the fake realtime model.";
                await Send(new { type = "response.output_text.delta", delta = answer });
                await Send(new
                {
                    type = "response.done",
                    response = new
                    {
                        status = "completed",
                        usage = new
                        {
                            total_tokens = 120 + Tokens(answer), input_tokens = 120, output_tokens = Tokens(answer),
                            input_token_details = new { cached_tokens = 0, text_tokens = 20, audio_tokens = 100 },
                            output_token_details = new { text_tokens = Tokens(answer), audio_tokens = 0 },
                        },
                    },
                });
                break;

            case "session.close":
                await socket.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                break;
        }
    }

    return Results.Empty;
}

// Images: n tiny PNGs as base64. fake-image answers like DALL·E (no usage, billed per picture), fake-image-token like
// gpt-image-1 (usage in tokens). Edits take the pictures in image / image[] and an optional mask.
static async Task<IResult> ImagesAsync(HttpContext ctx, bool edit)
{
    const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";
    string model, prompt, n;
    var images = 0;
    if (edit)
    {
        if (!ctx.Request.HasFormContentType)
        {
            return Results.Json(new { error = new { message = "Expected multipart/form-data", type = "invalid_request_error" } }, statusCode: 400);
        }

        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        model = form["model"].ToString() is { Length: > 0 } fm ? fm : "fake-image";
        prompt = form["prompt"].ToString();
        n = form["n"].ToString();
        images = form.Files.Count(f => f.Name is "image" or "image[]");
        if (images == 0)
        {
            return Results.Json(new { error = new { message = "image is required", type = "invalid_request_error" } }, statusCode: 400);
        }
    }
    else
    {
        var body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted) as JsonObject ?? [];
        model = body["model"]?.GetValue<string>() is { Length: > 0 } bm ? bm : "fake-image";
        prompt = body["prompt"]?.GetValue<string>() ?? string.Empty;
        n = body["n"]?.ToJsonString() ?? string.Empty;
    }

    if (await FailureAsync(ctx, model) is { } failure)
    {
        return failure;
    }

    if (string.IsNullOrWhiteSpace(prompt))
    {
        return Results.Json(new { error = new { message = "prompt is required", type = "invalid_request_error" } }, statusCode: 400);
    }

    var count = int.TryParse(n.Trim('"'), out var parsed) ? Math.Clamp(parsed, 1, 10) : 1;
    var data = Enumerable.Range(0, count).Select(_ => new { b64_json = Png, revised_prompt = prompt }).ToArray();
    if (!model.Contains("token", StringComparison.Ordinal))
    {
        return Results.Json(new { created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), data });
    }

    var inputTokens = Tokens(prompt) + images * 800;
    var outputTokens = count * 1056;
    return Results.Json(new
    {
        created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        data,
        usage = new
        {
            input_tokens = inputTokens,
            output_tokens = outputTokens,
            total_tokens = inputTokens + outputTokens,
            input_tokens_details = new { text_tokens = Tokens(prompt), image_tokens = images * 800 },
        },
    });
}

static async Task<IResult> TranscribeAsync(HttpContext ctx, bool translate)
{
    if (!ctx.Request.HasFormContentType)
    {
        return Results.Json(new { error = new { message = "Expected multipart/form-data", type = "invalid_request_error" } }, statusCode: 400);
    }

    var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
    var model = form["model"].ToString() is { Length: > 0 } m ? m : "fake-whisper";
    if (await FailureAsync(ctx, model) is { } failure)
    {
        return failure;
    }

    if (form.Files.GetFile("file") is not { Length: > 0 } file)
    {
        return Results.Json(new { error = new { message = "file is required", type = "invalid_request_error" } }, statusCode: 400);
    }

    var seconds = Math.Max(0.1, Math.Round(file.Length / 16_000.0, 1));
    var text = translate
        ? $"Test translation of {file.FileName} ({seconds:0.0} s) by {model}."
        : $"Testtranskribering av {file.FileName} ({seconds:0.0} s) från {model}." + (form["prompt"].ToString() is { Length: > 0 } prompt ? $" Prompt: {prompt}" : string.Empty);
    var tokenBilled = model.Contains("transcribe", StringComparison.Ordinal);
    var inputTokens = (int)Math.Ceiling(seconds * 17);
    var outputTokens = Tokens(text);
    object usage = tokenBilled
        ? new { type = "tokens", input_tokens = inputTokens, output_tokens = outputTokens, total_tokens = inputTokens + outputTokens, input_token_details = new { audio_tokens = inputTokens, text_tokens = 0 } }
        : new { type = "duration", seconds };

    if (tokenBilled && form["stream"] == "true")
    {
        await StartSse(ctx);
        foreach (var word in Words(text))
        {
            await Sse(ctx, null, new { type = "transcript.text.delta", delta = word });
        }

        await Sse(ctx, null, new { type = "transcript.text.done", text, usage });
        return Results.Empty;
    }

    var end = TimeSpan.FromSeconds(seconds);
    return form["response_format"].ToString() switch
    {
        "text" => Results.Text(text + "\n", "text/plain; charset=utf-8"),
        "srt" => Results.Text($"1\n00:00:00,000 --> {end:hh\\:mm\\:ss\\,fff}\n{text}\n\n", "application/x-subrip; charset=utf-8"),
        "vtt" => Results.Text($"WEBVTT\n\n00:00:00.000 --> {end:hh\\:mm\\:ss\\.fff}\n{text}\n\n", "text/vtt; charset=utf-8"),
        "verbose_json" => Results.Json(new { task = translate ? "translate" : "transcribe", language = translate ? "english" : "swedish", duration = seconds, text, segments = new[] { new { id = 0, start = 0.0, end = seconds, text } } }),
        _ => Results.Json(new { text, usage }),
    };
}

static async Task<JsonObject> ReadAsync(HttpContext ctx) =>
    await JsonNode.ParseAsync(ctx.Request.Body) as JsonObject ?? [];

static async Task<IResult?> FailureAsync(HttpContext ctx, string model, bool anthropic = false)
{
    switch (model)
    {
        case "fake-fail-503":
            return anthropic
                ? Results.Json(new { type = "error", error = new { type = "overloaded_error", message = "Fake overload" } }, statusCode: 529)
                : Results.Json(new { error = new { message = "Fake provider unavailable", type = "server_error" } }, statusCode: 503);
        case "fake-fail-429":
            ctx.Response.Headers.RetryAfter = "1";
            return Results.Json(new { error = new { message = "Fake rate limit", type = "rate_limit_error" } }, statusCode: 429);
        case "fake-slow":
            await Task.Delay(TimeSpan.FromSeconds(3), ctx.RequestAborted);
            return null;
        default:
            return null;
    }
}

static string Answer(string model, string prompt)
{
    var shortPrompt = prompt.Length > 200 ? prompt[..200] + "…" : prompt;
    return $"Hej! Detta är ett testsvar från {model}. Mottaget meddelande: \"{shortPrompt}\"";
}

static string LastUserText(JsonNode? messages) =>
    messages is JsonArray a
        ? a.OfType<JsonObject>().LastOrDefault(m => m["role"]?.GetValue<string>() == "user") is { } m ? TextOf(m["content"]) : string.Empty
        : string.Empty;

static string AllText(JsonNode? messages) =>
    messages is JsonArray a ? string.Join("\n", a.OfType<JsonObject>().Select(m => TextOf(m["content"]))) : string.Empty;

static string TextOf(JsonNode? content) => content switch
{
    JsonValue v when v.TryGetValue<string>(out var s) => s,
    JsonArray parts => string.Join("\n", parts.OfType<JsonObject>().Select(p => p["text"]?.GetValue<string>() ?? string.Empty)),
    _ => string.Empty,
};

static int Tokens(string text) => Math.Max(1, text.Length / 4);

static IEnumerable<string> Words(string text)
{
    var parts = text.Split(' ');
    for (var i = 0; i < parts.Length; i++)
    {
        yield return i == 0 ? parts[i] : " " + parts[i];
    }
}

static float[] Vector(string text)
{
    var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text));
    return [.. hash.Take(8).Select(b => (b - 128) / 128f)];
}

// Emits the answer one word per event, paced by the latency profile (instant when it is off).
static async Task StreamWordsAsync(Ume.LlmGateway.FakeLlm.FakeLatency latency, string answer, long inputTokens, Func<string, Task> emit, CancellationToken ct)
{
    var first = true;
    foreach (var word in Words(answer))
    {
        await (first ? latency.FirstTokenAsync(inputTokens, ct) : latency.NextTokenAsync(ct));
        first = false;
        await emit(word);
    }
}

static async Task StartSse(HttpContext ctx)
{
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    await ctx.Response.Body.FlushAsync();
}

static async Task Sse(HttpContext ctx, string? eventName, object payload)
{
    var sb = new StringBuilder();
    if (eventName is not null)
    {
        sb.Append("event: ").Append(eventName).Append('\n');
    }

    sb.Append("data: ").Append(JsonSerializer.Serialize(payload)).Append("\n\n");
    await ctx.Response.WriteAsync(sb.ToString());
    await ctx.Response.Body.FlushAsync();
}

public partial class Program;
