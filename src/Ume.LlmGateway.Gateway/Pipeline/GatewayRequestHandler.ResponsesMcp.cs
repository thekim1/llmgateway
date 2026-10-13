using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Providers;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>
/// Hosted <c>mcp</c> tools on <c>/v1/responses</c>: the gateway replaces each <c>mcp</c> tool by the
/// function tools of the server it names, runs the model's calls to them through the shared MCP core, and sends the
/// conversation again (replaying the whole input) until the model is done, a call needs approval, the caller's own
/// function is called or a limit is reached. Turn 1 is the request itself (routing, fallback, PII guard, budget); later
/// turns are pinned to its deployment, reserve their own budget and are accounted with <c>ParentRequestId</c>. A turn
/// whose calls cannot all run now runs none of them, so a stop never leaves tool results the provider has not seen, and no
/// result is stored anywhere. The answer uses OpenAI's hosted MCP items (<c>mcp_list_tools</c>, <c>mcp_call</c>,
/// <c>mcp_approval_request</c>). With <c>stream: true</c> every turn is streamed and relayed as one stream
/// (<see cref="ResponsesMcpStream"/>); the stream starts at turn 1's first event, so fallback happens only before it.
/// </summary>
public sealed partial class GatewayRequestHandler
{
    private const string McpDenied = "The user denied this tool call.";

    /// <summary>The <c>mcp_protocol_error</c> code of an <c>mcp_call</c> the gateway did not run (denied, not run, unknown, withheld).</summary>
    private const int GatewayMcpErrorCode = -32000;

    /// <summary>One offered tool: the request's <c>mcp</c> tool it came from, the catalogue entry, and whether a call needs approval.</summary>
    private sealed record McpOffer(ResponsesMcpTool Spec, McpToolEntry Entry, bool NeedsApproval);

    private enum McpDecision
    {
        Run = 0,
        Deny = 1,
        NotRun = 2,
    }

    /// <summary>A tool call the loop answers: run, refuse, or report as not run. <c>Item</c> is the <c>mcp_call</c> the client gets.</summary>
    private sealed class LoopCall(McpLoopCall call, McpDecision decision, string? reason = null)
    {
        public McpLoopCall Call { get; } = call;
        public McpDecision Decision { get; set; } = decision;
        public string? Reason { get; set; } = reason;
        public JsonObject? Item { get; set; }
    }

    /// <summary>What <see cref="PrepareResponsesMcpAsync"/> found out about the request.</summary>
    private sealed class ResponsesMcp
    {
        public required Dictionary<string, McpOffer> Offered { get; init; }
        public required List<(ResponsesMcpTool Spec, IReadOnlyList<McpToolEntry> Tools)> Servers { get; init; }

        /// <summary>The caller's own <c>function</c> tools: calls to them are returned to the caller.</summary>
        public required HashSet<string> CallerFunctions { get; init; }
        public required JsonNode? OriginalTools { get; init; }
        public required JsonNode? OriginalToolChoice { get; init; }
        public required string? OriginalPreviousResponseId { get; init; }
        public required int MaxToolCalls { get; init; }
        public required bool Store { get; init; }
        public required bool Stream { get; init; }
        public required bool CallerWantsEncryptedReasoning { get; init; }
        public required HashSet<string> ListLabels { get; init; }

        /// <summary>Calls to answer before turn 1 (a continuation): their outputs go right after their <c>function_call</c> or, when the provider has the calls already, first.</summary>
        public List<LoopCall> Pending { get; } = [];
        public bool PrependOutputs { get; set; }
        public string? PendingId { get; set; }
        public List<string> ApprovalIds { get; } = [];
        public Guid? PinnedDeploymentId { get; set; }
    }

    /// <summary>What every gateway-run tool loop counts: turns, tool calls, cost and what limits it.</summary>
    private abstract class McpLoopCore(RequestState first, int maxToolCalls)
    {
        public RequestState First { get; } = first;
        public int MaxToolCalls { get; } = maxToolCalls;
        public int ToolCalls { get; set; }

        /// <summary>The MCP calls of the turn just received, which the next turn would answer.</summary>
        public int NextCalls { get; set; }
        public int Turns { get; set; }
        public decimal CostSek { get; set; }
        public string? Pii { get; set; }
        public bool BudgetExhausted { get; set; }

        /// <summary>Whether the next turn may go to a provider that does not run on-prem (any candidate of turn 1, then the pinned one).</summary>
        public bool OffPrem { get; set; }
    }

    /// <summary>The running loop of one request.</summary>
    private sealed class McpLoop(ResponsesMcp request, RequestState first) : McpLoopCore(first, request.MaxToolCalls)
    {
        public ResponsesMcp Request { get; } = request;
        public JsonArray Output { get; } = [];

        /// <summary>A streamed loop's <c>response.created</c> response from the provider (the base of the final response when no turn completed).</summary>
        public JsonObject? Created { get; set; }

        public long InputTokens { get; set; }
        public long CachedTokens { get; set; }
        public long OutputTokens { get; set; }
        public long ReasoningTokens { get; set; }
    }

    /// <summary>
    /// Validates the <c>mcp</c> tools and turns the request into one the provider can take, except for the function tools,
    /// which are added only when turn 1 is sent (so the PII guard and the token estimate see the caller's body). Null when the
    /// request was refused (written and accounted).
    /// </summary>
    private async Task<ResponsesMcp?> PrepareResponsesMcpAsync(HttpContext http, RequestState state, CatalogSnapshot snapshot, VirtualKey key, JsonObject body,
        CancellationToken ct)
    {
        Task Refuse(int status, string code, string message) => RejectAsync(http, state, status, code, message, RequestOutcome.Rejected);

        if (body["background"] is JsonValue background && background.TryGetValue<bool>(out var inBackground) && inBackground)
        {
            await Refuse(400, GatewayErrorCodes.InvalidRequest, "'background' stöds inte tillsammans med mcp-verktyg.");
            return null;
        }

        if (body["conversation"] is not null)
        {
            await Refuse(400, GatewayErrorCodes.InvalidRequest, "'conversation' stöds inte tillsammans med mcp-verktyg; använd 'previous_response_id' eller skicka hela historiken.");
            return null;
        }

        var originalTools = body["tools"]?.DeepClone();
        var originalChoice = body["tool_choice"]?.DeepClone();
        var callerFunctions = new HashSet<string>(StringComparer.Ordinal);
        var servers = new List<(ResponsesMcpTool, IReadOnlyList<McpToolEntry>)>();
        var offered = new Dictionary<string, McpOffer>(StringComparer.Ordinal);
        var grants = snapshot.McpGrants(key.Id);
        if (body["tools"] is JsonArray tools)
        {
            for (var i = tools.Count - 1; i >= 0; i--)
            {
                if (tools[i] is not JsonObject tool || tool["type"] is not JsonValue t || !t.TryGetValue<string>(out var type))
                {
                    continue;
                }

                if (type == "function" && tool["name"] is JsonValue n && n.TryGetValue<string>(out var functionName))
                {
                    callerFunctions.Add(functionName);
                    continue;
                }

                if (type != "mcp")
                {
                    continue;
                }

                if (ResponsesMcpTool.Parse(tool, out var error) is not { } spec)
                {
                    await Refuse(400, GatewayErrorCodes.InvalidRequest, error!);
                    return null;
                }

                if (servers.Any(s => s.Item1.ServerLabel == spec.ServerLabel))
                {
                    await Refuse(400, GatewayErrorCodes.InvalidRequest, $"Två mcp-verktyg har samma 'server_label' ('{Truncate(spec.ServerLabel, 64)}').");
                    return null;
                }

                if (!snapshot.McpServers.TryGetValue(spec.ServerLabel, out var server)
                    || McpAccess.CheckServer(key, server, grants.GetValueOrDefault(server.Id), circuitOpen: false) != McpDenial.None)
                {
                    await Refuse(404, GatewayErrorCodes.McpServerNotFound, $"MCP-servern '{Truncate(spec.ServerLabel, 64)}' finns inte eller får inte användas med nyckeln.");
                    return null;
                }

                if (spec.ServerUrl is not null && spec.ServerUrl != server.Url)
                {
                    await Refuse(400, GatewayErrorCodes.InvalidRequest,
                        $"mcp-verktyget '{spec.ServerLabel}': 'server_url' får bara vara MCP-serverns registrerade adress; gatewayen ansluter aldrig till andra adresser.");
                    return null;
                }

                var grant = grants[server.Id];
                var visible = (await VisibleToolsAsync(snapshot, key, server, ct))
                    .Where(e => (spec.AllowedTools is null || spec.AllowedTools.Contains(e.Tool.Name)) && (!spec.ReadOnlyOnly || e.Definition.ReadOnly))
                    .OrderBy(e => e.Tool.Name, StringComparer.Ordinal)
                    .ToList();
                foreach (var entry in visible)
                {
                    var byGrant = grant.ApprovalPolicy == McpApprovalPolicy.Always
                        || (grant.ApprovalPolicy == McpApprovalPolicy.NeverForReadOnly && !entry.Definition.ReadOnly);
                    offered[entry.Tool.ExposedName] = new McpOffer(spec, entry, byGrant || spec.RequiresApproval(entry.Tool.Name));
                }

                servers.Add((spec, visible));
                tools.RemoveAt(i);
            }
        }

        servers.Reverse();
        if (callerFunctions.FirstOrDefault(offered.ContainsKey) is { } clash)
        {
            await Refuse(400, GatewayErrorCodes.InvalidRequest, $"Funktionsverktyget '{clash}' har samma namn som ett MCP-verktyg.");
            return null;
        }

        if (body["tool_choice"] is JsonObject choice && Str(choice["type"]) == "mcp")
        {
            var label = Str(choice["server_label"]);
            var name = Str(choice["name"]);
            var match = offered.Values.FirstOrDefault(o => o.Spec.ServerLabel == label && o.Entry.Tool.Name == name);
            if (match is null)
            {
                await Refuse(400, GatewayErrorCodes.InvalidRequest, "'tool_choice' väljer ett MCP-verktyg som inte erbjuds i förfrågan.");
                return null;
            }

            body["tool_choice"] = new JsonObject { ["type"] = "function", ["name"] = match.Entry.Tool.ExposedName };
        }

        var loopOptions = options.CurrentValue.Mcp.Loop;
        var maxToolCalls = body["max_tool_calls"] is JsonValue m && m.TryGetValue<int>(out var requestedMax) && requestedMax > 0
            ? Math.Min(requestedMax, loopOptions.MaxToolCalls)
            : loopOptions.DefaultMaxToolCalls;
        body.Remove("max_tool_calls");

        var include = body["include"] as JsonArray;
        var previous = body["previous_response_id"] is JsonValue p && p.TryGetValue<string>(out var previousId) ? previousId : null;
        if (body["input"] is JsonValue text && text.TryGetValue<string>(out var prompt))
        {
            body["input"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt });
        }

        var input = body["input"] as JsonArray ?? [];
        body["input"] = input;
        var listLabels = new HashSet<string>(StringComparer.Ordinal);
        if (previous is null)
        {
            foreach (var (spec, _) in servers)
            {
                if (!input.Any(i => Str(i?["type"]) == "mcp_list_tools" && Str(i!["server_label"]) == spec.ServerLabel))
                {
                    listLabels.Add(spec.ServerLabel);
                }
            }
        }

        var mcp = new ResponsesMcp
        {
            Offered = offered, Servers = servers, CallerFunctions = callerFunctions, OriginalTools = originalTools, OriginalToolChoice = originalChoice,
            OriginalPreviousResponseId = previous, MaxToolCalls = maxToolCalls, Stream = RequestRewriter.IsStreaming(body), Store = body["store"] is not JsonValue st || !st.TryGetValue<bool>(out var store) || store,
            CallerWantsEncryptedReasoning = include?.Any(i => i is JsonValue v && v.TryGetValue<string>(out var s) && s == "reasoning.encrypted_content") == true,
            ListLabels = listLabels,
        };

        if (previous is not null && previous.StartsWith(ResponsesMcpTool.GatewayResponsePrefix, StringComparison.Ordinal))
        {
            var pendingId = previous[ResponsesMcpTool.GatewayResponsePrefix.Length..];
            if (await mcpStates.GetPendingAsync(key.Id, pendingId, take: false, ct) is not { } pending)
            {
                await Refuse(404, GatewayErrorCodes.McpContinuationNotFound, "Svaret att fortsätta från finns inte, har gått ut eller har redan fortsatts.");
                return null;
            }

            body["previous_response_id"] = pending.ProviderResponseId;
            mcp.PendingId = pendingId;
            mcp.PinnedDeploymentId = pending.DeploymentId;
            mcp.PrependOutputs = true;
            state.ParentRequestId = pending.ParentRequestId;
            var answers = Answers(input, pending.Calls.Where(c => c.ApprovalId is not null).Select(c => c.ApprovalId!).ToHashSet(StringComparer.Ordinal));
            foreach (var call in pending.Calls)
            {
                if (call.NotRun is { } notRun)
                {
                    mcp.Pending.Add(new LoopCall(call, McpDecision.NotRun, notRun));
                }
                else if (call.ApprovalId is { } approvalId)
                {
                    mcp.ApprovalIds.Add(approvalId);
                    mcp.Pending.Add(answers.TryGetValue(approvalId, out var answer) && answer.Approve
                        ? new LoopCall(call, McpDecision.Run)
                        : new LoopCall(call, McpDecision.Deny, answer.Reason));
                }
                else
                {
                    mcp.Pending.Add(new LoopCall(call, McpDecision.Run));
                }
            }

            RemoveItems(input, i => Str(i["type"]) == "mcp_approval_response" && Str(i["approval_request_id"]) is { } id && answers.ContainsKey(id));
        }

        return await TranslateReplayAsync(http, state, key, mcp, input, ct) ? mcp : null;
    }

    /// <summary>
    /// Earlier MCP items the client sends back (history replayed instead of <c>previous_response_id</c>), as the provider
    /// understands them: <c>mcp_call</c> becomes the <c>function_call</c> and its output (a call still marked
    /// <c>calling</c> is run now), an answered <c>mcp_approval_request</c> becomes the stored call, run or refused, and
    /// <c>mcp_list_tools</c> is left out. False when the request was refused.
    /// </summary>
    private async Task<bool> TranslateReplayAsync(HttpContext http, RequestState state, VirtualKey key, ResponsesMcp mcp, JsonArray input, CancellationToken ct)
    {
        Task Refuse(int status, string code, string message) => RejectAsync(http, state, status, code, message, RequestOutcome.Rejected);

        if (!input.Any(i => i?["type"] is JsonValue t && t.TryGetValue<string>(out var type) && type.StartsWith("mcp_", StringComparison.Ordinal)))
        {
            return true;
        }

        var resolved = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var approvalsDone = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in input.OfType<JsonObject>().Where(i => Str(i["type"]) == "mcp_call"))
        {
            if (Str(item["id"]) is { } id && (item["output"] is not null || item["error"] is not null))
            {
                resolved[id] = item;
            }

            if (Str(item["approval_request_id"]) is { } approval)
            {
                approvalsDone.Add(approval);
            }
        }

        var requested = input.OfType<JsonObject>().Where(i => Str(i["type"]) == "mcp_approval_request").Select(i => Str(i["id"])).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var answers = Answers(input, requested);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var items = input.ToList();
        input.Clear();
        foreach (var node in items)
        {
            if (node is not JsonObject item)
            {
                input.Add(node);
                continue;
            }

            switch (Str(item["type"]))
            {
                case "mcp_list_tools":
                case "mcp_approval_response" when Str(item["approval_request_id"]) is { } answered && (requested.Contains(answered) || approvalsDone.Contains(answered)):
                    break;
                case "mcp_approval_response":
                    await Refuse(400, GatewayErrorCodes.McpApprovalNotFound, "Ett 'mcp_approval_response' hör inte till någon godkännandebegäran i förfrågan.");
                    return false;
                case "mcp_call":
                    var label = Str(item["server_label"]);
                    var name = Str(item["name"]);
                    if (Str(item["id"]) is not { } callId || label is null || name is null || !mcp.Servers.Any(s => s.Spec.ServerLabel == label))
                    {
                        await Refuse(400, GatewayErrorCodes.InvalidRequest, "Ett 'mcp_call' saknar id, namn eller hör till en MCP-server som förfrågan inte har som mcp-verktyg.");
                        return false;
                    }

                    if (!emitted.Add(callId))
                    {
                        break;
                    }

                    var exposed = McpToolNames.Exposed(label, name);
                    var arguments = Str(item["arguments"]) ?? "{}";
                    input.Add(FunctionCall(callId, exposed, arguments));
                    if (resolved.TryGetValue(callId, out var done))
                    {
                        input.Add(FunctionCallOutput(callId, ErrorText(done["error"]) is { } failed ? McpToolFormats.ErrorPrefix + failed : Str(done["output"]) ?? string.Empty));
                    }
                    else
                    {
                        mcp.Pending.Add(new LoopCall(new McpLoopCall(callId, label, name, exposed, arguments, null, null), McpDecision.Run));
                    }

                    break;
                case "mcp_approval_request":
                    var approvalId = Str(item["id"]);
                    if (approvalId is null || approvalsDone.Contains(approvalId) || !answers.TryGetValue(approvalId, out var answer))
                    {
                        break;
                    }

                    if (await mcpStates.GetApprovalAsync(key.Id, approvalId, take: false, ct) is not { } stored)
                    {
                        await Refuse(404, GatewayErrorCodes.McpApprovalNotFound, "Godkännandebegäran finns inte, har gått ut eller har redan besvarats.");
                        return false;
                    }

                    var call = stored.Call;
                    if (Str(item["server_label"]) != call.ServerLabel || Str(item["name"]) != call.Tool || Str(item["arguments"]) != call.Arguments)
                    {
                        await Refuse(400, GatewayErrorCodes.McpApprovalMismatch, "Godkännandebegäran har ändrats: verktyg och argument måste vara exakt de som modellen angav.");
                        return false;
                    }

                    state.ParentRequestId ??= stored.ParentRequestId;
                    mcp.ApprovalIds.Add(approvalId);
                    input.Add(FunctionCall(call.CallId, call.Exposed, call.Arguments));
                    mcp.Pending.Add(answer.Approve ? new LoopCall(call, McpDecision.Run) : new LoopCall(call, McpDecision.Deny, answer.Reason));
                    break;
                default:
                    input.Add(item);
                    break;
            }
        }

        return true;
    }

    /// <summary>The <c>mcp_approval_response</c> items in <paramref name="input"/> for <paramref name="ids"/>.</summary>
    private static Dictionary<string, (bool Approve, string? Reason)> Answers(JsonArray input, HashSet<string> ids)
    {
        var answers = new Dictionary<string, (bool, string?)>(StringComparer.Ordinal);
        foreach (var item in input.OfType<JsonObject>().Where(i => Str(i["type"]) == "mcp_approval_response"))
        {
            if (Str(item["approval_request_id"]) is { } id && ids.Contains(id))
            {
                answers[id] = (item["approve"] is JsonValue a && a.TryGetValue<bool>(out var approve) && approve, Str(item["reason"]));
            }
        }

        return answers;
    }

    /// <summary>Runs the loop and answers with the combined response. The turn-1 budget is reserved; <paramref name="route"/> gives turn 1's candidates.</summary>
    private async Task RunResponsesMcpAsync(HttpContext http, RequestState state, CatalogSnapshot snapshot, JsonObject body, SelectedRoute route, ResponsesMcp mcp,
        CancellationToken ct)
    {
        var key = state.Key!;
        var candidates = route.Candidates;
        if (mcp.PinnedDeploymentId is { } pinned)
        {
            candidates = [.. candidates.Where(c => c.ModelDeployment!.Id == pinned)];
            if (candidates.Count == 0)
            {
                await RejectAsync(http, state, 409, GatewayErrorCodes.McpContinuationNotFound,
                    "Modellen som svaret kom från kan inte användas för fortsättningen (nyckeln, routningen eller leverantören har ändrats).", RequestOutcome.Rejected);
                return;
            }
        }

        // Single use: whoever takes the state first continues; a concurrent copy of the request finds nothing. A stored loop
        // and a replayed history share the approval records, so an approval used on one path is gone on the other.
        var taken = mcp.PendingId is null || await mcpStates.GetPendingAsync(key.Id, mcp.PendingId, take: true, ct) is not null;
        foreach (var approvalId in mcp.ApprovalIds)
        {
            if (await mcpStates.GetApprovalAsync(key.Id, approvalId, take: true, ct) is not { } approval)
            {
                taken = false;
            }
            else if (approval.PendingId is { } replayedPending && replayedPending != mcp.PendingId)
            {
                await mcpStates.GetPendingAsync(key.Id, replayedPending, take: true, ct);
            }
        }

        if (!taken)
        {
            await RejectAsync(http, state, 409, GatewayErrorCodes.McpApprovalNotFound, "Godkännandet har redan använts eller har gått ut.", RequestOutcome.Rejected);
            return;
        }

        var loop = new McpLoop(mcp, state) { OffPrem = candidates.Any(c => c.ModelDeployment!.ProviderAccount!.Residency != DataResidency.OnPrem) };
        var started = time.GetTimestamp();
        foreach (var label in mcp.ListLabels)
        {
            var (spec, tools) = mcp.Servers.Single(s => s.Spec.ServerLabel == label);
            loop.Output.Add(ListToolsItem(spec.ServerLabel, tools));
        }

        var template = (JsonObject)body.DeepClone();
        var functionTools = template["tools"] as JsonArray ?? [];
        template["tools"] = functionTools;
        foreach (var offer in mcp.Offered.Values.OrderBy(o => o.Entry.Tool.ExposedName, StringComparer.Ordinal))
        {
            functionTools.Add(McpToolFormats.Definition(LlmToolFormat.Responses, offer.Entry.Tool.ExposedName, offer.Entry.Definition));
        }

        var input = (JsonArray)template["input"]!;
        var pendingCalls = mcp.Pending;
        var turnState = state;
        BudgetReservation? held = null;
        JsonObject? last = null;
        string? stopReason = null;
        string? providerStatus = null;
        JsonNode? providerIncomplete = null;
        List<LoopCall> owed = [];
        var stream = mcp.Stream
            ? new ResponsesMcpStream(http, TimeSpan.FromSeconds(options.CurrentValue.Mcp.Loop.KeepAliveSeconds), mcp.CallerWantsEncryptedReasoning)
            : null;
        Task<T> KeepingAlive<T>(Task<T> work) => stream is { Started: true } ? stream.KeepAliveAsync(work, ct) : work;
        try
        {
            while (true)
            {
                // Calls of the previous turn (or of a continuation): the next turn's budget is already held.
                var prepend = 0;
                foreach (var call in pendingCalls)
                {
                    var shownAt = stream is { Started: true } && call.Item is { } shown ? loop.Output.IndexOf(shown) : -1;
                    if (shownAt >= 0)
                    {
                        await stream!.ItemEventAsync("response.mcp_call.in_progress", shownAt, call.Call.CallId, ct);
                    }

                    var (output, item) = await KeepingAlive(AnswerCallAsync(http, snapshot, loop, call, ct));
                    if (shownAt >= 0)
                    {
                        await stream!.ItemEventAsync(Str(item["status"]) == "completed" ? "response.mcp_call.completed" : "response.mcp_call.failed", shownAt, call.Call.CallId, ct);
                        await stream.ItemDoneAsync(shownAt, item, ct);
                    }

                    if (turnState == state && !IsUnknown(call.Call))
                    {
                        loop.Output.Add(item);
                    }

                    if (mcp.PrependOutputs && turnState == state)
                    {
                        input.Insert(prepend++, output);
                    }
                    else
                    {
                        InsertOutput(input, output, call.Call.CallId);
                    }
                }

                if (turnState != state)
                {
                    turnState.Reservation = held;
                    held = null;
                }

                if (mcp.PrependOutputs && turnState == state)
                {
                    mcp.PrependOutputs = false;
                }

                var turnBody = (JsonObject)template.DeepClone();
                if (loop.Turns > 0 && turnBody["tool_choice"] is { } forced && (forced is JsonObject || Str(forced) == "required"))
                {
                    turnBody["tool_choice"] = "auto";
                }

                var (result, lastFailure) = await KeepingAlive(SendTurnAsync(turnState, snapshot, TurnCandidates(loop, candidates), turnBody, mcp, ct));
                var deployment = turnState.Deployment!;
                var turnStart = loop.Output.Count;
                var deferred = new HashSet<string>(StringComparer.Ordinal);
                if (result is not (ProviderJsonResult or ProviderStreamResult))
                {
                    if (await AnswerFailedTurnAsync(http, loop, turnState, snapshot, candidates.Count, route.ModelName, result, lastFailure, ct))
                    {
                        return;
                    }

                    stopReason = "provider_error";
                    break;
                }

                JsonObject response;
                using (result as IDisposable)
                {
                    JsonObject? parsed;
                    TokenUsage usage;
                    int statusCode;
                    string? errorCode;
                    if (result is ProviderJsonResult json)
                    {
                        parsed = ParseResponse(json.Body);
                        (usage, statusCode, errorCode) = (json.Usage, json.StatusCode, GatewayErrorCodes.ProviderRejected);
                    }
                    else
                    {
                        var streamed = (ProviderStreamResult)result;
                        parsed = await RelayMcpTurnAsync(http, state, loop, stream!, streamed, deferred, ct);
                        last ??= loop.Created;
                        (usage, statusCode, errorCode) = (streamed.Usage.ToTokenUsage(turnState.EstimatedInputTokens), parsed is null ? 502 : 200, "stream_interrupted");
                    }

                    var cost = CostCalculator.Calculate(usage, deployment.PriceAt(time.GetUtcNow()), snapshot.SekPerUsd);
                    loop.CostSek += cost.Sek;
                    loop.Turns++;
                    await AccountAsync(turnState, deployment, usage, statusCode, parsed is null ? RequestOutcome.ProviderError : RequestOutcome.Success,
                        parsed is null ? errorCode : null, snapshot);
                    if (parsed is null)
                    {
                        if (loop.Turns == 1 && !http.Response.HasStarted)
                        {
                            await GatewayErrors.WriteAsync(http, GatewayEndpoint.Responses, 502, GatewayErrorCodes.AllProvidersFailed,
                                "Leverantörens svar kunde inte läsas.", options.CurrentValue.DocsUrl);
                            return;
                        }

                        stopReason = "provider_error";
                        break;
                    }

                    response = parsed;
                }

                last = response;
                loop.OffPrem = deployment.ProviderAccount!.Residency != DataResidency.OnPrem;
                AddUsage(loop, response["usage"]);
                var turnOutput = response["output"] as JsonArray ?? [];
                // Only function_call items are tool calls the loop answers; other items (a provider's own hosted tools such as
                // web_search_call, reasoning, messages) pass through unchanged.
                var mcpCalls = new List<(JsonObject Item, McpOffer Offer)>();
                var unknownCalls = new List<JsonObject>();
                var callerCall = false;
                foreach (var item in turnOutput.OfType<JsonObject>())
                {
                    input.Add(item.DeepClone());
                    if (Str(item["type"]) != "function_call")
                    {
                        continue;
                    }

                    var name = Str(item["name"]) ?? string.Empty;
                    if (mcp.Offered.TryGetValue(name, out var offer))
                    {
                        mcpCalls.Add((item, offer));
                    }
                    else if (mcp.CallerFunctions.Contains(name))
                    {
                        callerCall = true;
                    }
                    else
                    {
                        unknownCalls.Add(item);
                    }
                }

                var status = Str(response["status"]);
                loop.NextCalls = mcpCalls.Count + unknownCalls.Count;
                var next = new List<LoopCall>();
                string? notRun = null;
                var approvals = false;
                if (status != "completed")
                {
                    providerStatus = status;
                    providerIncomplete = response["incomplete_details"]?.DeepClone();
                    notRun = "the response is not complete";
                }
                else if (loop.NextCalls == 0)
                {
                    stopReason = loop.BudgetExhausted ? "budget_exceeded" : null;
                    AppendTurn(loop, turnOutput, next);
                    break;
                }
                else if (loop.BudgetExhausted)
                {
                    notRun = stopReason = "budget_exceeded";
                }
                else if (!(approvals = mcpCalls.Any(c => c.Offer!.NeedsApproval)) && !callerCall)
                {
                    held = await KeepingAlive(ReserveNextTurnAsync(loop, template, snapshot, started, ct));
                    notRun = stopReason = held is null ? LimitReached(loop, started) : null;
                }

                foreach (var (item, offer) in mcpCalls)
                {
                    var call = new McpLoopCall(Str(item["call_id"]) ?? string.Empty, offer!.Spec.ServerLabel, offer.Entry.Tool.Name, offer.Entry.Tool.ExposedName,
                        Str(item["arguments"]) ?? "{}", notRun is null && offer.NeedsApproval ? "mcpr_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) : null,
                        notRun is null ? null : $"Not run: {notRun}.");
                    next.Add(new LoopCall(call, call.NotRun is not null ? McpDecision.NotRun : McpDecision.Run));
                }

                foreach (var item in unknownCalls)
                {
                    var name = Str(item["name"]) ?? string.Empty;
                    next.Add(new LoopCall(new McpLoopCall(Str(item["call_id"]) ?? string.Empty, string.Empty, name, name, Str(item["arguments"]) ?? "{}", null,
                        $"Unknown tool: '{Truncate(name, 128)}'"), McpDecision.NotRun));
                }

                AppendTurn(loop, turnOutput, next);
                var stops = notRun is not null || callerCall || approvals;
                if (stream is { Started: true })
                {
                    await EmitTurnCallsAsync(stream, loop, turnStart, deferred, stops, ct);
                }

                if (stops)
                {
                    owed = next;
                    break;
                }

                pendingCalls = next;
                turnState = NextTurnState(loop, GatewayEndpoint.Responses);
            }
        }
        catch (Exception ex) when (turnState != state || state.Accounted)
        {
            // Turn 1 not accounted yet: HandleAsync accounts it. Otherwise the turn in flight is accounted here.
            if (!await AnswerLoopFailureAsync(http, state, turnState, snapshot, ex, GatewayEndpoint.Responses, ct))
            {
                throw;
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

        await WriteMcpResponseAsync(http, state, loop, stream, last!, stopReason, providerStatus, providerIncomplete, owed);
    }

    /// <summary>
    /// The budget for the next turn, reserved before this turn's calls run; null when a limit stops the loop instead
    /// (<see cref="LimitReached"/> says which).
    /// </summary>
    private async Task<BudgetReservation?> ReserveNextTurnAsync(McpLoopCore loop, JsonObject template, CatalogSnapshot snapshot, long started, CancellationToken ct)
    {
        if (LimitReached(loop, started) is not "budget_exceeded")
        {
            return null;
        }

        var deployment = loop.First.Deployment!;
        var usage = new TokenUsage(CostCalculator.EstimateInputTokens(System.Text.Encoding.UTF8.GetByteCount(template.ToJsonString()), default), 0,
            RequestRewriter.RequestedMaxOutputTokens(template) ?? options.CurrentValue.DefaultOutputTokenEstimate);
        var estimate = CostCalculator.Calculate(usage, deployment.PriceAt(time.GetUtcNow()), snapshot.SekPerUsd).Sek;
        var reservation = await budgets.ReserveAsync(BudgetService.ApplicableBudgets(snapshot, loop.First.Key!), estimate, ct);
        return reservation.Allowed ? reservation : null;
    }

    /// <summary>Turn 1 may fall back across the route; later turns stay on the deployment turn 1 used.</summary>
    private static IReadOnlyList<RouteTarget> TurnCandidates(McpLoopCore loop, IReadOnlyList<RouteTarget> candidates) =>
        loop.Turns == 0 ? candidates : [candidates.First(c => c.ModelDeployment!.Id == loop.First.Deployment!.Id)];

    /// <summary>The usage record of the next turn: a child of the request, on the deployment turn 1 used.</summary>
    private RequestState NextTurnState(McpLoopCore loop, GatewayEndpoint endpoint) => new(NewRequestId(), endpoint, time.GetTimestamp())
    {
        Key = loop.First.Key, RequestedModel = loop.First.RequestedModel, ParentRequestId = loop.First.RequestId, Deployment = loop.First.Deployment,
    };

    /// <summary>
    /// A turn no provider answered. On turn 1 the client gets the provider's own error, or the gateway's when every
    /// candidate failed (true: answered). A later turn is accounted as a provider error and the loop ends with what it has (false).
    /// </summary>
    private async Task<bool> AnswerFailedTurnAsync(HttpContext http, McpLoopCore loop, RequestState turnState, CatalogSnapshot snapshot, int candidateCount,
        string modelName, ProviderResult? result, ProviderFailure? lastFailure, CancellationToken ct)
    {
        (result as IDisposable)?.Dispose();
        var deployment = turnState.Deployment!;
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
                return true;
            }

            await RejectAllFailedAsync(http, turnState, candidateCount, modelName, lastFailure);
            return true;
        }

        var failure = result as ProviderFailure ?? lastFailure;
        await AccountAsync(turnState, deployment, default, failure?.StatusCode ?? 502, RequestOutcome.ProviderError,
            result is ProviderFailure ? GatewayErrorCodes.ProviderRejected : GatewayErrorCodes.AllProvidersFailed, snapshot);
        return false;
    }

    /// <summary>
    /// An unexpected failure once the loop is past turn 1 or turn 1 is accounted: the turn in flight is accounted, otherwise
    /// the client gets a 500 if nothing was sent yet. False: the caller rethrows.
    /// </summary>
    private async Task<bool> AnswerLoopFailureAsync(HttpContext http, RequestState state, RequestState turnState, CatalogSnapshot snapshot, Exception ex,
        GatewayEndpoint endpoint, CancellationToken ct)
    {
        if (!turnState.Accounted && turnState.Reservation is not null)
        {
            return await AccountUnexpectedAsync(http, turnState, snapshot, ex);
        }

        if (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogUnexpectedFailure(logger, ex, state.RequestId);
            if (http.Response.HasStarted)
            {
                return false;
            }

            await GatewayErrors.WriteAsync(http, endpoint, 500, GatewayErrorCodes.InternalError,
                "Ett internt fel uppstod i gatewayen. Försök igen; om felet kvarstår, kontakta förvaltningen och ange request-id.", options.CurrentValue.DocsUrl);
        }

        return true;
    }

    /// <summary>The limit that stops the loop before another turn; <c>budget_exceeded</c> when only the budget can still stop it.</summary>
    private string LimitReached(McpLoopCore loop, long started)
    {
        var limits = options.CurrentValue.Mcp.Loop;
        return loop.ToolCalls + loop.NextCalls > loop.MaxToolCalls ? "max_tool_calls"
            : loop.Turns >= limits.MaxTurns ? "max_turns"
            : time.GetElapsedTime(started).TotalSeconds >= limits.TimeoutSeconds ? "time_limit"
            : "budget_exceeded";
    }

    /// <summary>
    /// Turn 1 with the route's fallback, later turns on the pinned deployment only, to <paramref name="turn"/>'s endpoint
    /// (Responses when <paramref name="mcp"/> is set, otherwise not streamed). Null result: every candidate failed.
    /// </summary>
    private async Task<(ProviderResult? Result, ProviderFailure? LastFailure)> SendTurnAsync(RequestState turn, CatalogSnapshot snapshot, IReadOnlyList<RouteTarget> candidates,
        JsonObject body, ResponsesMcp? mcp, CancellationToken ct)
    {
        var stream = mcp?.Stream ?? false;
        ProviderFailure? lastFailure = null;
        if (turn.EstimatedInputTokens == 0)
        {
            // Later turns: what a cancelled or broken turn is accounted with, like turn 1's estimate.
            turn.EstimatedInputTokens = CostCalculator.EstimateInputTokens(System.Text.Encoding.UTF8.GetByteCount(body.ToJsonString()), default);
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            var deployment = candidates[i].ModelDeployment!;
            var provider = deployment.ProviderAccount!;
            turn.Fallbacks = i;
            turn.Deployment = deployment;
            if (!TryGetCredential(snapshot, provider, out var credential))
            {
                lastFailure = ProviderFailure.CredentialUnavailable;
                continue;
            }

            var attempt = i == candidates.Count - 1 ? body : (JsonObject)body.DeepClone();
            if (mcp is { Store: false, CallerWantsEncryptedReasoning: false } && deployment.ParameterProfile == ParameterProfile.OpenAIReasoning)
            {
                var include = attempt["include"] as JsonArray ?? [];
                include.Add("reasoning.encrypted_content");
                attempt["include"] = include;
            }

            RequestRewriter.Apply(attempt, turn.Endpoint, deployment.UpstreamModel, deployment.ParameterProfile, stream);
            ProviderResult result;
            try
            {
                result = await _adapters.Resolve(provider.Type).SendAsync(new ProviderCall(provider, deployment, turn.Endpoint, attempt, stream, credential, false, null), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await AccountAsync(turn, deployment, new TokenUsage(turn.EstimatedInputTokens, 0, 0), 499, RequestOutcome.ClientCancelled, null, snapshot);
                throw;
            }

            switch (result)
            {
                case ProviderFailure failure when failure.CanFallBack:
                    await RecordFailureAsync(turn, provider, failure);
                    lastFailure = failure;
                    continue;
                case ProviderFailure failure:
                    return (failure, failure);
                default:
                    await RecordSuccessAsync(turn, provider);
                    return (result, null);
            }
        }

        return (null, lastFailure);
    }

    /// <summary>Answers one call: runs it through the shared MCP core (accounted as <see cref="GatewayEndpoint.McpToolCall"/>), or refuses it.</summary>
    private async Task<(JsonObject Output, JsonObject Item)> AnswerCallAsync(HttpContext http, CatalogSnapshot snapshot, McpLoop loop, LoopCall call, CancellationToken ct)
    {
        var c = call.Call;
        var item = call.Item ?? McpCallItem(c);
        call.Item = item;
        string? error = null;
        var errorType = "mcp_protocol_error";
        var errorCode = GatewayMcpErrorCode;
        JsonNode? output = null;
        if (call.Decision == McpDecision.Deny)
        {
            error = string.IsNullOrWhiteSpace(call.Reason) ? McpDenied : $"{McpDenied} Reason: {Truncate(call.Reason, 500)}";
        }
        else if (IsUnknown(c))
        {
            loop.ToolCalls++;
            error = c.NotRun;
        }
        else if (call.Decision == McpDecision.NotRun || loop.BudgetExhausted)
        {
            error = call.Reason ?? c.NotRun ?? "Not run: budget_exceeded.";
        }
        else if (!loop.Request.Offered.TryGetValue(c.Exposed, out var offer) || offer.Spec.ServerLabel != c.ServerLabel)
        {
            error = $"Unknown tool: '{c.Exposed}'";
        }
        else
        {
            var run = await RunLoopToolAsync(http, snapshot, loop, c.Exposed, c.Arguments, ct);
            if (run.Error is not null)
            {
                (error, errorType) = (run.Error, run.ErrorType ?? errorType);
                errorCode = run.ErrorCode ?? errorCode;
            }
            else
            {
                output = McpToolFormats.Result(new LlmToolCall(LlmToolFormat.Responses, c.CallId, c.Exposed, null), run.Result)["output"]!.DeepClone();
                var text = McpToolFormats.Text(run.Result);
                if (McpToolFormats.IsError(run.Result))
                {
                    item["error"] = new JsonObject { ["type"] = "mcp_tool_execution_error", ["content"] = text };
                    item["status"] = "failed";
                }
                else
                {
                    item["output"] = text;
                    item["status"] = "completed";
                }
            }
        }

        if (error is not null)
        {
            item["error"] = McpCallError(error, errorType, errorCode);
            item["status"] = call.Decision == McpDecision.NotRun ? "incomplete" : "failed";
            output = McpToolFormats.ErrorPrefix + error;
        }

        return (FunctionCallOutput(c.CallId, output!), item);
    }

    /// <summary>The MCP result of a tool the loop ran, or why there is none (with an OpenAI <c>McpToolCallError</c> type and code when they differ from the default).</summary>
    private readonly record struct LoopToolRun(JsonNode? Result, string? Error, string? ErrorType, int? ErrorCode);

    /// <summary>
    /// Runs one offered tool through the shared MCP core (accounted as <see cref="GatewayEndpoint.McpToolCall"/> with the
    /// loop's <c>ParentRequestId</c>) and counts it. A result with personal data is withheld from a model that is not on-prem
    /// when the key reroutes PII.
    /// </summary>
    private async Task<LoopToolRun> RunLoopToolAsync(HttpContext http, CatalogSnapshot snapshot, McpLoopCore loop, string exposed, string argumentsJson, CancellationToken ct)
    {
        JsonNode? arguments;
        try
        {
            arguments = string.IsNullOrWhiteSpace(argumentsJson) ? null : JsonNode.Parse(argumentsJson, documentOptions: JsonOptions);
        }
        catch (JsonException)
        {
            arguments = JsonValue.Create(argumentsJson);
        }

        var toolState = new RequestState(NewRequestId(), GatewayEndpoint.McpToolCall, time.GetTimestamp())
        {
            Key = loop.First.Key, McpMethod = "tools/call", ParentRequestId = loop.First.RequestId,
        };
        var answer = new McpLoopAnswer(http);
        await CallToolAsync(answer, toolState, snapshot, null, exposed, arguments, null, ct);
        ct.ThrowIfCancellationRequested();
        loop.ToolCalls++;
        loop.CostSek += answer.CostSek;
        loop.Pii = StrongerPii(loop.Pii, answer.Pii);
        if (answer.ErrorCode == GatewayErrorCodes.BudgetExceeded)
        {
            loop.BudgetExhausted = true;
        }

        if (answer.Error is not null || !answer.Answered)
        {
            var error = answer.Error ?? "The tool call failed.";
            return answer.ErrorCode is GatewayErrorCodes.McpUpstreamFailed or GatewayErrorCodes.McpResultTooLarge
                ? new LoopToolRun(null, error, "http_error", answer.Status)
                : new LoopToolRun(null, error, null, answer.Answered ? answer.RpcCode : null);
        }

        if (loop.First.Key!.PiiPolicy == PiiPolicy.RerouteToOnPrem && loop.OffPrem && McpPii.ScanResult(answer.Result?.DeepClone(), redact: false).HasPii)
        {
            loop.Pii = StrongerPii(loop.Pii, "rerouted-onprem");
            return new LoopToolRun(null, "Result withheld: it contains personal data and the model does not run on-prem.", null, null);
        }

        return new LoopToolRun(answer.Result, null, null, null);
    }

    /// <summary>Adds the client's view of a turn: the model's items, its MCP calls as <c>mcp_call</c> or <c>mcp_approval_request</c>.</summary>
    private static void AppendTurn(McpLoop loop, JsonArray turnOutput, List<LoopCall> calls)
    {
        var byCallId = calls.ToDictionary(c => c.Call.CallId, StringComparer.Ordinal);
        foreach (var item in turnOutput.OfType<JsonObject>())
        {
            if (Str(item["type"]) == "function_call" && Str(item["call_id"]) is { } callId && byCallId.TryGetValue(callId, out var call))
            {
                var c = call.Call;
                if (IsUnknown(c))
                {
                    continue;
                }

                if (c.ApprovalId is not null)
                {
                    loop.Output.Add(new JsonObject
                    {
                        ["type"] = "mcp_approval_request", ["id"] = c.ApprovalId, ["server_label"] = c.ServerLabel, ["name"] = c.Tool, ["arguments"] = c.Arguments,
                    });
                    continue;
                }

                call.Item = McpCallItem(c);
                if (c.NotRun is { } notRun)
                {
                    call.Item["error"] = McpCallError(notRun, "mcp_protocol_error", GatewayMcpErrorCode);
                    call.Item["status"] = "incomplete";
                }

                loop.Output.Add(call.Item);
                continue;
            }

            loop.Output.Add(item.DeepClone());
        }
    }

    /// <summary>
    /// The combined response: the last turn's object with every turn's items, summed usage, the caller's tools, and the id
    /// to continue from. When calls are still owed and <c>store</c> is on, the id is a gateway id whose state is stored.
    /// </summary>
    private async Task WriteMcpResponseAsync(HttpContext http, RequestState state, McpLoop loop, ResponsesMcpStream? stream, JsonObject response, string? stopReason,
        string? providerStatus, JsonNode? providerIncomplete, List<LoopCall> owed)
    {
        var mcp = loop.Request;
        var key = state.Key!;
        var deployment = state.Deployment!;
        var id = Str(response["id"]) ?? string.Empty;
        var owedCalls = owed.Select(c => c.Call).ToList();
        var approvals = owedCalls.Where(c => c.ApprovalId is not null).ToList();
        var parent = state.ParentRequestId ?? state.RequestId;
        if (owedCalls.Count > 0)
        {
            string? pendingId = null;
            McpPendingLoop? pending = null;
            if (mcp.Store)
            {
                pendingId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
                pending = new McpPendingLoop(id, deployment.Id, parent, owedCalls);
                id = ResponsesMcpTool.GatewayResponsePrefix + pendingId;
            }

            await mcpStates.SaveAsync(key.Id, pendingId, pending, approvals, parent, CancellationToken.None);
        }

        response["id"] = id;
        response["output"] = loop.Output;
        response["usage"] = new JsonObject
        {
            ["input_tokens"] = loop.InputTokens,
            ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = loop.CachedTokens },
            ["output_tokens"] = loop.OutputTokens,
            ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = loop.ReasoningTokens },
            ["total_tokens"] = loop.InputTokens + loop.OutputTokens,
        };
        ClientView(response, mcp);
        if (providerStatus is not null)
        {
            response["status"] = providerStatus;
            response["incomplete_details"] = providerIncomplete;
        }
        else if (stopReason is not null)
        {
            response["status"] = "incomplete";
            response["incomplete_details"] = new JsonObject { ["reason"] = stopReason };
        }

        if (!mcp.CallerWantsEncryptedReasoning)
        {
            foreach (var item in loop.Output.OfType<JsonObject>().Where(i => Str(i["type"]) == "reasoning"))
            {
                item.Remove("encrypted_content");
            }
        }

        if (stream is { Started: true })
        {
            using (usageWriter.TrackPending())
            {
                await stream.CompleteAsync(response);
            }

            return;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
        using (usageWriter.TrackPending())
        {
            SetRoutingHeaders(http, deployment, state.Fallbacks);
            http.Response.Headers[GatewayHeaders.CostSek] = loop.CostSek.ToString("0.000000", CultureInfo.InvariantCulture);
            http.Response.Headers[GatewayHeaders.Turns] = loop.Turns.ToString(CultureInfo.InvariantCulture);
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

    /// <summary>The request's own <c>tools</c>, <c>tool_choice</c>, <c>previous_response_id</c> and <c>max_tool_calls</c> in a provider response.</summary>
    private static JsonObject ClientView(JsonObject response, ResponsesMcp mcp)
    {
        Restore(response, "tools", mcp.OriginalTools);
        Restore(response, "tool_choice", mcp.OriginalToolChoice);
        Restore(response, "previous_response_id", mcp.OriginalPreviousResponseId);
        response["max_tool_calls"] = mcp.MaxToolCalls;
        return response;
    }

    /// <summary>
    /// Relays one streamed turn. At the first event of turn 1 the client stream starts (the routing headers are final then):
    /// <c>response.created</c> with the provider's response as the client sees it, then the items the loop has already
    /// (<c>mcp_list_tools</c>, calls answered for a continuation). Null when the provider failed or the stream broke off.
    /// </summary>
    private async Task<JsonObject?> RelayMcpTurnAsync(HttpContext http, RequestState first, McpLoop loop, ResponsesMcpStream stream, ProviderStreamResult result,
        HashSet<string> deferred, CancellationToken ct)
    {
        var mcp = loop.Request;
        try
        {
            return await stream.RelayTurnAsync(result, loop.Output.Count, item => ClassifyStreamed(mcp, item, deferred), async created =>
            {
                if (stream.Started)
                {
                    return;
                }

                loop.Created = (JsonObject?)created?.DeepClone() ?? new JsonObject { ["id"] = string.Empty, ["object"] = "response" };
                var shown = ClientView((JsonObject)loop.Created.DeepClone(), mcp);
                shown["status"] = "in_progress";
                shown["output"] = new JsonArray();
                shown.Remove("usage");
                SetRoutingHeaders(http, first.Deployment!, first.Fallbacks);
                await stream.StartAsync(shown, ct);
                for (var i = 0; i < loop.Output.Count; i++)
                {
                    await stream.WholeItemAsync(i, (JsonObject)loop.Output[i]!, ct);
                }
            }, ct);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException && !ct.IsCancellationRequested)
        {
            LogStreamBroken(logger, first.RequestId, first.Deployment!.ProviderAccount!.Name, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>How a streamed item is shown; calls that may need approval are deferred (their ids collected in <paramref name="deferred"/>).</summary>
    private static (StreamItemKind Kind, JsonObject? Shown) ClassifyStreamed(ResponsesMcp mcp, JsonObject item, HashSet<string> deferred)
    {
        if (Str(item["type"]) != "function_call")
        {
            return (StreamItemKind.Plain, null);
        }

        var name = Str(item["name"]) ?? string.Empty;
        var callId = Str(item["call_id"]) ?? string.Empty;
        if (mcp.Offered.TryGetValue(name, out var offer))
        {
            if (offer.NeedsApproval)
            {
                deferred.Add(callId);
                return (StreamItemKind.Deferred, null);
            }

            return (StreamItemKind.McpCall, ResponsesMcpStream.McpCallShell(callId, offer.Spec.ServerLabel, offer.Entry.Tool.Name, null));
        }

        return (mcp.CallerFunctions.Contains(name) ? StreamItemKind.Plain : StreamItemKind.Hidden, null);
    }

    /// <summary>
    /// After a streamed turn: the deferred items whole (approval requests, calls that will not run), and when the loop
    /// <paramref name="stops"/>, <c>output_item.done</c> for the turn's other MCP calls, which will not run either.
    /// </summary>
    private static async Task EmitTurnCallsAsync(ResponsesMcpStream stream, McpLoop loop, int from, HashSet<string> deferred, bool stops, CancellationToken ct)
    {
        for (var i = from; i < loop.Output.Count; i++)
        {
            var item = (JsonObject)loop.Output[i]!;
            var type = Str(item["type"]);
            if (type == "mcp_approval_request" || (type == "mcp_call" && deferred.Contains(Str(item["id"]) ?? string.Empty)))
            {
                await stream.WholeItemAsync(i, item, ct);
            }
            else if (type == "mcp_call" && stops)
            {
                await stream.ItemDoneAsync(i, item, ct);
            }
        }
    }

    private static JsonObject ListToolsItem(string label, IReadOnlyList<McpToolEntry> tools) => new()
    {
        ["type"] = "mcp_list_tools",
        ["id"] = "mcpl_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12)),
        ["server_label"] = label,
        ["tools"] = new JsonArray([.. tools.Select(e =>
        {
            var tool = new JsonObject { ["name"] = e.Tool.Name };
            var definition = e.Definition.Json;
            if (definition.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String)
            {
                tool["description"] = d.GetString();
            }

            tool["input_schema"] = e.Definition.InputSchema is { } schema ? JsonNode.Parse(schema.GetRawText()) : new JsonObject { ["type"] = "object" };
            if (definition.TryGetProperty("annotations", out var a) && a.ValueKind == JsonValueKind.Object)
            {
                tool["annotations"] = JsonNode.Parse(a.GetRawText());
            }

            return (JsonNode)tool;
        })]),
    };

    /// <summary>A call to a function nobody offered: answered to the model with an error and not shown to the client.</summary>
    private static bool IsUnknown(McpLoopCall call) => call.ServerLabel.Length == 0;

    /// <summary>An <c>mcp_call</c> error in the shape of OpenAI's <c>McpToolCallError</c> (<c>mcp_protocol_error</c> or <c>http_error</c>).</summary>
    private static JsonObject McpCallError(string message, string type, int code) => new() { ["type"] = type, ["code"] = code, ["message"] = message };

    /// <summary>The text of an <c>mcp_call</c> error sent back in the history: a string, or the message or content of an error object.</summary>
    private static string? ErrorText(JsonNode? error) => error switch
    {
        JsonValue v when v.TryGetValue<string>(out var text) => text,
        JsonObject o => Str(o["message"]) ?? (o["content"] is JsonValue c && c.TryGetValue<string>(out var content) ? content : o["content"]?.ToJsonString()),
        _ => null,
    };

    private static JsonObject McpCallItem(McpLoopCall call)
    {
        var item = new JsonObject
        {
            ["type"] = "mcp_call", ["id"] = call.CallId, ["server_label"] = call.ServerLabel, ["name"] = call.Tool, ["arguments"] = call.Arguments,
            ["output"] = null, ["error"] = null, ["status"] = "calling",
        };
        if (call.ApprovalId is not null)
        {
            item["approval_request_id"] = call.ApprovalId;
        }

        return item;
    }

    private static JsonObject FunctionCall(string callId, string name, string arguments) =>
        new() { ["type"] = "function_call", ["call_id"] = callId, ["name"] = name, ["arguments"] = arguments };

    private static JsonObject FunctionCallOutput(string callId, JsonNode output) =>
        new() { ["type"] = "function_call_output", ["call_id"] = callId, ["output"] = output };

    /// <summary>Puts a call's output right after its <c>function_call</c>.</summary>
    private static void InsertOutput(JsonArray input, JsonObject output, string callId)
    {
        for (var i = input.Count - 1; i >= 0; i--)
        {
            if (input[i] is JsonObject item && Str(item["type"]) == "function_call" && Str(item["call_id"]) == callId)
            {
                input.Insert(i + 1, output);
                return;
            }
        }

        input.Add(output);
    }

    private static void RemoveItems(JsonArray input, Func<JsonObject, bool> match)
    {
        for (var i = input.Count - 1; i >= 0; i--)
        {
            if (input[i] is JsonObject item && match(item))
            {
                input.RemoveAt(i);
            }
        }
    }

    private static void Restore(JsonObject response, string name, JsonNode? original)
    {
        if (original is null)
        {
            response.Remove(name);
        }
        else
        {
            response[name] = original.DeepClone();
        }
    }

    private static void AddUsage(McpLoop loop, JsonNode? usage)
    {
        static long Read(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
        loop.InputTokens += Read(usage?["input_tokens"]);
        loop.CachedTokens += Read(usage?["input_tokens_details"]?["cached_tokens"]);
        loop.OutputTokens += Read(usage?["output_tokens"]);
        loop.ReasoningTokens += Read(usage?["output_tokens_details"]?["reasoning_tokens"]);
    }

    private static JsonObject? ParseResponse(ReadOnlyMemory<byte> body)
    {
        try
        {
            return JsonNode.Parse(body.Span, documentOptions: JsonOptions) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly string[] PiiStrength = ["detected", "redacted", "rerouted-onprem", "blocked"];

    private static string? StrongerPii(string? a, string? b) =>
        a is null ? b : b is null ? a : Array.IndexOf(PiiStrength, a) >= Array.IndexOf(PiiStrength, b) ? a : b;

    private static string NewRequestId() => "req_" + Guid.CreateVersion7().ToString("N");

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
