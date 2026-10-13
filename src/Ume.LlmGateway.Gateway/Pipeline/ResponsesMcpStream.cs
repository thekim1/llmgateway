using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using Ume.LlmGateway.Infrastructure.Providers;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>How a provider output item is shown in the combined stream.</summary>
internal enum StreamItemKind
{
    /// <summary>Relayed unchanged (re-indexed): messages, reasoning, the caller's function calls, hosted tools.</summary>
    Plain = 0,

    /// <summary>A call to an MCP tool, shown as <c>mcp_call</c>; its <c>output_item.done</c> waits for the gateway.</summary>
    McpCall = 1,

    /// <summary>
    /// A call that may need approval: nothing is relayed and its index is kept; once the turn is complete the loop emits
    /// the whole item (an <c>mcp_approval_request</c>, or an <c>mcp_call</c> that was not run).
    /// </summary>
    Deferred = 2,

    /// <summary>A call the gateway answers itself and never shows (a function nobody offered).</summary>
    Hidden = 3,
}

/// <summary>
/// The one Responses event stream a client of a hosted <c>mcp</c> tool sees: the gateway's own <c>response.created</c> and
/// <c>response.in_progress</c>, every provider turn relayed with <c>output_index</c> re-based to the combined output and
/// <c>sequence_number</c> renumbered, MCP function calls shown as <c>mcp_call</c> (their argument events as
/// <c>response.mcp_call_arguments.*</c>, with the call id as <c>item_id</c>), the gateway's <c>response.mcp_call.*</c>
/// events around each tool call, and one final <c>response.completed</c>/<c>response.incomplete</c> with the combined
/// response. Event shapes follow the openai-python types (<c>openai/types/responses</c>). While the gateway works between
/// events, SSE comments keep proxies from timing the stream out.
/// </summary>
internal sealed class ResponsesMcpStream(HttpContext http, TimeSpan keepAliveInterval, bool keepEncryptedReasoning)
{
    private static readonly byte[] KeepAlive = ": keep-alive\n\n"u8.ToArray();

    private long _sequence;

    public bool Started { get; private set; }

    /// <summary>Starts the stream with the gateway's <c>response.created</c> and <c>response.in_progress</c>.</summary>
    public async Task StartAsync(JsonObject response, CancellationToken ct)
    {
        Started = true;
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        http.Response.StatusCode = 200;
        http.Response.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-cache, no-store";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        await http.Response.StartAsync(ct);
        await EmitAsync("response.created", new JsonObject { ["response"] = response.DeepClone() }, ct);
        await EmitAsync("response.in_progress", new JsonObject { ["response"] = response.DeepClone() }, ct);
    }

    /// <summary>The final event: <c>response.completed</c>, or <c>response.incomplete</c>/<c>response.failed</c> after the status.</summary>
    public Task CompleteAsync(JsonObject response) =>
        EmitAsync(Str(response["status"]) is "incomplete" or "failed" ? "response." + Str(response["status"]) : "response.completed",
            new JsonObject { ["response"] = response.DeepClone() }, CancellationToken.None);

    public Task ItemAddedAsync(int index, JsonObject item, CancellationToken ct) =>
        EmitAsync("response.output_item.added", new JsonObject { ["output_index"] = index, ["item"] = item.DeepClone() }, ct);

    public Task ItemDoneAsync(int index, JsonObject item, CancellationToken ct) =>
        EmitAsync("response.output_item.done", new JsonObject { ["output_index"] = index, ["item"] = item.DeepClone() }, ct);

    /// <summary><c>response.mcp_call.{in_progress|completed|failed}</c> or <c>response.mcp_list_tools.*</c>.</summary>
    public Task ItemEventAsync(string type, int index, string itemId, CancellationToken ct) =>
        EmitAsync(type, new JsonObject { ["item_id"] = itemId, ["output_index"] = index }, ct);

    /// <summary>
    /// The full event sequence of an item that is complete already: added, its progress events (an <c>mcp_call</c> that
    /// was not run has none), done.
    /// </summary>
    public async Task WholeItemAsync(int index, JsonObject item, CancellationToken ct)
    {
        var id = Str(item["id"]) ?? string.Empty;
        switch (Str(item["type"]))
        {
            case "mcp_list_tools":
                var shell = (JsonObject)item.DeepClone();
                shell["tools"] = new JsonArray();
                await ItemAddedAsync(index, shell, ct);
                await ItemEventAsync("response.mcp_list_tools.in_progress", index, id, ct);
                await ItemEventAsync("response.mcp_list_tools.completed", index, id, ct);
                break;
            case "mcp_call":
                await ItemAddedAsync(index, McpCallShell(id, Str(item["server_label"]), Str(item["name"]), Str(item["approval_request_id"])), ct);
                await EmitAsync("response.mcp_call_arguments.done", new JsonObject { ["item_id"] = id, ["output_index"] = index, ["arguments"] = item["arguments"]?.DeepClone() }, ct);
                if (Str(item["status"]) is "completed" or "failed")
                {
                    await ItemEventAsync("response.mcp_call.in_progress", index, id, ct);
                    await ItemEventAsync(Str(item["status"]) == "completed" ? "response.mcp_call.completed" : "response.mcp_call.failed", index, id, ct);
                }

                break;
            default:
                await ItemAddedAsync(index, item, ct);
                break;
        }

        await ItemDoneAsync(index, item, ct);
    }

    /// <summary>
    /// Relays one provider turn, its items numbered from <paramref name="baseIndex"/>. <paramref name="classify"/> decides
    /// from each added item how it is shown (and gives the shell to show for an MCP call). Returns the provider's final
    /// response object, or null when the provider reported a failure or the stream ended without one.
    /// <paramref name="onFirstEvent"/> runs once, before anything is relayed, with the provider's <c>response.created</c>
    /// response when that is the first event.
    /// </summary>
    public async Task<JsonObject?> RelayTurnAsync(ProviderStreamResult result, int baseIndex, Func<JsonObject, (StreamItemKind Kind, JsonObject? Shown)> classify,
        Func<JsonObject?, Task> onFirstEvent, CancellationToken ct)
    {
        var items = new Dictionary<int, (int Index, StreamItemKind Kind, string? Id)>();
        var next = baseIndex;
        JsonObject? final = null;
        Func<JsonObject?, Task>? first = onFirstEvent;
        await foreach (var evt in result.Events.WithCancellation(ct))
        {
            if (evt.Data == SseEvent.Done || ParseEvent(evt.Data) is not { } data || Str(data["type"]) is not { } type)
            {
                continue;
            }

            if (first is not null)
            {
                await first(type == "response.created" ? data["response"] as JsonObject : null);
                first = null;
            }

            switch (type)
            {
                case "response.created" or "response.in_progress" or "response.queued":
                    continue;
                case "response.completed" or "response.incomplete":
                    final = data["response"] as JsonObject;
                    continue;
                case "response.failed" or "error":
                    return null;
            }

            if (data["output_index"] is not JsonValue o || !o.TryGetValue<int>(out var providerIndex))
            {
                await EmitAsync(type, data, ct);
                continue;
            }

            if (type == "response.output_item.added" && data["item"] is JsonObject added)
            {
                var (kind, shown) = classify(added);
                if (kind == StreamItemKind.Hidden)
                {
                    items[providerIndex] = (-1, kind, null);
                    continue;
                }

                items[providerIndex] = (next, kind, Str(added["call_id"]));
                data["output_index"] = next++;
                if (kind == StreamItemKind.Deferred)
                {
                    continue;
                }

                data["item"] = shown ?? Shown(added);
                await EmitAsync(type, data, ct);
                continue;
            }

            if (!items.TryGetValue(providerIndex, out var known) || known.Kind is StreamItemKind.Hidden or StreamItemKind.Deferred)
            {
                continue;
            }

            data["output_index"] = known.Index;
            if (known.Kind == StreamItemKind.McpCall)
            {
                if (type is not ("response.function_call_arguments.delta" or "response.function_call_arguments.done"))
                {
                    continue;
                }

                type = type == "response.function_call_arguments.delta" ? "response.mcp_call_arguments.delta" : "response.mcp_call_arguments.done";
                data["item_id"] = known.Id;
            }
            else if (type == "response.output_item.done" && data["item"] is JsonObject done)
            {
                data["item"] = Shown(done);
            }

            await EmitAsync(type, data, ct);
        }

        return final;
    }

    /// <summary>Waits for <paramref name="work"/>, writing an SSE comment whenever it takes longer than the keep-alive interval.</summary>
    public async Task<T> KeepAliveAsync<T>(Task<T> work, CancellationToken ct)
    {
        await KeepAliveAsync((Task)work, ct);
        return await work;
    }

    public async Task KeepAliveAsync(Task work, CancellationToken ct)
    {
        while (Started && !work.IsCompleted)
        {
            using var interval = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (await Task.WhenAny(work, Task.Delay(keepAliveInterval, interval.Token)) == work)
            {
                await interval.CancelAsync();
                break;
            }

            ct.ThrowIfCancellationRequested();
            await http.Response.BodyWriter.WriteAsync(KeepAlive, ct);
        }

        await work;
    }

    /// <summary>An <c>mcp_call</c> as it is shown when added: no output yet, arguments still to come.</summary>
    public static JsonObject McpCallShell(string id, string? serverLabel, string? name, string? approvalRequestId)
    {
        var shell = new JsonObject
        {
            ["type"] = "mcp_call", ["id"] = id, ["server_label"] = serverLabel, ["name"] = name, ["arguments"] = string.Empty, ["status"] = "in_progress",
        };
        if (approvalRequestId is not null)
        {
            shell["approval_request_id"] = approvalRequestId;
        }

        return shell;
    }

    /// <summary>A relayed item as the combined response shows it: reasoning without the <c>encrypted_content</c> the gateway asked for itself.</summary>
    private JsonObject Shown(JsonObject item)
    {
        if (!keepEncryptedReasoning && Str(item["type"]) == "reasoning")
        {
            item.Remove("encrypted_content");
        }

        return item;
    }

    private async Task EmitAsync(string type, JsonObject data, CancellationToken ct)
    {
        data["type"] = type;
        data["sequence_number"] = _sequence++;
        var writer = http.Response.BodyWriter;
        writer.Write("event: "u8);
        Encoding.UTF8.GetBytes(type, writer);
        writer.Write("\ndata: "u8);
        using (var json = new Utf8JsonWriter(writer))
        {
            data.WriteTo(json);
        }

        writer.Write("\n\n"u8);
        await writer.FlushAsync(ct);
    }

    private static JsonObject? ParseEvent(string data)
    {
        try
        {
            return JsonNode.Parse(data) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
