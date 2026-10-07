using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Fake LLM provider for demos and automated tests. Implements just enough of the OpenAI (chat, embeddings,
// responses, models) and Anthropic (messages) APIs. Behaviour is controlled by the requested model name:
//   fake-fail-503 → always 503, fake-fail-429 → always 429, fake-slow → 3 s delay, anything else → answer.
// NOT for production. It echoes the last user message so demos can show PII redaction.
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
var app = builder.Build();
app.MapDefaultEndpoints();

app.MapGet("/", () => "Ume fake LLM – endast för test och demo.");
app.MapGet("/v1/models", () => Results.Json(new
{
    @object = "list",
    data = new[] { "fake-chat", "fake-embed", "fake-claude", "fake-fail-503", "fake-fail-429", "fake-slow" }
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
    var answer = Answer(model, prompt);
    var promptTokens = Tokens(AllText(body["messages"]));
    var completionTokens = Tokens(answer);
    var id = "chatcmpl-" + Guid.NewGuid().ToString("N")[..12];
    var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    if (body["stream"]?.GetValue<bool>() == true)
    {
        await StartSse(ctx);
        await Sse(ctx, null, new { id, @object = "chat.completion.chunk", created, model, choices = new[] { new { index = 0, delta = new { role = "assistant", content = "" }, finish_reason = (string?)null } } });
        foreach (var word in Words(answer))
        {
            await Sse(ctx, null, new { id, @object = "chat.completion.chunk", created, model, choices = new[] { new { index = 0, delta = new { content = word }, finish_reason = (string?)null } } });
        }

        await Sse(ctx, null, new { id, @object = "chat.completion.chunk", created, model, choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } } });
        if (body["stream_options"]?["include_usage"]?.GetValue<bool>() == true)
        {
            await Sse(ctx, null, new { id, @object = "chat.completion.chunk", created, model, choices = Array.Empty<object>(), usage = new { prompt_tokens = promptTokens, completion_tokens = completionTokens, total_tokens = promptTokens + completionTokens } });
        }

        await ctx.Response.WriteAsync("data: [DONE]\n\n");
        return Results.Empty;
    }

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
    var answer = Answer(model, input);
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
        foreach (var word in Words(answer))
        {
            await Sse(ctx, "response.output_text.delta", new { type = "response.output_text.delta", delta = word });
        }

        await Sse(ctx, "response.completed", new { type = "response.completed", response });
        return Results.Empty;
    }

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
    var answer = Answer(model, prompt);
    var inputTokens = Tokens(AllText(body["messages"]) + body["system"]);
    var outputTokens = Tokens(answer);
    var id = "msg_" + Guid.NewGuid().ToString("N")[..12];

    if (body["stream"]?.GetValue<bool>() == true)
    {
        await StartSse(ctx);
        await Sse(ctx, "message_start", new { type = "message_start", message = new { id, type = "message", role = "assistant", model, content = Array.Empty<object>(), usage = new { input_tokens = inputTokens, output_tokens = 1, cache_read_input_tokens = 0, cache_creation_input_tokens = 0 } } });
        await Sse(ctx, "content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } });
        foreach (var word in Words(answer))
        {
            await Sse(ctx, "content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = word } });
        }

        await Sse(ctx, "content_block_stop", new { type = "content_block_stop", index = 0 });
        await Sse(ctx, "message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn" }, usage = new { output_tokens = outputTokens } });
        await Sse(ctx, "message_stop", new { type = "message_stop" });
        return Results.Empty;
    }

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

app.Run();

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
