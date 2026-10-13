using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>
/// MCP resources and prompts. There is no catalogue of them: lists are forwarded to the servers the key may use them on
/// (<see cref="VirtualKeyMcpGrant.AllowResources"/>, <see cref="VirtualKeyMcpGrant.AllowPrompts"/>), page by page, and
/// are not accounted. <c>resources/read</c> and <c>prompts/get</c> are accounted like tool calls. In the aggregated view
/// resource URIs are <c>mcp+ume://{slug}/{uri}</c> and prompts are named like tools; <c>/mcp/{slug}</c> keeps the
/// server's own URIs, names and cursors.
/// </summary>
public sealed partial class GatewayRequestHandler
{
    /// <summary>Exposed prompt names seen in aggregated lists, so <c>prompts/get</c> finds the server's name without listing again.</summary>
    private readonly ConcurrentDictionary<(Guid Server, string Exposed), string> _mcpPromptNames = new();

    private const int McpPromptNameCacheSize = 10_000;

    /// <summary>How many pages of a server's prompt list are searched for a prompt name not seen before.</summary>
    private const int McpPromptLookupPages = 10;

    /// <summary>A forwarded list: its method, the result field that holds the items, and which grant flag it needs.</summary>
    private sealed record McpList(string Method, string Field, bool IsPrompts)
    {
        public static McpList Resources { get; } = new("resources/list", "resources", false);
        public static McpList ResourceTemplates { get; } = new("resources/templates/list", "resourceTemplates", false);
        public static McpList Prompts { get; } = new("prompts/list", "prompts", true);
    }

    /// <summary>
    /// One page of a forwarded list. Aggregated, the servers are visited in slug order and the cursor says which server
    /// and which of its pages comes next; a server that fails is skipped (and counted against its circuit).
    /// </summary>
    private async Task ListForwardedAsync(McpResponse response, CatalogSnapshot snapshot, VirtualKey key, McpServer? only, JsonNode id, JsonObject? parameters,
        McpList list, bool stateless, CancellationToken ct)
    {
        var servers = await FeatureServersAsync(snapshot, key, only, list.IsPrompts, ct);
        var cursor = parameters?["cursor"] is JsonValue c && c.TryGetValue<string>(out var given) ? given : null;
        var start = 0;
        string? upstreamCursor = cursor;
        if (only is null && cursor is not null)
        {
            if (!TryDecodeCursor(cursor, out var slug, out upstreamCursor))
            {
                await response.ErrorAsync(id, (int)ModelContextProtocol.McpErrorCode.InvalidParams, "Invalid cursor");
                return;
            }

            start = servers.FindIndex(s => string.CompareOrdinal(s.Slug, slug) >= 0);
            if (start < 0)
            {
                start = servers.Count;
            }
            else if (servers[start].Slug != slug)
            {
                upstreamCursor = null;
            }
        }

        var items = new JsonArray();
        string? next = null;
        for (var i = start; i < servers.Count; i++)
        {
            var server = servers[i];
            var page = await ListPageAsync(response.Http, snapshot, server, list.Method, i == start ? upstreamCursor : null, ct);
            if (page is null)
            {
                continue;
            }

            var pageNext = page["nextCursor"] is JsonValue n && n.TryGetValue<string>(out var nextCursor) ? nextCursor : null;
            foreach (var item in page[list.Field] as JsonArray ?? [])
            {
                if (item is not JsonObject entry)
                {
                    continue;
                }

                var copy = entry.DeepClone().AsObject();
                // Names and descriptions (often file names) follow the key's PII policy; an item that would be blocked is left out.
                if (key.PiiPolicy is PiiPolicy.Redact or PiiPolicy.Block
                    && McpPii.ScanListItem(copy, list.IsPrompts, redact: key.PiiPolicy == PiiPolicy.Redact).HasPii && key.PiiPolicy == PiiPolicy.Block)
                {
                    continue;
                }

                items.Add(only is null ? Aggregate(copy, server, list) : copy);
            }

            if (only is not null)
            {
                next = pageNext;
                break;
            }

            if (items.Count > 0 || pageNext is not null)
            {
                next = pageNext is not null ? EncodeCursor(server.Slug, pageNext) : i + 1 < servers.Count ? EncodeCursor(servers[i + 1].Slug, null) : null;
                break;
            }
        }

        await response.ResultAsync(id, w =>
        {
            w.WriteStartObject();
            w.WritePropertyName(list.Field);
            items.WriteTo(w);
            if (next is not null)
            {
                w.WriteString("nextCursor", next);
            }

            if (stateless)
            {
                WriteCacheHints(w, 0);
            }

            w.WriteEndObject();
        }, ct);
    }

    /// <summary>A list item in the aggregated view: URIs namespaced, prompt names exposed (and remembered for <c>prompts/get</c>).</summary>
    private JsonObject Aggregate(JsonObject item, McpServer server, McpList list)
    {
        if (list.IsPrompts && item["name"] is JsonValue name && name.TryGetValue<string>(out var original))
        {
            var exposed = McpToolNames.Exposed(server.Slug, original);
            if (_mcpPromptNames.Count >= McpPromptNameCacheSize)
            {
                _mcpPromptNames.Clear();
            }

            _mcpPromptNames[(server.Id, exposed)] = original;
            item["name"] = exposed;
        }

        foreach (var field in (string[])["uri", "uriTemplate"])
        {
            if (item[field] is JsonValue uri && uri.TryGetValue<string>(out var value))
            {
                item[field] = McpResourceUris.Namespace(server.Slug, value);
            }
        }

        return item;
    }

    /// <summary>One page of a server's list; null when the server failed (logged, counted against its circuit).</summary>
    private async Task<JsonObject?> ListPageAsync(HttpContext http, CatalogSnapshot snapshot, McpServer server, string method, string? cursor, CancellationToken ct)
    {
        var parameters = new JsonObject();
        if (cursor is not null)
        {
            parameters["cursor"] = cursor;
        }

        if (UpstreamCall(http, snapshot, server, method, parameters, mayRetry: true) is not { } call)
        {
            metrics.McpCall(server.Slug, method, "credential_unavailable", 0);
            return null;
        }

        var started = time.GetTimestamp();
        using var activity = McpActivities.StartActivity(method, System.Diagnostics.ActivityKind.Client);
        activity?.SetTag("mcp.method.name", method);
        activity?.SetTag("ume.mcp.server", server.Slug);
        var result = await mcp.SendAsync(call, ct);
        metrics.McpCall(server.Slug, method, OutcomeName(result), time.GetElapsedTime(started).TotalMilliseconds);
        await RecordMcpCircuitAsync(RequestIdMiddleware.Get(http), server, result);
        if (!result.Succeeded)
        {
            LogMcpListServerSkipped(logger, RequestIdMiddleware.Get(http), server.Slug, method, result.Failure.ToString(), result.HttpStatus ?? 0);
            return null;
        }

        return result.Result as JsonObject;
    }

    private async Task ReadResourceAsync(McpResponse response, CatalogSnapshot snapshot, VirtualKey key, McpServer? only, JsonNode id, JsonObject? parameters, CancellationToken ct)
    {
        var state = new RequestState(RequestIdMiddleware.Get(response.Http), GatewayEndpoint.Mcp, time.GetTimestamp()) { Key = key, McpMethod = "resources/read" };
        var answer = new McpRpcAnswer(response, id);
        var uri = parameters?["uri"] is JsonValue u && u.TryGetValue<string>(out var given) ? given : null;
        McpServer? server = only;
        var original = uri;
        if (only is null && uri is not null)
        {
            server = McpResourceUris.TryParse(uri, out var slug, out var inner) ? snapshot.McpServers.GetValueOrDefault(slug) : null;
            original = inner;
        }

        state.RequestedModel = McpUsageName(server?.Slug ?? "?", string.Empty);
        state.McpServerId = server?.Id;
        var denial = server is null || original is null ? (McpDenial?)null
            : McpAccess.CheckResources(key, server, snapshot.McpGrants(key.Id).GetValueOrDefault(server.Id), await IsMcpCircuitOpenAsync(server.Id, ct));
        if (server is null || original is null || denial != McpDenial.None)
        {
            LogMcpDenied(state, "resources/read", denial, server);
            await RefuseAsync(answer, state, (int)ModelContextProtocol.McpErrorCode.InvalidParams, "Resource not found",
                404, RequestOutcome.Rejected, GatewayErrorCodes.McpResourceNotFound);
            return;
        }

        Action<JsonNode>? rewrite = only is null ? result => NamespaceContents(result, server.Slug) : null;
        var forward = new McpForward(server, "resources/read", new JsonObject { ["uri"] = original }, null, MayRetry: true, Price: 0m,
            McpResultKind.Resource, "resources/read", null, rewrite);
        await RunAccountedAsync(answer, state, snapshot, forward, ProgressRelay(response, parameters, state.Key!.PiiPolicy, ct), ct);
    }

    private async Task GetPromptAsync(McpResponse response, CatalogSnapshot snapshot, VirtualKey key, McpServer? only, JsonNode id, JsonObject? parameters, CancellationToken ct)
    {
        var state = new RequestState(RequestIdMiddleware.Get(response.Http), GatewayEndpoint.Mcp, time.GetTimestamp()) { Key = key, McpMethod = "prompts/get" };
        var answer = new McpRpcAnswer(response, id);
        var name = parameters?["name"] is JsonValue n && n.TryGetValue<string>(out var given) ? given : null;
        state.RequestedModel = McpUsageName(only?.Slug, name ?? string.Empty);
        state.McpTool = name is null ? null : Truncate(name, 128);
        var server = only ?? (name is not null && McpResourceUris.SlugOfExposedName(name) is { } slug ? snapshot.McpServers.GetValueOrDefault(slug) : null);
        state.McpServerId = server?.Id;
        var denial = server is null || name is null ? (McpDenial?)null
            : McpAccess.CheckPrompts(key, server, snapshot.McpGrants(key.Id).GetValueOrDefault(server.Id), await IsMcpCircuitOpenAsync(server.Id, ct));
        var original = denial == McpDenial.None ? only is not null ? name : await PromptNameAsync(response.Http, snapshot, server!, name!, ct) : null;
        if (server is null || original is null || denial != McpDenial.None)
        {
            LogMcpDenied(state, state.McpTool ?? "-", denial, server);
            await RefuseAsync(answer, state, (int)ModelContextProtocol.McpErrorCode.InvalidParams, $"Unknown prompt: '{Truncate(name ?? string.Empty, 128)}'",
                404, RequestOutcome.Rejected, GatewayErrorCodes.McpPromptNotFound);
            return;
        }

        state.McpTool = Truncate(original, 128);
        state.RequestedModel = McpUsageName(server.Slug, original);
        var arguments = parameters!["arguments"];
        if (arguments is not null and not JsonObject)
        {
            await RefuseAsync(answer, state, (int)ModelContextProtocol.McpErrorCode.InvalidParams, "Invalid params: 'arguments' must be an object",
                400, RequestOutcome.Rejected, GatewayErrorCodes.McpInvalidArguments);
            return;
        }

        var args = (JsonObject?)arguments ?? [];
        parameters.Remove("arguments");
        var forward = new McpForward(server, "prompts/get", new JsonObject { ["name"] = original, ["arguments"] = args }, args, MayRetry: true, Price: 0m,
            McpResultKind.Prompt, "prompts/get " + original, original,
            only is null ? result => NamespaceBlockUris((result["messages"] as JsonArray ?? []).Select(m => m?["content"]), server.Slug) : null);
        await RunAccountedAsync(answer, state, snapshot, forward, ProgressRelay(response, parameters, state.Key!.PiiPolicy, ct), ct);
    }

    /// <summary>The server's name of an exposed prompt name: from aggregated lists seen before, else by searching its list.</summary>
    private async Task<string?> PromptNameAsync(HttpContext http, CatalogSnapshot snapshot, McpServer server, string exposed, CancellationToken ct)
    {
        if (_mcpPromptNames.TryGetValue((server.Id, exposed), out var known))
        {
            return known;
        }

        string? cursor = null;
        for (var page = 0; page < McpPromptLookupPages; page++)
        {
            if (await ListPageAsync(http, snapshot, server, McpList.Prompts.Method, cursor, ct) is not { } result)
            {
                return null;
            }

            foreach (var prompt in result["prompts"] as JsonArray ?? [])
            {
                if (prompt?["name"] is JsonValue name && name.TryGetValue<string>(out var original) && McpToolNames.Exposed(server.Slug, original) == exposed)
                {
                    _mcpPromptNames[(server.Id, exposed)] = original;
                    return original;
                }
            }

            cursor = result["nextCursor"] is JsonValue n && n.TryGetValue<string>(out var next) ? next : null;
            if (cursor is null)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Servers on which the key may use resources (or prompts), in slug order, without those whose circuit is open.</summary>
    private async Task<List<McpServer>> FeatureServersAsync(CatalogSnapshot snapshot, VirtualKey key, McpServer? only, bool prompts, CancellationToken ct)
    {
        var candidates = VisibleServers(snapshot, key, only);
        var open = await OpenMcpCircuitsAsync([.. candidates.Select(s => s.Server.Id)], ct);
        return [.. candidates.Where(s => (prompts
                ? McpAccess.CheckPrompts(key, s.Server, s.Grant, open.Contains(s.Server.Id))
                : McpAccess.CheckResources(key, s.Server, s.Grant, open.Contains(s.Server.Id))) == McpDenial.None)
            .Select(s => s.Server)];
    }

    /// <summary>
    /// Aggregated view: the URIs of <c>resource_link</c> and embedded <c>resource</c> content blocks, so they can be read
    /// through <c>/mcp</c>. Only these fields change; <c>structuredContent</c> is the tool's own data and is left as it is.
    /// </summary>
    private static void NamespaceBlockUris(IEnumerable<JsonNode?> blocks, string slug)
    {
        foreach (var block in blocks)
        {
            var type = block?["type"] is JsonValue t && t.TryGetValue<string>(out var name) ? name : null;
            var target = type switch
            {
                "resource_link" => block as JsonObject,
                "resource" => block!["resource"] as JsonObject,
                _ => null,
            };
            if (target?["uri"] is JsonValue uri && uri.TryGetValue<string>(out var value))
            {
                target["uri"] = McpResourceUris.Namespace(slug, value);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Request {RequestId}: MCP server {Server} left out of {Method} ({Failure}, HTTP {Status})")]
    private static partial void LogMcpListServerSkipped(ILogger logger, string requestId, string server, string method, string failure, int status);

    private static void NamespaceContents(JsonNode result, string slug)
    {
        foreach (var content in result["contents"] as JsonArray ?? [])
        {
            if (content is JsonObject obj && obj["uri"] is JsonValue uri && uri.TryGetValue<string>(out var value))
            {
                obj["uri"] = McpResourceUris.Namespace(slug, value);
            }
        }
    }

    /// <summary>The aggregated cursor: the server to continue with and that server's own cursor, opaque to the client.</summary>
    private static string EncodeCursor(string slug, string? cursor)
    {
        var json = new JsonObject { ["s"] = slug };
        if (cursor is not null)
        {
            json["c"] = cursor;
        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryDecodeCursor(string value, out string slug, out string? cursor)
    {
        slug = string.Empty;
        cursor = null;
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
            if (JsonNode.Parse(Convert.FromBase64String(base64)) is not JsonObject json || json["s"] is not JsonValue s || !s.TryGetValue<string>(out var parsed)
                || !McpToolNames.IsValidSlug(parsed))
            {
                return false;
            }

            slug = parsed;
            cursor = json["c"] is JsonValue c && c.TryGetValue<string>(out var inner) ? inner : null;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }
}
