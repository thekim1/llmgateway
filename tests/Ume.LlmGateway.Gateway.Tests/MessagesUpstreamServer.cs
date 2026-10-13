using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>
/// An Anthropic Messages provider on a loopback port at <c>{Url}/{name}/v1/messages</c>, steered by the newest user text:
/// <c>[[call:NAME {json}]]</c> (once or more) makes those tool calls on the first turn and then answers
/// <c>Tool results: …</c> quoting the <c>tool_result</c> texts; <c>[[loop:NAME]]</c> calls NAME on every turn;
/// <c>[[fail-turn:N]]</c> answers 500 on turn N. Like Anthropic it refuses a <c>tool_use</c> without its
/// <c>tool_result</c> in the next message, and connector fields it does not know (<c>mcp_servers</c>, <c>mcp_toolset</c>,
/// <c>mcp_*</c> blocks). Usage per turn: 100 input tokens, 20 output tokens, 10 cache-read tokens.
/// </summary>
public sealed partial class MessagesUpstreamServer : IAsyncDisposable
{
    private WebApplication? _app;

    public string Url { get; private set; } = string.Empty;

    /// <summary>The bodies and <c>anthropic-beta</c> headers the gateway sent, per provider name, in order.</summary>
    public ConcurrentDictionary<string, ConcurrentQueue<(JsonObject Body, string? Beta)>> Requests { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<JsonObject> For(string name) => Requests.TryGetValue(name, out var requests) ? [.. requests.Select(r => r.Body)] : [];

    public IReadOnlyList<string?> BetaHeaders(string name) => Requests.TryGetValue(name, out var requests) ? [.. requests.Select(r => r.Beta)] : [];

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        _app = builder.Build();
        _app.MapPost("/{name}/v1/messages", async (HttpContext http, string name) =>
        {
            var body = (await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted))!.AsObject();
            var beta = http.Request.Headers.TryGetValue("anthropic-beta", out var b) ? b.ToString() : null;
            Requests.GetOrAdd(name, _ => new()).Enqueue((body.DeepClone().AsObject(), beta));
            var (status, answer) = Respond(body);
            return Results.Json(answer, statusCode: status);
        });
        await _app.StartAsync();
        Url = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    private static (int Status, JsonObject Body) Respond(JsonObject request)
    {
        if (request.ContainsKey("mcp_servers") || (request["tools"] as JsonArray ?? []).Any(t => Str(t?["type"]) == "mcp_toolset"))
        {
            return Error(400, "invalid_request_error", "mcp_servers: Extra inputs are not permitted (beta header missing).");
        }

        var messages = request["messages"]!.AsArray();
        var promptIndex = -1;
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i]!;
            if (Content(message).Any(c => Str(c?["type"])?.StartsWith("mcp_", StringComparison.Ordinal) == true))
            {
                return Error(400, "invalid_request_error", $"messages.{i}: unknown content block type.");
            }

            if (Str(message["role"]) == "assistant")
            {
                var uses = Content(message).Where(c => Str(c?["type"]) == "tool_use").Select(c => Str(c!["id"])).ToHashSet();
                var answered = i + 1 < messages.Count
                    ? Content(messages[i + 1]!).Where(c => Str(c?["type"]) == "tool_result").Select(c => Str(c!["tool_use_id"])).ToHashSet()
                    : [];
                if (uses.Count > 0 && i + 1 < messages.Count && !uses.SetEquals(answered))
                {
                    return Error(400, "invalid_request_error", $"messages.{i + 1}: tool_use ids without tool_result blocks.");
                }
            }
            else if (Content(message).Any(c => Str(c?["type"]) == "text"))
            {
                promptIndex = i;
            }
        }

        var prompt = promptIndex < 0 ? string.Empty : string.Join(" ", Content(messages[promptIndex]!).Where(c => Str(c?["type"]) == "text").Select(c => Str(c!["text"])));
        var turn = 1 + messages.Skip(promptIndex + 1).Count(m => Str(m?["role"]) == "assistant");
        if (FailTurn().Match(prompt) is { Success: true } fail && int.Parse(fail.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) == turn)
        {
            return Error(500, "api_error", "Internal server error.");
        }

        var content = new JsonArray();
        var stop = "end_turn";
        var loop = Loop().Match(prompt);
        var calls = Call().Matches(prompt);
        if (loop.Success || (calls.Count > 0 && turn == 1))
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = "Calling tools." });
            foreach (var (tool, input) in loop.Success ? [(loop.Groups[1].Value, "{}")] : calls.Select(c => (c.Groups[1].Value, c.Groups[2].Value)))
            {
                content.Add(new JsonObject
                {
                    ["type"] = "tool_use", ["id"] = "toolu_" + Guid.NewGuid().ToString("N")[..20], ["name"] = tool, ["input"] = JsonNode.Parse(input.Length == 0 ? "{}" : input),
                });
            }

            stop = "tool_use";
        }
        else if (calls.Count > 0 || Str(messages[^1]?["role"]) == "user" && Content(messages[^1]!).Any(c => Str(c?["type"]) == "tool_result"))
        {
            var results = Content(messages[^1]!).Where(c => Str(c?["type"]) == "tool_result").Select(c =>
                (c!["is_error"]?.GetValue<bool>() == true ? "(error) " : string.Empty)
                + (Str(c["content"]) ?? string.Join(" ", (c["content"] as JsonArray ?? []).Where(p => Str(p?["type"]) == "text").Select(p => Str(p!["text"])))));
            content.Add(new JsonObject { ["type"] = "text", ["text"] = "Tool results: " + string.Join(" | ", results) });
        }
        else
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = "Hej!" });
        }

        return (200, new JsonObject
        {
            ["id"] = "msg_" + Guid.NewGuid().ToString("N")[..20], ["type"] = "message", ["role"] = "assistant", ["model"] = Str(request["model"]),
            ["content"] = content, ["stop_reason"] = stop, ["stop_sequence"] = null,
            ["usage"] = new JsonObject { ["input_tokens"] = 100, ["cache_creation_input_tokens"] = 0, ["cache_read_input_tokens"] = 10, ["output_tokens"] = 20 },
        });
    }

    private static JsonArray Content(JsonNode message) => message["content"] switch
    {
        JsonArray blocks => blocks,
        JsonValue text => new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text.GetValue<string>() }),
        _ => [],
    };

    private static (int, JsonObject) Error(int status, string type, string message) =>
        (status, new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["type"] = type, ["message"] = message } });

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    [GeneratedRegex(@"\[\[call:([^\s\]]+)\s*(\{[^\]]*\})?\]\]")]
    private static partial Regex Call();

    [GeneratedRegex(@"\[\[loop:([^\s\]]+)\]\]")]
    private static partial Regex Loop();

    [GeneratedRegex(@"\[\[fail-turn:(\d+)\]\]")]
    private static partial Regex FailTurn();

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }
}
