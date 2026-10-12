using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Providers;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>
/// Anthropic's MCP connector on <c>/v1/messages</c> (<c>mcp_servers</c> plus <c>mcp_toolset</c> tools): the gateway offers
/// the servers' tools as ordinary tools, runs the model's calls to them through the shared MCP core and sends the
/// conversation again until the model is done, calls one of the caller's own tools or a limit is reached. The connector is
/// never sent upstream. The answer shows each call as <c>mcp_tool_use</c> followed by its <c>mcp_tool_result</c>. The
/// connector has no approval protocol, so tools that need approval are not offered; turns, fallback, budget, PII and
/// accounting are those of the <c>/v1/responses</c> loop. Streaming is not supported yet.
/// </summary>
public sealed partial class GatewayRequestHandler
{
    /// <summary>One offered tool: the connector server it came from and the catalogue entry.</summary>
    private sealed record MessagesMcpOffer(string ServerName, McpToolEntry Entry);

    /// <summary>What <see cref="PrepareMessagesMcpAsync"/> found out about the request.</summary>
    private sealed class MessagesMcp
    {
        public required Dictionary<string, MessagesMcpOffer> Offered { get; init; }
        public required List<(AnthropicMcpServer Spec, List<McpToolEntry> Tools)> Servers { get; init; }

        /// <summary>The names of the caller's own tools: calls to them end the loop and go to the caller.</summary>
        public required HashSet<string> CallerTools { get; init; }
    }

    /// <summary>The running loop of one <c>/v1/messages</c> request: the client's content blocks of every turn and the summed usage.</summary>
    private sealed class MessagesMcpLoop(RequestState first, int maxToolCalls) : McpLoopCore(first, maxToolCalls)
    {
        public JsonArray Content { get; } = [];
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long CacheCreationTokens { get; set; }
        public long CacheReadTokens { get; set; }
    }

    /// <summary>
    /// Validates the connector, takes it out of the body and turns earlier <c>mcp_tool_use</c>/<c>mcp_tool_result</c> blocks
    /// into <c>tool_use</c>/<c>tool_result</c>. The tools are added only when turn 1 is sent, so the PII guard and the token
    /// estimate see the caller's body. Null when the request was refused (written and accounted).
    /// </summary>
    private async Task<MessagesMcp?> PrepareMessagesMcpAsync(HttpContext http, RequestState state, CatalogSnapshot snapshot, VirtualKey key, JsonObject body,
        CancellationToken ct)
    {
        Task Refuse(int status, string code, string message) => RejectAsync(http, state, status, code, message, RequestOutcome.Rejected);

        if (RequestRewriter.IsStreaming(body))
        {
            await Refuse(400, GatewayErrorCodes.InvalidRequest, "'stream: true' stöds inte ännu tillsammans med MCP-servrar ('mcp_servers'); skicka förfrågan utan streaming.");
            return null;
        }

        if (AnthropicMcpServer.Parse(body, out var error) is not { } specs)
        {
            await Refuse(400, GatewayErrorCodes.InvalidRequest, error!);
            return null;
        }

        var grants = snapshot.McpGrants(key.Id);
        var offered = new Dictionary<string, MessagesMcpOffer>(StringComparer.Ordinal);
        var servers = new List<(AnthropicMcpServer, List<McpToolEntry>)>();
        foreach (var spec in specs)
        {
            if (!snapshot.McpServers.TryGetValue(spec.Name, out var server)
                || McpAccess.CheckServer(key, server, grants.GetValueOrDefault(server.Id), circuitOpen: false) != McpDenial.None)
            {
                await Refuse(404, GatewayErrorCodes.McpServerNotFound, $"MCP-servern '{Truncate(spec.Name, 64)}' finns inte eller får inte användas med nyckeln.");
                return null;
            }

            if (spec.Url is not null && spec.Url != server.Url)
            {
                await Refuse(400, GatewayErrorCodes.InvalidRequest,
                    $"MCP-servern '{spec.Name}': 'url' får bara vara MCP-serverns registrerade adress; gatewayen ansluter aldrig till andra adresser.");
                return null;
            }

            var grant = grants[server.Id];
            var tools = (await VisibleToolsAsync(snapshot, key, server, ct))
                .Where(e => spec.Enables(e.Tool.Name)
                    && grant.ApprovalPolicy != McpApprovalPolicy.Always
                    && (grant.ApprovalPolicy != McpApprovalPolicy.NeverForReadOnly || e.Definition.ReadOnly))
                .OrderBy(e => e.Tool.Name, StringComparer.Ordinal)
                .ToList();
            foreach (var entry in tools)
            {
                offered[entry.Tool.ExposedName] = new MessagesMcpOffer(spec.Name, entry);
            }

            servers.Add((spec, tools));
        }

        var callerTools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in body["tools"] as JsonArray ?? [])
        {
            if (Str(tool?["name"]) is { } name)
            {
                callerTools.Add(name);
            }
        }

        if (callerTools.FirstOrDefault(offered.ContainsKey) is { } clash)
        {
            await Refuse(400, GatewayErrorCodes.InvalidRequest, $"Verktyget '{clash}' har samma namn som ett MCP-verktyg.");
            return null;
        }

        if (body["messages"] is JsonArray messages && TranslateMessagesHistory(messages, specs, out error) is false)
        {
            await Refuse(400, GatewayErrorCodes.InvalidRequest, error!);
            return null;
        }

        return new MessagesMcp { Offered = offered, Servers = servers, CallerTools = callerTools };
    }

    /// <summary>
    /// Earlier connector blocks in assistant messages, as the provider understands them: <c>mcp_tool_use</c> becomes
    /// <c>tool_use</c> (with the exposed name), each run of <c>mcp_tool_result</c> blocks becomes <c>tool_result</c> blocks in
    /// a user message after the assistant blocks so far (merged into the next user message when that follows), and
    /// <c>mcp_tool_listing</c> is left out. False with <paramref name="error"/> when a block cannot be translated.
    /// </summary>
    private static bool TranslateMessagesHistory(JsonArray messages, IReadOnlyList<AnthropicMcpServer> servers, out string? error)
    {
        error = null;
        static bool IsConnectorBlock(JsonNode? block) => Str(block?["type"]) is "mcp_tool_use" or "mcp_tool_result" or "mcp_tool_listing";
        if (!messages.Any(m => Str(m?["role"]) == "assistant" && m!["content"] is JsonArray content && content.Any(IsConnectorBlock)))
        {
            return true;
        }

        var items = messages.ToList();
        messages.Clear();
        JsonArray? carried = null;
        foreach (var node in items)
        {
            var role = Str(node?["role"]);
            if (carried is not null)
            {
                if (role == "user")
                {
                    var content = node!["content"] is JsonValue text ? new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text.DeepClone() }) : node["content"] as JsonArray ?? [];
                    for (var i = carried.Count - 1; i >= 0; i--)
                    {
                        var result = carried[i]!;
                        carried.RemoveAt(i);
                        content.Insert(0, result);
                    }

                    node["content"] = content;
                    messages.Add(node);
                    carried = null;
                    continue;
                }

                messages.Add(new JsonObject { ["role"] = "user", ["content"] = carried });
                carried = null;
            }

            if (role != "assistant" || node!["content"] is not JsonArray blocks || !blocks.Any(IsConnectorBlock))
            {
                messages.Add(node);
                continue;
            }

            var message = (JsonObject)node;
            var original = blocks.ToList();
            blocks.Clear();
            var segment = new JsonArray();
            var pending = new JsonArray();
            var uses = new HashSet<string>(StringComparer.Ordinal);
            var answered = new HashSet<string>(StringComparer.Ordinal);
            var first = true;
            void Flush()
            {
                var assistant = first ? message : new JsonObject { ["role"] = "assistant" };
                assistant["content"] = segment;
                messages.Add(assistant);
                first = false;
                segment = [];
            }

            foreach (var block in original)
            {
                switch (Str(block?["type"]))
                {
                    case "mcp_tool_listing":
                        break;
                    case "mcp_tool_use":
                        var server = Str(block!["server_name"]);
                        if (Str(block["id"]) is not { } id || Str(block["name"]) is not { } name || server is null || !servers.Any(s => s.Name == server))
                        {
                            error = "Ett 'mcp_tool_use'-block saknar id eller namn, eller hör till en MCP-server som inte finns i 'mcp_servers'.";
                            return false;
                        }

                        uses.Add(id);
                        segment.Add(new JsonObject
                        {
                            ["type"] = "tool_use", ["id"] = id, ["name"] = McpToolNames.Exposed(server, name), ["input"] = block["input"]?.DeepClone() ?? new JsonObject(),
                        });
                        break;
                    case "mcp_tool_result":
                        if (Str(block!["tool_use_id"]) is not { } useId || !uses.Contains(useId) || !answered.Add(useId))
                        {
                            error = "Ett 'mcp_tool_result'-block hör inte till något tidigare 'mcp_tool_use'-block i samma meddelande.";
                            return false;
                        }

                        var toolResult = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = useId };
                        if (block["content"] is { } resultContent)
                        {
                            toolResult["content"] = resultContent.DeepClone();
                        }

                        if (block["is_error"] is JsonValue e && e.TryGetValue<bool>(out var isError) && isError)
                        {
                            toolResult["is_error"] = true;
                        }

                        pending.Add(toolResult);
                        break;
                    default:
                        if (pending.Count > 0)
                        {
                            Flush();
                            messages.Add(new JsonObject { ["role"] = "user", ["content"] = pending });
                            pending = [];
                        }

                        segment.Add(block);
                        break;
                }
            }

            if (answered.Count != uses.Count)
            {
                error = "Varje 'mcp_tool_use'-block måste följas av sitt 'mcp_tool_result'-block.";
                return false;
            }

            if (segment.Count > 0 || first)
            {
                Flush();
            }

            carried = pending.Count > 0 ? pending : null;
        }

        if (carried is not null)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = carried });
        }

        return true;
    }

    /// <summary>Runs the loop and answers with the combined message. The turn-1 budget is reserved; <paramref name="route"/> gives turn 1's candidates.</summary>
    private async Task RunMessagesMcpAsync(HttpContext http, RequestState state, CatalogSnapshot snapshot, JsonObject body, SelectedRoute route, MessagesMcp mcp,
        CancellationToken ct)
    {
        var key = state.Key!;
        var candidates = route.Candidates;
        var loop = new MessagesMcpLoop(state, options.CurrentValue.Mcp.Loop.DefaultMaxToolCalls)
        {
            OffPrem = candidates.Any(c => c.ModelDeployment!.ProviderAccount!.Residency != DataResidency.OnPrem),
        };
        var started = time.GetTimestamp();
        var template = (JsonObject)body.DeepClone();
        var tools = template["tools"] as JsonArray ?? [];
        foreach (var (spec, entries) in mcp.Servers)
        {
            JsonObject? lastTool = null;
            foreach (var entry in entries)
            {
                lastTool = McpToolFormats.Definition(LlmToolFormat.Anthropic, entry.Tool.ExposedName, entry.Definition);
                tools.Add(lastTool);
            }

            if (lastTool is not null && spec.CacheControl is not null)
            {
                lastTool["cache_control"] = spec.CacheControl.DeepClone();
            }
        }

        if (tools.Count > 0)
        {
            template["tools"] = tools;
        }
        else
        {
            template.Remove("tools");
        }

        var messages = template["messages"] as JsonArray ?? [];
        template["messages"] = messages;
        var turnState = state;
        BudgetReservation? held = null;
        JsonObject? last = null;
        string? stopReason = null;
        string? stopOverride = null;
        try
        {
            while (true)
            {
                if (turnState != state)
                {
                    turnState.Reservation = held;
                    held = null;
                }

                var turnBody = (JsonObject)template.DeepClone();
                if (loop.Turns > 0 && turnBody["tool_choice"] is JsonObject choice && Str(choice["type"]) is "any" or "tool")
                {
                    var auto = new JsonObject { ["type"] = "auto" };
                    if (choice["disable_parallel_tool_use"] is { } parallel)
                    {
                        auto["disable_parallel_tool_use"] = parallel.DeepClone();
                    }

                    turnBody["tool_choice"] = auto;
                }

                var (result, lastFailure) = await SendTurnAsync(turnState, snapshot,
                    loop.Turns == 0 ? candidates : [candidates.First(c => c.ModelDeployment!.Id == state.Deployment!.Id)], turnBody, null, ct);
                var deployment = turnState.Deployment!;
                if (result is not ProviderJsonResult json)
                {
                    if (loop.Turns == 0)
                    {
                        if (result is ProviderFailure clientError)
                        {
                            SetRoutingHeaders(http, deployment, turnState.Fallbacks);
                            http.Response.StatusCode = clientError.StatusCode;
                            http.Response.ContentType = clientError.ContentType ?? "application/json";
                            if (clientError.Body is not null)
                            {
                                await http.Response.WriteAsync(clientError.Body, ct);
                            }

                            await AccountAsync(turnState, deployment, default, clientError.StatusCode, RequestOutcome.ProviderError, GatewayErrorCodes.ProviderRejected, snapshot);
                            return;
                        }

                        await RejectAllFailedAsync(http, turnState, candidates.Count, route.ModelName, lastFailure);
                        return;
                    }

                    (result as IDisposable)?.Dispose();
                    var failure = result as ProviderFailure ?? lastFailure;
                    await AccountAsync(turnState, deployment, default, failure?.StatusCode ?? 502, RequestOutcome.ProviderError,
                        result is ProviderFailure ? GatewayErrorCodes.ProviderRejected : GatewayErrorCodes.AllProvidersFailed, snapshot);
                    stopReason = "provider_error";
                    stopOverride = "pause_turn";
                    break;
                }

                JsonObject? response;
                using (json)
                {
                    response = ParseResponse(json.Body);
                    loop.CostSek += CostCalculator.Calculate(json.Usage, deployment.PriceAt(time.GetUtcNow()), snapshot.SekPerUsd).Sek;
                    loop.Turns++;
                    await AccountAsync(turnState, deployment, json.Usage, json.StatusCode, response is null ? RequestOutcome.ProviderError : RequestOutcome.Success,
                        response is null ? GatewayErrorCodes.ProviderRejected : null, snapshot);
                }

                if (response is null)
                {
                    if (loop.Turns == 1)
                    {
                        await GatewayErrors.WriteAsync(http, GatewayEndpoint.AnthropicMessages, 502, GatewayErrorCodes.AllProvidersFailed,
                            "Leverantörens svar kunde inte läsas.", options.CurrentValue.DocsUrl);
                        return;
                    }

                    stopReason = "provider_error";
                    stopOverride = "pause_turn";
                    break;
                }

                last = response;
                loop.OffPrem = deployment.ProviderAccount!.Residency != DataResidency.OnPrem;
                AddMessagesUsage(loop, response["usage"]);
                var content = response["content"] as JsonArray ?? [];
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content.DeepClone() });

                var mcpCalls = new List<(JsonObject Block, MessagesMcpOffer Offer)>();
                var unknownCalls = new List<JsonObject>();
                var callerCall = false;
                foreach (var block in content.OfType<JsonObject>().Where(b => Str(b["type"]) == "tool_use"))
                {
                    var name = Str(block["name"]) ?? string.Empty;
                    if (mcp.Offered.TryGetValue(name, out var offer))
                    {
                        mcpCalls.Add((block, offer));
                    }
                    else if (mcp.CallerTools.Contains(name))
                    {
                        callerCall = true;
                    }
                    else
                    {
                        unknownCalls.Add(block);
                    }
                }

                loop.NextCalls = mcpCalls.Count + unknownCalls.Count;
                if (loop.NextCalls == 0)
                {
                    AppendMessagesTurn(loop, content, mcp, []);
                    stopReason = loop.BudgetExhausted ? "budget_exceeded" : null;
                    break;
                }

                var toolUse = Str(response["stop_reason"]) == "tool_use";
                string? notRun = null;
                if (!toolUse)
                {
                    notRun = "the response is not complete";
                }
                else if (loop.BudgetExhausted)
                {
                    notRun = stopReason = "budget_exceeded";
                }
                else if (callerCall)
                {
                    var limit = LimitReached(loop, started);
                    notRun = stopReason = limit is "max_tool_calls" or "time_limit" ? limit : null;
                }
                else
                {
                    held = await ReserveNextTurnAsync(loop, template, snapshot, started, ct);
                    notRun = stopReason = held is null ? LimitReached(loop, started) : null;
                }

                var results = new Dictionary<string, (JsonObject ForModel, JsonObject ForClient)>(StringComparer.Ordinal);
                foreach (var (block, offer) in mcpCalls)
                {
                    var id = Str(block["id"]) ?? string.Empty;
                    if (notRun is not null || loop.BudgetExhausted)
                    {
                        results[id] = ToolError(id, $"Not run: {notRun ?? "budget_exceeded"}.");
                        continue;
                    }

                    var run = await RunLoopToolAsync(http, snapshot, loop, offer.Entry.Tool.ExposedName, block["input"]?.ToJsonString() ?? "{}", ct);
                    results[id] = run.Error is { } failed
                        ? ToolError(id, failed)
                        : (McpToolFormats.Result(new LlmToolCall(LlmToolFormat.Anthropic, id, offer.Entry.Tool.ExposedName, null), run.Result),
                            McpToolResultBlock(id, McpToolFormats.Text(run.Result), McpToolFormats.IsError(run.Result)));
                }

                foreach (var block in unknownCalls)
                {
                    loop.ToolCalls++;
                    var id = Str(block["id"]) ?? string.Empty;
                    results[id] = ToolError(id, $"Unknown tool: '{Truncate(Str(block["name"]) ?? string.Empty, 128)}'");
                }

                AppendMessagesTurn(loop, content, mcp, results);
                if (notRun is not null || callerCall)
                {
                    stopOverride = toolUse && !callerCall ? "pause_turn" : null;
                    break;
                }

                messages.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray([.. content.OfType<JsonObject>()
                        .Where(b => Str(b["type"]) == "tool_use" && results.ContainsKey(Str(b["id"]) ?? string.Empty))
                        .Select(b => (JsonNode)results[Str(b["id"])!].ForModel)]),
                });
                turnState = new RequestState(NewRequestId(), GatewayEndpoint.AnthropicMessages, time.GetTimestamp())
                {
                    Key = key, RequestedModel = state.RequestedModel, ParentRequestId = state.RequestId, Deployment = state.Deployment,
                };
            }
        }
        catch (Exception ex) when (turnState != state || state.Accounted)
        {
            if (!turnState.Accounted && turnState.Reservation is not null)
            {
                if (!await AccountUnexpectedAsync(http, turnState, snapshot, ex))
                {
                    throw;
                }
            }
            else if (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                LogUnexpectedFailure(logger, ex, state.RequestId);
                await GatewayErrors.WriteAsync(http, GatewayEndpoint.AnthropicMessages, 500, GatewayErrorCodes.InternalError,
                    "Ett internt fel uppstod i gatewayen. Försök igen; om felet kvarstår, kontakta förvaltningen och ange request-id.", options.CurrentValue.DocsUrl);
            }

            return;
        }
        finally
        {
            if (held is not null)
            {
                await budgets.CommitAsync(held, 0m, CancellationToken.None);
            }
        }

        await WriteMessagesMcpResponseAsync(http, state, loop, last!, stopReason, stopOverride);
    }

    /// <summary>Adds the client's view of a turn: the model's blocks, each MCP call as <c>mcp_tool_use</c> plus <c>mcp_tool_result</c>; calls to unknown tools are left out.</summary>
    private static void AppendMessagesTurn(MessagesMcpLoop loop, JsonArray content, MessagesMcp mcp, Dictionary<string, (JsonObject ForModel, JsonObject ForClient)> results)
    {
        foreach (var block in content.OfType<JsonObject>())
        {
            if (Str(block["type"]) == "tool_use" && Str(block["id"]) is { } id && results.TryGetValue(id, out var result))
            {
                if (mcp.Offered.TryGetValue(Str(block["name"]) ?? string.Empty, out var offer))
                {
                    loop.Content.Add(new JsonObject
                    {
                        ["type"] = "mcp_tool_use", ["id"] = id, ["name"] = offer.Entry.Tool.Name, ["server_name"] = offer.ServerName,
                        ["input"] = block["input"]?.DeepClone() ?? new JsonObject(),
                    });
                    loop.Content.Add(result.ForClient);
                }

                continue;
            }

            loop.Content.Add(block.DeepClone());
        }
    }

    /// <summary>The combined message: the last turn's object with every turn's blocks and the summed usage.</summary>
    private async Task WriteMessagesMcpResponseAsync(HttpContext http, RequestState state, MessagesMcpLoop loop, JsonObject response, string? stopReason, string? stopOverride)
    {
        response["content"] = loop.Content;
        var usage = response["usage"] as JsonObject ?? [];
        usage["input_tokens"] = loop.InputTokens;
        usage["cache_creation_input_tokens"] = loop.CacheCreationTokens;
        usage["cache_read_input_tokens"] = loop.CacheReadTokens;
        usage["output_tokens"] = loop.OutputTokens;
        response["usage"] = usage;
        if (stopOverride is not null)
        {
            response["stop_reason"] = stopOverride;
            response["stop_sequence"] = null;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
        using (usageWriter.TrackPending())
        {
            SetRoutingHeaders(http, state.Deployment!, state.Fallbacks);
            http.Response.Headers[GatewayHeaders.CostSek] = loop.CostSek.ToString("0.000000", CultureInfo.InvariantCulture);
            http.Response.Headers[GatewayHeaders.Turns] = loop.Turns.ToString(CultureInfo.InvariantCulture);
            if (stopReason is not null)
            {
                http.Response.Headers[GatewayHeaders.McpStop] = stopReason;
            }

            if (StrongerPii(http.Response.Headers[GatewayHeaders.Pii].ToString() is { Length: > 0 } before ? before : null, loop.Pii) is { } pii)
            {
                http.Response.Headers[GatewayHeaders.Pii] = pii;
            }

            if (state.Reservation?.RemainingSek is { } remaining)
            {
                http.Response.Headers[GatewayHeaders.BudgetRemainingSek] = FormatSek(Math.Max(0, remaining - loop.CostSek));
            }

            http.Response.StatusCode = 200;
            http.Response.ContentType = "application/json";
            http.Response.ContentLength = bytes.Length;
            await http.Response.Body.WriteAsync(bytes, CancellationToken.None);
        }
    }

    /// <summary>A call the gateway answers with an error: <c>tool_result</c> for the model, <c>mcp_tool_result</c> for the client.</summary>
    private static (JsonObject ForModel, JsonObject ForClient) ToolError(string id, string message) => (
        new JsonObject
        {
            ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }), ["is_error"] = true,
        },
        McpToolResultBlock(id, message, isError: true));

    /// <summary>An <c>mcp_tool_result</c> block; its content is text (images as placeholders), as in Anthropic's connector.</summary>
    private static JsonObject McpToolResultBlock(string id, string text, bool isError) => new()
    {
        ["type"] = "mcp_tool_result", ["tool_use_id"] = id, ["is_error"] = isError,
        ["content"] = text.Length == 0 ? new JsonArray() : new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
    };

    private static void AddMessagesUsage(MessagesMcpLoop loop, JsonNode? usage)
    {
        static long Read(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
        loop.InputTokens += Read(usage?["input_tokens"]);
        loop.OutputTokens += Read(usage?["output_tokens"]);
        loop.CacheCreationTokens += Read(usage?["cache_creation_input_tokens"]);
        loop.CacheReadTokens += Read(usage?["cache_read_input_tokens"]);
    }
}
