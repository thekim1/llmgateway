using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Mcp;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>
/// The MCP gateway: Streamable HTTP <c>POST /mcp</c> (every server the key is granted, tools and prompts named
/// <see cref="McpToolNames.Exposed"/>, resource URIs <see cref="McpResourceUris"/>) and <c>POST /mcp/{slug}</c> (one
/// server, original names). Stateless for every client: <c>2026-07-28</c> clients send their version in each request's
/// <c>_meta</c>, handshake-era clients are answered <c>initialize</c> and <c>ping</c> without a session, and any instance
/// serves any request. Tool discovery is answered from the catalogue; resource and prompt lists are forwarded. Only
/// <c>tools/call</c>, <c>resources/read</c> and <c>prompts/get</c> are accounted, each exactly once. Resources and prompts
/// are in <c>GatewayRequestHandler.McpResources.cs</c>, <c>subscriptions/listen</c> in <c>GatewayRequestHandler.McpListen.cs</c>.
/// </summary>
public sealed partial class GatewayRequestHandler
{
    /// <summary>The protocol revision of stateless clients (per-request <c>_meta</c>).</summary>
    public const string McpStatelessVersion = "2026-07-28";

    /// <summary>Revisions of clients that use the <c>initialize</c> handshake, newest first.</summary>
    public static readonly IReadOnlyList<string> McpHandshakeVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    private const string McpVersionMeta = "io.modelcontextprotocol/protocolVersion";
    private const string McpProtocolVersionHeader = "MCP-Protocol-Version";

    /// <summary>The aggregated server instructions are cut here.</summary>
    private const int McpMaxInstructions = 2 * McpServer.MaxInstructionsLength;

    /// <summary>A server's own JSON-RPC error message is cut here.</summary>
    private const int McpMaxErrorMessage = 500;

    private static readonly ActivitySource McpActivities = new(Extensions.TelemetryName);

    /// <summary>JSON-RPC error codes of the gateway's own refusals (the implementation-defined range).</summary>
    private static class McpErrors
    {
        public const int RateLimited = -32000;
        public const int BudgetExceeded = -32001;
        public const int PiiRefused = -32002;
        public const int UpstreamFailed = -32003;
        public const int ResultTooLarge = -32004;
        public const int UnsupportedProtocolVersion = -32022;
    }

    /// <summary>What an accounted MCP request is checked and scanned as.</summary>
    private enum McpResultKind
    {
        Tool = 0,
        Resource = 1,
        Prompt = 2,
    }

    /// <summary>
    /// An accounted request to one server, after resolution and authorisation. <c>Params</c> is what is sent upstream;
    /// <c>Arguments</c> is part of it and is PII-scanned (and redacted) in place. <c>Rewrite</c> is applied to the result
    /// before it is sent (resource URIs in the aggregated view).
    /// </summary>
    private sealed record McpForward(McpServer Server, string Method, JsonObject Params, JsonObject? Arguments, bool MayRetry, decimal Price,
        McpResultKind Kind, string SpanName, string? ToolName, Action<JsonNode>? Rewrite = null);

    public async Task HandleMcpAsync(HttpContext http, string? slug)
    {
        ArgumentNullException.ThrowIfNull(http);
        var ct = http.RequestAborted;
        var key = await AuthenticateAsync(http, GatewayEndpoint.Mcp, time.GetUtcNow(), ct);
        if (key is null)
        {
            return;
        }

        if (!http.Request.HasJsonContentType())
        {
            await GatewayErrors.WriteAsync(http, GatewayEndpoint.Mcp, 415, GatewayErrorCodes.UnsupportedMediaType, "Content-Type måste vara application/json.", options.CurrentValue.DocsUrl);
            return;
        }

        using var response = new McpResponse(http);
        var (body, _, status) = await ReadJsonAsync(http.Request, ct);
        if (status == 413)
        {
            await GatewayErrors.WriteAsync(http, GatewayEndpoint.Mcp, 413, GatewayErrorCodes.RequestTooLarge, "Förfrågan är för stor.", options.CurrentValue.DocsUrl);
            return;
        }

        if (status != 0)
        {
            await response.ErrorAsync(null, (int)ModelContextProtocol.McpErrorCode.ParseError, "Parse error", httpStatus: 400);
            return;
        }

        var snapshot = await catalog.GetAsync(ct);
        McpServer? only = null;
        if (slug is not null && (!snapshot.McpServers.TryGetValue(slug, out only)
            || McpAccess.CheckServer(key, only, snapshot.McpGrants(key.Id).GetValueOrDefault(only.Id), circuitOpen: false) != McpDenial.None))
        {
            await GatewayErrors.WriteAsync(http, GatewayEndpoint.Mcp, 404, GatewayErrorCodes.McpServerNotFound,
                $"MCP-servern '{slug}' finns inte eller får inte användas med nyckeln.", options.CurrentValue.DocsUrl);
            return;
        }

        if (body is null || body["jsonrpc"]?.GetValueKind() != JsonValueKind.String)
        {
            await response.ErrorAsync(null, (int)ModelContextProtocol.McpErrorCode.InvalidRequest, "Invalid Request", httpStatus: 400);
            return;
        }

        var id = body["id"];
        if (body["method"] is not JsonValue methodValue || !methodValue.TryGetValue<string>(out var method))
        {
            // A client's response or something else that is not a request: nothing to answer.
            http.Response.StatusCode = body.ContainsKey("result") || body.ContainsKey("error") ? 202 : 400;
            return;
        }

        if (id is null)
        {
            // Notifications, including notifications/cancelled: closing the request's stream is what cancels it here.
            http.Response.StatusCode = 202;
            return;
        }

        var parameters = body["params"] as JsonObject;
        var requested = parameters?["_meta"]?[McpVersionMeta] is JsonValue v && v.TryGetValue<string>(out var metaVersion) ? metaVersion : null;
        var header = http.Request.Headers[McpProtocolVersionHeader].ToString();
        if ((requested is not null && requested != McpStatelessVersion)
            || (header.Length > 0 && header != McpStatelessVersion && !McpHandshakeVersions.Contains(header)))
        {
            await response.ErrorAsync(id, McpErrors.UnsupportedProtocolVersion, "Unsupported protocol version", httpStatus: 400,
                data: new JsonObject
                {
                    ["supported"] = requested is not null ? new JsonArray(McpStatelessVersion) : new JsonArray([.. McpHandshakeVersions.Prepend(McpStatelessVersion).Select(v => JsonValue.Create(v))]),
                    ["requested"] = requested ?? header,
                });
            return;
        }

        var stateless = requested is not null;
        switch (method)
        {
            case "initialize":
                await response.ResultAsync(id, w => WriteInitializeResult(w, snapshot, key, only, parameters), ct);
                return;
            case "server/discover":
                await response.ResultAsync(id, w => WriteDiscoverResult(w, snapshot, key, only), ct);
                return;
            case "ping":
                await response.ResultAsync(id, w => { w.WriteStartObject(); w.WriteEndObject(); }, ct);
                return;
            case "tools/list":
                var tools = await VisibleToolsAsync(snapshot, key, only, ct);
                await response.ResultAsync(id, w => WriteToolList(w, tools, only is null, stateless), ct);
                return;
            case "tools/call":
                await CallToolAsync(response, snapshot, key, only, id, parameters, ct);
                return;
            case "resources/list":
                await ListForwardedAsync(response, snapshot, key, only, id, parameters, McpList.Resources, stateless, ct);
                return;
            case "resources/templates/list":
                await ListForwardedAsync(response, snapshot, key, only, id, parameters, McpList.ResourceTemplates, stateless, ct);
                return;
            case "prompts/list":
                await ListForwardedAsync(response, snapshot, key, only, id, parameters, McpList.Prompts, stateless, ct);
                return;
            case "resources/read":
                await ReadResourceAsync(response, snapshot, key, only, id, parameters, ct);
                return;
            case "prompts/get":
                await GetPromptAsync(response, snapshot, key, only, id, parameters, ct);
                return;
            case "subscriptions/listen" when stateless:
                await ListenAsync(response, key, KeyAuthenticator.ExtractKey(http.Request)!, only, id, parameters, ct);
                return;
            default:
                await response.ErrorAsync(id, (int)ModelContextProtocol.McpErrorCode.MethodNotFound, $"Method not found: '{Truncate(method, 100)}'");
                return;
        }
    }

    private async Task CallToolAsync(McpResponse response, CatalogSnapshot snapshot, VirtualKey key, McpServer? only, JsonNode id, JsonObject? parameters, CancellationToken ct)
    {
        var state = new RequestState(RequestIdMiddleware.Get(response.Http), GatewayEndpoint.Mcp, time.GetTimestamp()) { Key = key, McpMethod = "tools/call" };
        var name = parameters?["name"] is JsonValue n && n.TryGetValue<string>(out var given) ? given : null;
        state.RequestedModel = McpUsageName(only?.Slug, name ?? string.Empty);
        state.McpTool = name is null ? null : Truncate(name, 128);

        // Resolve and authorise: every refusal looks like an unknown tool, the reason goes to the security log.
        McpToolEntry? entry = null;
        if (name is not null)
        {
            entry = only is null ? snapshot.McpTools.GetValueOrDefault(name) : snapshot.McpServerTools(only.Id).GetValueOrDefault(name);
        }

        var denial = entry is null ? (McpDenial?)null
            : McpAccess.Check(key, entry.Server, snapshot.McpGrants(key.Id).GetValueOrDefault(entry.Server.Id), entry.Tool, await IsMcpCircuitOpenAsync(entry.Server.Id, ct));
        if (entry is null || denial != McpDenial.None)
        {
            state.McpServerId = entry?.Server.Id;
            LogMcpDenied(state, state.McpTool ?? "-", denial, entry?.Server);
            await RefuseAsync(response, state, id, (int)ModelContextProtocol.McpErrorCode.InvalidParams, $"Unknown tool: '{Truncate(name ?? string.Empty, 128)}'",
                404, RequestOutcome.Rejected, GatewayErrorCodes.McpToolNotFound);
            return;
        }

        var server = entry.Server;
        state.McpServerId = server.Id;
        state.McpTool = entry.Tool.Name;
        state.RequestedModel = McpUsageName(server.Slug, entry.Tool.Name);

        // Arguments against the approved input schema.
        var arguments = parameters!["arguments"];
        if (arguments is not null and not JsonObject)
        {
            await RefuseAsync(response, state, id, (int)ModelContextProtocol.McpErrorCode.InvalidParams, "Invalid params: 'arguments' must be an object",
                400, RequestOutcome.Rejected, GatewayErrorCodes.McpInvalidArguments);
            return;
        }

        var args = (JsonObject?)arguments ?? [];
        if (entry.Definition.InputSchema is { } schema && McpSchemaValidator.Validate(schema, args) is { } invalid)
        {
            await RefuseAsync(response, state, id, (int)ModelContextProtocol.McpErrorCode.InvalidParams, $"Invalid arguments for tool '{entry.Tool.Name}': {invalid}",
                400, RequestOutcome.Rejected, GatewayErrorCodes.McpInvalidArguments);
            return;
        }

        parameters.Remove("arguments");
        var forward = new McpForward(server, "tools/call", new JsonObject { ["name"] = entry.Tool.Name, ["arguments"] = args }, args, entry.Definition.MayRetry,
            entry.Tool.PriceSek is > 0 ? entry.Tool.PriceSek.Value : 0m, McpResultKind.Tool, "tools/call " + entry.Tool.Name, entry.Tool.Name,
            only is null ? result => NamespaceBlockUris(result["content"] as JsonArray ?? [], server.Slug) : null);
        await RunAccountedAsync(response, state, snapshot, id, parameters, forward, ct);
    }

    /// <summary>
    /// The shared steps of an accounted request: rate limit, PII guard on the arguments, budget, then the upstream call.
    /// Every path accounts exactly once.
    /// </summary>
    private async Task RunAccountedAsync(McpResponse response, RequestState state, CatalogSnapshot snapshot, JsonNode id, JsonObject? requestParams,
        McpForward forward, CancellationToken ct)
    {
        var key = state.Key!;
        var server = forward.Server;
        var rate = await rateLimiter.AcquireMcpCallAsync(key.Id, key.McpCallsPerMinute, ct);
        if (!rate.Allowed)
        {
            var retryAfter = Math.Max(1, (int)Math.Ceiling(rate.RetryAfter.TotalSeconds));
            response.SetHeader("Retry-After", retryAfter.ToString(CultureInfo.InvariantCulture));
            await RefuseAsync(response, state, id, McpErrors.RateLimited, $"Gränsen för MCP-anrop per minut är nådd för nyckeln. Försök igen om {retryAfter} s.",
                429, RequestOutcome.RateLimited, GatewayErrorCodes.RateLimited, new JsonObject { ["code"] = GatewayErrorCodes.RateLimited, ["retryAfterSeconds"] = retryAfter });
            return;
        }

        PiiScanResult? argumentScan = null;
        if (key.PiiPolicy != PiiPolicy.Off && forward.Arguments is { } args)
        {
            argumentScan = McpPii.ScanArguments(args, redact: key.PiiPolicy == PiiPolicy.Redact);
            state.PiiCategories = argumentScan.Summary;
            if (argumentScan.HasPii)
            {
                state.PiiAction = key.PiiPolicy;
                var decision = PiiJsonScanner.Decide(key.PiiPolicy, argumentScan);
                response.SetHeader(GatewayHeaders.Pii, decision switch
                {
                    PiiDecision.ForwardRedacted => "redacted",
                    PiiDecision.Block => "blocked",
                    PiiDecision.ForwardOnPremOnly => server.Residency == DataResidency.OnPrem ? "rerouted-onprem" : "blocked",
                    _ => "detected",
                });
                if (decision == PiiDecision.Block)
                {
                    await RefuseAsync(response, state, id, McpErrors.PiiRefused,
                        $"Argumenten innehåller personuppgifter ({argumentScan.Summary}) och nyckelns policy tillåter inte det. Ta bort uppgifterna och försök igen.",
                        400, RequestOutcome.PiiBlocked, GatewayErrorCodes.PiiBlocked, new JsonObject { ["code"] = GatewayErrorCodes.PiiBlocked });
                    return;
                }

                if (decision == PiiDecision.ForwardOnPremOnly && server.Residency != DataResidency.OnPrem)
                {
                    await RefuseAsync(response, state, id, McpErrors.PiiRefused,
                        $"Argumenten innehåller personuppgifter ({argumentScan.Summary}); nyckeln får då bara använda MCP-servrar som körs on-prem.",
                        400, RequestOutcome.PiiBlocked, GatewayErrorCodes.PiiBlocked, new JsonObject { ["code"] = GatewayErrorCodes.PiiBlocked });
                    return;
                }
            }
        }

        if (forward.Price > 0)
        {
            var reservation = await budgets.ReserveAsync(BudgetService.ApplicableBudgets(snapshot, key), forward.Price, ct);
            state.Reservation = reservation;
            if (!reservation.Allowed)
            {
                await RefuseAsync(response, state, id, McpErrors.BudgetExceeded, BudgetExceededMessage(reservation.Exceeded!.Budget, "Kontakta budgetansvarig."),
                    402, RequestOutcome.BudgetExceeded, GatewayErrorCodes.BudgetExceeded, new JsonObject { ["code"] = GatewayErrorCodes.BudgetExceeded });
                return;
            }
        }

        // From here on the reservation must be reconciled whatever happens.
        try
        {
            await SendAccountedAsync(response, state, snapshot, id, forward, argumentScan, ProgressRelay(response, requestParams, ct), ct);
        }
        catch (Exception ex) when (!state.Accounted)
        {
            if (!await AccountUnexpectedAsync(response.Http, state, snapshot, ex))
            {
                throw;
            }
        }
    }

    private async Task SendAccountedAsync(McpResponse response, RequestState state, CatalogSnapshot snapshot, JsonNode id, McpForward forward,
        PiiScanResult? argumentScan, Func<JsonObject, ValueTask>? progress, CancellationToken ct)
    {
        var server = forward.Server;
        if (UpstreamCall(response.Http, snapshot, server, forward.Method, forward.Params, forward.MayRetry, progress) is not { } call)
        {
            await RefuseAsync(response, state, id, McpErrors.UpstreamFailed, $"MCP-servern '{server.Slug}' kan inte nås just nu.",
                502, RequestOutcome.ProviderError, GatewayErrorCodes.McpUpstreamFailed, new JsonObject { ["code"] = GatewayErrorCodes.McpUpstreamFailed });
            return;
        }

        using var activity = McpActivities.StartActivity(forward.SpanName, ActivityKind.Client);
        activity?.SetTag("mcp.method.name", forward.Method);
        if (forward.ToolName is not null && forward.Kind == McpResultKind.Tool)
        {
            activity?.SetTag("gen_ai.tool.name", forward.ToolName);
            activity?.SetTag("gen_ai.operation.name", "execute_tool");
        }
        else if (forward.ToolName is not null)
        {
            activity?.SetTag("gen_ai.prompt.name", forward.ToolName);
        }

        activity?.SetTag("ume.mcp.server", server.Slug);
        activity?.SetTag("ume.request_id", state.RequestId);

        var started = time.GetTimestamp();
        McpUpstreamResult result;
        try
        {
            result = await mcp.SendAsync(call, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            metrics.McpCall(server.Slug, forward.Method, "cancelled", time.GetElapsedTime(started).TotalMilliseconds);
            await AccountAsync(state, null, default, 499, RequestOutcome.ClientCancelled, null, snapshot);
            return;
        }

        activity?.SetTag("ume.mcp.attempts", result.Attempts);
        metrics.McpCall(server.Slug, forward.Method, OutcomeName(result), time.GetElapsedTime(started).TotalMilliseconds);
        await RecordMcpCircuitAsync(state.RequestId, server, result);
        if (!result.Succeeded)
        {
            activity?.SetStatus(ActivityStatusCode.Error, result.Failure.ToString());
            LogMcpUpstreamFailed(logger, state.RequestId, server.Slug, result.Failure.ToString(), result.HttpStatus ?? 0, result.Attempts);
            var (code, message, status, error) = result.Failure switch
            {
                McpUpstreamFailure.JsonRpcError => (result.ErrorCode, UpstreamErrorMessage(response, state, result.ErrorMessage, argumentScan), 400, GatewayErrorCodes.ProviderRejected),
                McpUpstreamFailure.TooLarge => (McpErrors.ResultTooLarge,
                    $"Svaret från MCP-servern är större än tillåtet ({server.MaxResultBytes.ToString(CultureInfo.InvariantCulture)} byte).", 502, GatewayErrorCodes.McpResultTooLarge),
                McpUpstreamFailure.Timeout => (McpErrors.UpstreamFailed, $"MCP-servern '{server.Slug}' svarade inte i tid.", 504, GatewayErrorCodes.McpUpstreamFailed),
                _ => (McpErrors.UpstreamFailed, $"MCP-servern '{server.Slug}' kan inte nås just nu.", 502, GatewayErrorCodes.McpUpstreamFailed),
            };
            await RefuseAsync(response, state, id, code, message, status, RequestOutcome.ProviderError, error,
                result.Failure == McpUpstreamFailure.JsonRpcError ? null : new JsonObject { ["code"] = error });
            return;
        }

        var cost = forward.Price > 0 ? new Cost(snapshot.SekPerUsd > 0 ? forward.Price / snapshot.SekPerUsd : 0, forward.Price) : default;

        // The PII guard on the result: it reaches the client and, on the LLM path, a model.
        var answer = result.Result;
        var policy = state.Key!.PiiPolicy;
        if (policy != PiiPolicy.Off)
        {
            var redact = policy == PiiPolicy.Redact;
            var resultScan = forward.Kind switch
            {
                McpResultKind.Resource => McpPii.ScanResourceContents(answer, redact),
                McpResultKind.Prompt => McpPii.ScanPrompt(answer, redact),
                _ => McpPii.ScanResult(answer, redact),
            };
            if (resultScan.HasPii)
            {
                state.PiiCategories = (argumentScan is null ? resultScan : PiiJsonScanner.Merge(argumentScan, resultScan)).Summary;
                state.PiiAction = policy;
                if (policy == PiiPolicy.Block)
                {
                    response.SetHeader(GatewayHeaders.Pii, "blocked");
                    await response.ErrorAsync(id, McpErrors.PiiRefused,
                        $"Svaret från MCP-servern innehåller personuppgifter ({resultScan.Summary}) och nyckelns policy tillåter inte det.",
                        data: new JsonObject { ["code"] = GatewayErrorCodes.PiiBlocked }, ct: CancellationToken.None);
                    await AccountAsync(state, null, default, 200, RequestOutcome.PiiBlocked, GatewayErrorCodes.PiiBlocked, snapshot, cost);
                    return;
                }

                response.SetHeader(GatewayHeaders.Pii, redact ? "redacted" : "detected");
            }
        }

        if (answer is not null)
        {
            forward.Rewrite?.Invoke(answer);
        }

        using (usageWriter.TrackPending())
        {
            if (forward.Price > 0)
            {
                response.SetHeader(GatewayHeaders.CostSek, forward.Price.ToString("0.000000", CultureInfo.InvariantCulture));
            }

            await response.ResultAsync(id, w => WriteNode(w, answer), CancellationToken.None);
            await AccountAsync(state, null, default, 200, RequestOutcome.Success, null, snapshot, cost);
        }
    }

    /// <summary>
    /// The upstream request for <paramref name="server"/>, with its credential and the caller headers its allow-list names.
    /// Null when the credential cannot be used (logged).
    /// </summary>
    private McpUpstreamCall? UpstreamCall(HttpContext http, CatalogSnapshot snapshot, McpServer server, string method, JsonObject parameters, bool mayRetry,
        Func<JsonObject, ValueTask>? progress = null)
    {
        McpUpstreamAuth auth;
        try
        {
            auth = snapshot.McpCredentials.Get(server, credentials);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            LogMcpCredentialUnavailable(logger, server.Slug, ex.GetType().Name);
            return null;
        }

        var forwarded = new List<KeyValuePair<string, string>>();
        foreach (var name in server.ForwardHeaders)
        {
            if (McpUpstream.IsForwardable(name) && http.Request.Headers.TryGetValue(name, out var value) && value.Count > 0)
            {
                forwarded.Add(new(name, value.ToString()));
            }
        }

        return new McpUpstreamCall(server, auth, method, parameters, forwarded, mayRetry, progress);
    }

    /// <summary>
    /// When the client asked for progress (<c>_meta.progressToken</c>), relays the server's progress notifications with the
    /// client's token; the answer then becomes an SSE stream.
    /// </summary>
    private static Func<JsonObject, ValueTask>? ProgressRelay(McpResponse response, JsonObject? requestParams, CancellationToken ct)
    {
        if (requestParams?["_meta"]?["progressToken"] is not JsonValue token
            || token.GetValueKind() is not (JsonValueKind.String or JsonValueKind.Number))
        {
            return null;
        }

        var callerToken = token.DeepClone();
        var last = double.NegativeInfinity;
        var gate = new Lock();
        return async notification =>
        {
            // The upstream client may hand over notifications out of order; progress must only ever increase.
            if (notification["progress"] is not JsonValue p || !p.TryGetValue<double>(out var value))
            {
                return;
            }

            lock (gate)
            {
                if (value <= last)
                {
                    return;
                }

                last = value;
            }

            notification["progressToken"] = callerToken.DeepClone();
            notification.Remove("_meta");
            try
            {
                await response.NotifyAsync("notifications/progress", notification, ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
            {
                // The client left; the call itself is cancelled through the request's token.
            }
        };
    }

    private static string OutcomeName(McpUpstreamResult result) =>
        result.Failure == McpUpstreamFailure.None ? "success" : result.Failure.ToString().ToLowerInvariant();

    private static void WriteNode(Utf8JsonWriter w, JsonNode? node)
    {
        if (node is null)
        {
            w.WriteNullValue();
        }
        else
        {
            node.WriteTo(w);
        }
    }

    /// <summary>
    /// The server's own JSON-RPC error message, passed on to the client: cut to <see cref="McpMaxErrorMessage"/>
    /// characters and put through the key's PII policy like a result text.
    /// </summary>
    private static string UpstreamErrorMessage(McpResponse response, RequestState state, string? message, PiiScanResult? argumentScan)
    {
        var text = Truncate(string.IsNullOrWhiteSpace(message) ? "Error" : message, McpMaxErrorMessage);
        var policy = state.Key!.PiiPolicy;
        if (policy == PiiPolicy.Off)
        {
            return text;
        }

        var holder = new JsonObject { ["message"] = text };
        var scan = PiiJsonScanner.ScanValues(holder, redact: policy == PiiPolicy.Redact);
        if (!scan.HasPii)
        {
            return text;
        }

        state.PiiAction = policy;
        state.PiiCategories = (argumentScan is null ? scan : PiiJsonScanner.Merge(argumentScan, scan)).Summary;
        response.SetHeader(GatewayHeaders.Pii, policy switch
        {
            PiiPolicy.Redact => "redacted",
            PiiPolicy.Block => "blocked",
            _ => "detected",
        });
        return policy == PiiPolicy.Block
            ? "MCP-servern svarade med ett fel vars text innehöll personuppgifter; texten har tagits bort."
            : holder["message"]!.GetValue<string>();
    }

    /// <summary>
    /// What an MCP call's usage record holds in <c>RequestedModel</c>: <c>mcp:{slug}/{name}</c> (the server's name for the
    /// tool or prompt; just <c>mcp:{slug}</c> for a resource read), so reports that group by model show MCP calls apart from
    /// models. For a name that matched nothing, the name as the client sent it.
    /// </summary>
    private static string McpUsageName(string? slug, string name) =>
        ModelNames.Truncate(slug is null ? "mcp:" + name : name.Length == 0 ? "mcp:" + slug : $"mcp:{slug}/{name}");

    /// <summary>Answers a refused accounted request with a JSON-RPC error and accounts it.</summary>
    private async Task RefuseAsync(McpResponse response, RequestState state, JsonNode id, int code, string message, int recordedStatus, RequestOutcome outcome,
        string errorCode, JsonObject? data = null)
    {
        await response.ErrorAsync(id, code, message, data);
        await AccountAsync(state, null, default, recordedStatus, outcome, errorCode, null);
    }

    private void LogMcpDenied(RequestState state, string name, McpDenial? denial, McpServer? server)
    {
        if (_security.IsEnabled(LogLevel.Information))
        {
            var key = state.Key!;
            SecurityEvents.McpToolDenied(_security, name, state.RequestId, denial is { } d ? McpAccess.ReasonName(d) : "unknown_tool",
                server?.Slug, key.Id, key.Prefix, key.TeamId, key.Team?.DepartmentId ?? Guid.Empty);
        }
    }

    /// <summary>The servers the key may use (granted, enabled, residency, circuit closed) and their tools the grant allows.</summary>
    private async Task<IReadOnlyList<McpToolEntry>> VisibleToolsAsync(CatalogSnapshot snapshot, VirtualKey key, McpServer? only, CancellationToken ct)
    {
        var servers = VisibleServers(snapshot, key, only);
        if (servers.Count == 0)
        {
            return [];
        }

        var open = await OpenMcpCircuitsAsync([.. servers.Select(s => s.Server.Id)], ct);
        return VisibleTools(snapshot, key, servers, open);
    }

    private static List<McpToolEntry> VisibleTools(CatalogSnapshot snapshot, VirtualKey key, List<(McpServer Server, VirtualKeyMcpGrant Grant)> servers,
        IReadOnlySet<Guid> openCircuits)
    {
        var tools = new List<McpToolEntry>();
        foreach (var (server, grant) in servers)
        {
            var circuitOpen = openCircuits.Contains(server.Id);
            foreach (var entry in snapshot.McpServerTools(server.Id).Values)
            {
                if (McpAccess.Check(key, server, grant, entry.Tool, circuitOpen) == McpDenial.None)
                {
                    tools.Add(entry);
                }
            }
        }

        return tools;
    }

    private static List<(McpServer Server, VirtualKeyMcpGrant Grant)> VisibleServers(CatalogSnapshot snapshot, VirtualKey key, McpServer? only)
    {
        var grants = snapshot.McpGrants(key.Id);
        var servers = new List<(McpServer, VirtualKeyMcpGrant)>();
        foreach (var grant in grants.Values)
        {
            if (snapshot.McpServerById(grant.McpServerId) is { } server && (only is null || only.Id == server.Id)
                && McpAccess.CheckServer(key, server, grant, circuitOpen: false) == McpDenial.None)
            {
                servers.Add((server, grant));
            }
        }

        servers.Sort((a, b) => string.CompareOrdinal(a.Item1.Slug, b.Item1.Slug));
        return servers;
    }

    private async Task<bool> IsMcpCircuitOpenAsync(Guid serverId, CancellationToken ct) => (await OpenMcpCircuitsAsync([serverId], ct)).Contains(serverId);

    /// <summary>Servers whose circuit is open. The circuit store being unavailable counts as closed, as for providers.</summary>
    private async Task<IReadOnlySet<Guid>> OpenMcpCircuitsAsync(IReadOnlyCollection<Guid> serverIds, CancellationToken ct)
    {
        if (serverIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        try
        {
            return await circuits.GetOpenAsync(serverIds, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCircuitStoreFailed(logger, ex.GetType().Name, "-");
            return new HashSet<Guid>();
        }
    }

    private async Task RecordMcpCircuitAsync(string requestId, McpServer server, McpUpstreamResult result)
    {
        try
        {
            if (result.CountsAgainstCircuit)
            {
                await circuits.RecordFailureAsync(server.Id, CancellationToken.None);
            }
            else if (result.Succeeded)
            {
                await circuits.RecordSuccessAsync(server.Id, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCircuitStoreFailed(logger, ex.GetType().Name, requestId);
        }
    }

    private static void WriteInitializeResult(Utf8JsonWriter w, CatalogSnapshot snapshot, VirtualKey key, McpServer? only, JsonObject? parameters)
    {
        var requested = parameters?["protocolVersion"] is JsonValue v && v.TryGetValue<string>(out var version) ? version : null;
        w.WriteStartObject();
        w.WriteString("protocolVersion", requested is not null && McpHandshakeVersions.Contains(requested) ? requested : McpHandshakeVersions[0]);
        WriteCapabilities(w, snapshot, key, only, listChanged: false);
        w.WritePropertyName("serverInfo");
        WriteServerInfo(w);
        WriteInstructions(w, snapshot, key, only);
        w.WriteEndObject();
    }

    private void WriteDiscoverResult(Utf8JsonWriter w, CatalogSnapshot snapshot, VirtualKey key, McpServer? only)
    {
        w.WriteStartObject();
        w.WriteStartArray("supportedVersions");
        w.WriteStringValue(McpStatelessVersion);
        w.WriteEndArray();
        WriteCapabilities(w, snapshot, key, only, listChanged: true);
        WriteInstructions(w, snapshot, key, only);
        WriteCacheHints(w, options.CurrentValue.CatalogCacheSeconds * 1000L);
        w.WriteStartObject("_meta");
        w.WritePropertyName("io.modelcontextprotocol/serverInfo");
        WriteServerInfo(w);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private void WriteToolList(Utf8JsonWriter w, IReadOnlyList<McpToolEntry> tools, bool aggregated, bool stateless)
    {
        w.WriteStartObject();
        w.WriteStartArray("tools");
        foreach (var entry in tools.OrderBy(t => aggregated ? t.Tool.ExposedName : t.Tool.Name, StringComparer.Ordinal))
        {
            w.WriteStartObject();
            w.WriteString("name", aggregated ? entry.Tool.ExposedName : entry.Tool.Name);
            if (entry.Tool.Title is { } title)
            {
                w.WriteString("title", title);
            }

            foreach (var property in entry.Definition.Json.EnumerateObject())
            {
                if (property.NameEquals("name") || (entry.Tool.Title is not null && property.NameEquals("title")))
                {
                    continue;
                }

                property.WriteTo(w);
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
        if (stateless)
        {
            WriteCacheHints(w, options.CurrentValue.CatalogCacheSeconds * 1000L);
        }

        w.WriteEndObject();
    }

    /// <summary>
    /// Tools always; resources and prompts when the key may use them on at least one server. <c>listChanged</c> only where
    /// the gateway can tell the client: on <c>subscriptions/listen</c>, which handshake-era clients do not have.
    /// </summary>
    private static void WriteCapabilities(Utf8JsonWriter w, CatalogSnapshot snapshot, VirtualKey key, McpServer? only, bool listChanged)
    {
        var servers = VisibleServers(snapshot, key, only);
        w.WriteStartObject("capabilities");
        w.WriteStartObject("tools");
        w.WriteBoolean("listChanged", listChanged);
        w.WriteEndObject();
        if (servers.Any(s => McpAccess.CheckResources(key, s.Server, s.Grant, circuitOpen: false) == McpDenial.None))
        {
            w.WriteStartObject("resources");
            w.WriteBoolean("listChanged", listChanged);
            w.WriteEndObject();
        }

        if (servers.Any(s => McpAccess.CheckPrompts(key, s.Server, s.Grant, circuitOpen: false) == McpDenial.None))
        {
            w.WriteStartObject("prompts");
            w.WriteBoolean("listChanged", listChanged);
            w.WriteEndObject();
        }

        w.WriteEndObject();
    }

    private static void WriteServerInfo(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("name", "ume-llm-gateway");
        w.WriteString("title", "Umeå kommun LLM Gateway");
        w.WriteString("version", Infrastructure.Providers.ProviderTransport.UserAgent.Product!.Version);
        w.WriteEndObject();
    }

    /// <summary>What a client may cache: lists depend on the key's grants, so shared caches must not keep them.</summary>
    private static void WriteCacheHints(Utf8JsonWriter w, long ttlMs)
    {
        w.WriteNumber("ttlMs", ttlMs);
        w.WriteString("cacheScope", "private");
        w.WriteString("resultType", "complete");
    }

    /// <summary>The instructions of the servers the key may use, each under its name, capped in total.</summary>
    private static void WriteInstructions(Utf8JsonWriter w, CatalogSnapshot snapshot, VirtualKey key, McpServer? only)
    {
        var text = new StringBuilder();
        foreach (var (server, _) in VisibleServers(snapshot, key, only))
        {
            if (string.IsNullOrWhiteSpace(server.Instructions))
            {
                continue;
            }

            if (only is not null)
            {
                text.Append(server.Instructions.Trim());
                continue;
            }

            text.Append(text.Length > 0 ? "\n\n" : string.Empty).Append("## ").Append(server.DisplayName).Append(" (").Append(server.Slug).Append(")\n")
                .Append(server.Instructions.Trim());
        }

        if (text.Length > 0)
        {
            w.WriteString("instructions", text.Length > McpMaxInstructions ? text.ToString(0, McpMaxInstructions) : text.ToString());
        }
    }

    private static string Truncate(string text, int length) => text.Length > length ? text[..length] : text;

    [LoggerMessage(Level = LogLevel.Error, Message = "Credential for MCP server {Server} cannot be used ({ExceptionType})")]
    private static partial void LogMcpCredentialUnavailable(ILogger logger, string server, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Request {RequestId}: MCP server {Server} failed ({Failure}, HTTP {Status}) after {Attempts} attempt(s)")]
    private static partial void LogMcpUpstreamFailed(ILogger logger, string requestId, string server, string failure, int status, int attempts);
}
