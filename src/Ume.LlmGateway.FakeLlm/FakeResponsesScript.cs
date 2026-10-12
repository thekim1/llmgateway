using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ume.LlmGateway.FakeLlm;

/// <summary>
/// A deterministic Responses API model for tool loops, used by FakeLlm and (as linked source) by the gateway tests. The
/// newest user text steers it:
/// <list type="bullet">
/// <item><c>[[call:NAME {json}]]</c>, once or more: those function calls (parallel) on the first turn, then a message quoting the outputs;</item>
/// <item><c>[[loop:NAME]]</c>: a call to NAME on every turn;</item>
/// <item><c>[[fail-turn:N]]</c>: HTTP 500 on turn N (turns are counted from the user text);</item>
/// <item><c>[[web_search]]</c>: a <c>web_search_call</c> item (a hosted tool the provider runs itself) on the first turn.</item>
/// <item><c>[[break-turn:N]]</c>: when streamed, turn N stops right after its first item is added (the connection is cut).</item>
/// </list>
/// <see cref="Stream"/> answers the same as Responses stream events (<c>response.created</c> ... <c>response.completed</c>).
/// A model name with <c>reasoning</c> adds a reasoning item, with <c>encrypted_content</c> when <c>include</c> asks for it;
/// replaying one without it while <c>store</c> is false is refused. Like OpenAI, responses are kept for
/// <c>previous_response_id</c> unless <c>store</c> is false, and a function call without its output (or an output without
/// its call) is refused. Usage per turn: 100 input tokens (10 cached), 20 output tokens (5 reasoning).
/// </summary>
internal sealed partial class FakeResponsesScript
{
    private readonly ConcurrentDictionary<string, JsonArray> _stored = new(StringComparer.Ordinal);

    public (int Status, JsonObject Body) Respond(JsonObject request)
    {
        var (status, body, _) = Answer(request);
        return (status, body);
    }

    /// <summary>The answer as stream events (null for an HTTP error), and whether the connection is cut after them.</summary>
    public (int Status, JsonObject Body, List<JsonObject>? Events, bool BreaksOff) Stream(JsonObject request)
    {
        var (status, body, breakOff) = Answer(request);
        if (status != 200)
        {
            return (status, body, null, false);
        }

        var events = new List<JsonObject>();
        void Emit(string type, JsonObject data)
        {
            data["type"] = type;
            data["sequence_number"] = events.Count;
            events.Add(data);
        }

        var shell = (JsonObject)body.DeepClone();
        shell["status"] = "in_progress";
        shell["output"] = new JsonArray();
        shell["usage"] = null;
        Emit("response.created", new JsonObject { ["response"] = shell.DeepClone() });
        Emit("response.in_progress", new JsonObject { ["response"] = shell.DeepClone() });
        var output = body["output"]!.AsArray();
        for (var i = 0; i < output.Count; i++)
        {
            var item = output[i]!.AsObject();
            var id = Str(item["id"]);
            var added = (JsonObject)item.DeepClone();
            switch (Str(item["type"]))
            {
                case "function_call":
                    added["arguments"] = string.Empty;
                    added["status"] = "in_progress";
                    break;
                case "message":
                    added["content"] = new JsonArray();
                    added["status"] = "in_progress";
                    break;
            }

            Emit("response.output_item.added", new JsonObject { ["output_index"] = i, ["item"] = added });
            if (breakOff)
            {
                return (status, body, events, true);
            }

            switch (Str(item["type"]))
            {
                case "function_call":
                    var arguments = Str(item["arguments"]) ?? string.Empty;
                    var half = arguments.Length / 2;
                    foreach (var delta in new[] { arguments[..half], arguments[half..] })
                    {
                        Emit("response.function_call_arguments.delta", new JsonObject { ["item_id"] = id, ["output_index"] = i, ["delta"] = delta });
                    }

                    Emit("response.function_call_arguments.done", new JsonObject { ["item_id"] = id, ["output_index"] = i, ["arguments"] = arguments });
                    break;
                case "message":
                    var parts = item["content"]!.AsArray();
                    for (var j = 0; j < parts.Count; j++)
                    {
                        var text = Str(parts[j]!["text"]) ?? string.Empty;
                        var empty = (JsonObject)parts[j]!.DeepClone();
                        empty["text"] = string.Empty;
                        Emit("response.content_part.added", new JsonObject { ["item_id"] = id, ["output_index"] = i, ["content_index"] = j, ["part"] = empty });
                        foreach (var word in text.Split(' '))
                        {
                            Emit("response.output_text.delta", new JsonObject { ["item_id"] = id, ["output_index"] = i, ["content_index"] = j, ["delta"] = word + " " });
                        }

                        Emit("response.output_text.done", new JsonObject { ["item_id"] = id, ["output_index"] = i, ["content_index"] = j, ["text"] = text });
                        Emit("response.content_part.done", new JsonObject { ["item_id"] = id, ["output_index"] = i, ["content_index"] = j, ["part"] = parts[j]!.DeepClone() });
                    }

                    break;
            }

            Emit("response.output_item.done", new JsonObject { ["output_index"] = i, ["item"] = item.DeepClone() });
        }

        Emit("response.completed", new JsonObject { ["response"] = body.DeepClone() });
        return (status, body, events, false);
    }

    private (int Status, JsonObject Body, bool BreakOff) Answer(JsonObject request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = Str(request["model"]) ?? "fake-chat";
        var store = request["store"] is not JsonValue s || !s.TryGetValue<bool>(out var keep) || keep;
        var conversation = new JsonArray();
        if (Str(request["previous_response_id"]) is { } previous)
        {
            if (!_stored.TryGetValue(previous, out var earlier))
            {
                return Error(404, $"Previous response with id '{previous}' not found.");
            }

            foreach (var item in earlier)
            {
                conversation.Add(item!.DeepClone());
            }
        }

        switch (request["input"])
        {
            case JsonValue text:
                conversation.Add(new JsonObject { ["role"] = "user", ["content"] = text.ToString() });
                break;
            case JsonArray items:
                foreach (var item in items)
                {
                    conversation.Add(item!.DeepClone());
                }

                break;
        }

        var all = conversation.OfType<JsonObject>().ToList();
        var callIds = all.Where(i => Str(i["type"]) == "function_call").Select(i => Str(i["call_id"])).ToHashSet();
        var outputIds = all.Where(i => Str(i["type"]) == "function_call_output").Select(i => Str(i["call_id"])).ToHashSet();
        if (callIds.FirstOrDefault(id => !outputIds.Contains(id)) is { } missing)
        {
            return Error(400, $"No tool output found for function call {missing}.");
        }

        if (outputIds.FirstOrDefault(id => !callIds.Contains(id)) is { } orphan)
        {
            return Error(400, $"No tool call found for function call output with call_id {orphan}.");
        }

        if (!store && all.Any(i => Str(i["type"]) == "reasoning" && i["encrypted_content"] is null))
        {
            return Error(400, "Reasoning items need 'encrypted_content' when 'store' is false.");
        }

        var lastUser = all.FindLastIndex(i => Str(i["role"]) == "user");
        var prompt = lastUser < 0 ? string.Empty : TextOf(all[lastUser]["content"]);
        var since = all.Skip(lastUser + 1).ToList();
        var turn = 1 + since.Where(i => Str(i["type"]) == "function_call").Select(i => TurnOf(Str(i["call_id"]))).DefaultIfEmpty(0).Max();
        if (FailRegex().Match(prompt) is { Success: true } fail && int.Parse(fail.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) == turn)
        {
            return Error(500, "Fake failure.");
        }

        var breakOff = BreakRegex().Match(prompt) is { Success: true } cut && int.Parse(cut.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) == turn;

        var output = new JsonArray();
        var reasoning = model.Contains("reasoning", StringComparison.Ordinal);
        if (reasoning)
        {
            var item = new JsonObject { ["type"] = "reasoning", ["id"] = "rs_" + Guid.NewGuid().ToString("N")[..12], ["summary"] = new JsonArray() };
            if (request["include"] is JsonArray include && include.Any(i => Str(i) == "reasoning.encrypted_content"))
            {
                item["encrypted_content"] = "enc_" + turn;
            }

            output.Add(item);
        }

        if (turn == 1 && prompt.Contains("[[web_search]]", StringComparison.Ordinal))
        {
            output.Add(new JsonObject
            {
                ["type"] = "web_search_call", ["id"] = "ws_" + Guid.NewGuid().ToString("N")[..12], ["status"] = "completed",
                ["action"] = new JsonObject { ["type"] = "search", ["query"] = "Umeå" },
            });
        }

        var outputs = since.Where(i => Str(i["type"]) == "function_call_output").Select(i => TextOf(i["output"])).ToList();
        if (LoopRegex().Match(prompt) is { Success: true } loop)
        {
            output.Add(Call(turn, 0, loop.Groups[1].Value, "{}"));
        }
        else if (outputs.Count == 0 && CallRegex().Matches(prompt) is { Count: > 0 } calls)
        {
            for (var i = 0; i < calls.Count; i++)
            {
                output.Add(Call(turn, i, calls[i].Groups[1].Value, calls[i].Groups[2].Success ? calls[i].Groups[2].Value : "{}"));
            }
        }
        else
        {
            var answer = outputs.Count > 0 ? "Tool results: " + string.Join(" | ", outputs) : "Fake answer: " + prompt;
            output.Add(new JsonObject
            {
                ["type"] = "message", ["id"] = "msg_" + Guid.NewGuid().ToString("N")[..12], ["role"] = "assistant", ["status"] = "completed",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = answer, ["annotations"] = new JsonArray() }),
            });
        }

        var id = "resp_" + Guid.NewGuid().ToString("N")[..16];
        if (store)
        {
            foreach (var item in output)
            {
                conversation.Add(item!.DeepClone());
            }

            _stored[id] = conversation;
        }

        return (200, new JsonObject
        {
            ["id"] = id, ["object"] = "response", ["status"] = "completed", ["model"] = model, ["output"] = output,
            ["tools"] = request["tools"]?.DeepClone(), ["tool_choice"] = request["tool_choice"]?.DeepClone() ?? "auto", ["store"] = store,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = 100, ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 10 },
                ["output_tokens"] = 20, ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = reasoning ? 5 : 0 },
                ["total_tokens"] = 120,
            },
        }, breakOff);
    }

    private static JsonObject Call(int turn, int index, string name, string arguments) => new()
    {
        ["type"] = "function_call", ["id"] = $"fc_t{turn}_{index}_{Guid.NewGuid().ToString("N")[..6]}", ["call_id"] = $"call_t{turn}_{index}_{Guid.NewGuid().ToString("N")[..6]}",
        ["name"] = name, ["arguments"] = arguments, ["status"] = "completed",
    };

    private static int TurnOf(string? callId) =>
        callId is not null && callId.StartsWith("call_t", StringComparison.Ordinal) && int.TryParse(callId.AsSpan(6, callId.IndexOf('_', 6) - 6), out var turn) ? turn : 0;

    private static string TextOf(JsonNode? content) => content switch
    {
        JsonValue v when v.TryGetValue<string>(out var text) => text,
        JsonArray parts => string.Join(" ", parts.Select(p => Str(p?["text"]) ?? (Str(p?["type"]) is { } t ? $"[{t}]" : string.Empty))),
        _ => string.Empty,
    };

    private static (int, JsonObject, bool) Error(int status, string message) =>
        (status, new JsonObject { ["error"] = new JsonObject { ["type"] = status >= 500 ? "server_error" : "invalid_request_error", ["message"] = message } }, false);

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    [GeneratedRegex(@"\[\[call:([A-Za-z0-9_.\-]+)\s*(\{.*?\})?\]\]")]
    private static partial Regex CallRegex();

    [GeneratedRegex(@"\[\[loop:([A-Za-z0-9_.\-]+)\]\]")]
    private static partial Regex LoopRegex();

    [GeneratedRegex(@"\[\[fail-turn:(\d+)\]\]")]
    private static partial Regex FailRegex();

    [GeneratedRegex(@"\[\[break-turn:(\d+)\]\]")]
    private static partial Regex BreakRegex();
}
