using System.Buffers;
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
/// The MCP gateway: Streamable HTTP <c>POST /mcp</c> (every server the key is granted, tools named
/// <see cref="McpToolNames.Exposed"/>) and <c>POST /mcp/{slug}</c> (one server, original names). Stateless for every
/// client: <c>2026-07-28</c> clients send their version in each request's <c>_meta</c>, handshake-era clients are answered
/// <c>initialize</c> and <c>ping</c> without a session, and any instance serves any request. Discovery is answered from
/// the catalogue; only <c>tools/call</c> reaches a server, and each one is accounted exactly once.
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

        var (body, _, status) = await ReadJsonAsync(http.Request, ct);
        if (status == 413)
        {
            await GatewayErrors.WriteAsync(http, GatewayEndpoint.Mcp, 413, GatewayErrorCodes.RequestTooLarge, "Förfrågan är för stor.", options.CurrentValue.DocsUrl);
            return;
        }

        if (status != 0)
        {
            await WriteRpcErrorAsync(http, null, (int)ModelContextProtocol.McpErrorCode.ParseError, "Parse error", httpStatus: 400);
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
            await WriteRpcErrorAsync(http, null, (int)ModelContextProtocol.McpErrorCode.InvalidRequest, "Invalid Request", httpStatus: 400);
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
            http.Response.StatusCode = 202; // a notification
            return;
        }

        var parameters = body["params"] as JsonObject;
        var requested = parameters?["_meta"]?[McpVersionMeta] is JsonValue v && v.TryGetValue<string>(out var metaVersion) ? metaVersion : null;
        var header = http.Request.Headers[McpProtocolVersionHeader].ToString();
        if ((requested is not null && requested != McpStatelessVersion)
            || (header.Length > 0 && header != McpStatelessVersion && !McpHandshakeVersions.Contains(header)))
        {
            await WriteRpcErrorAsync(http, id, McpErrors.UnsupportedProtocolVersion, "Unsupported protocol version", httpStatus: 400,
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
                await WriteRpcResultAsync(http, id, w => WriteInitializeResult(w, snapshot, key, only, parameters));
                return;
            case "server/discover":
                await WriteRpcResultAsync(http, id, w => WriteDiscoverResult(w, snapshot, key, only));
                return;
            case "ping":
                await WriteRpcResultAsync(http, id, w => { w.WriteStartObject(); w.WriteEndObject(); });
                return;
            case "tools/list":
                var tools = await VisibleToolsAsync(snapshot, key, only, ct);
                await WriteRpcResultAsync(http, id, w => WriteToolList(w, tools, only is null, stateless));
                return;
            case "tools/call":
                await CallToolAsync(http, snapshot, key, only, id, parameters, ct);
                return;
            default:
                await WriteRpcErrorAsync(http, id, (int)ModelContextProtocol.McpErrorCode.MethodNotFound, $"Method not found: '{Truncate(method, 100)}'");
                return;
        }
    }

    private async Task CallToolAsync(HttpContext http, CatalogSnapshot snapshot, VirtualKey key, McpServer? only, JsonNode id, JsonObject? parameters, CancellationToken ct)
    {
        var state = new RequestState(RequestIdMiddleware.Get(http), GatewayEndpoint.Mcp, time.GetTimestamp()) { Key = key, McpMethod = "tools/call" };
        var name = parameters?["name"] is JsonValue n && n.TryGetValue<string>(out var given) ? given : null;
        state.RequestedModel = McpUsageName(only?.Slug, name ?? string.Empty);
        state.McpTool = name is null ? null : Truncate(name, 128);

        // 1. Resolve and authorise: every refusal looks like an unknown tool, the reason goes to the security log.
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
            if (_security.IsEnabled(LogLevel.Information))
            {
                SecurityEvents.McpToolDenied(_security, state.McpTool ?? "-", state.RequestId, denial is { } d ? McpAccess.ReasonName(d) : "unknown_tool",
                    entry?.Server.Slug, key.Id, key.Prefix, key.TeamId, key.Team?.DepartmentId ?? Guid.Empty);
            }

            await RefuseToolAsync(http, state, id, (int)ModelContextProtocol.McpErrorCode.InvalidParams, $"Unknown tool: '{Truncate(name ?? string.Empty, 128)}'",
                404, RequestOutcome.Rejected, GatewayErrorCodes.McpToolNotFound);
            return;
        }

        var server = entry.Server;
        state.McpServerId = server.Id;
        state.McpTool = entry.Tool.Name;
        state.RequestedModel = McpUsageName(server.Slug, entry.Tool.Name);

        // 2. Arguments against the approved input schema.
        var arguments = parameters!["arguments"];
        if (arguments is not null and not JsonObject)
        {
            await RefuseToolAsync(http, state, id, (int)ModelContextProtocol.McpErrorCode.InvalidParams, "Invalid params: 'arguments' must be an object",
                400, RequestOutcome.Rejected, GatewayErrorCodes.McpInvalidArguments);
            return;
        }

        var args = (JsonObject?)arguments ?? [];
        if (entry.Definition.InputSchema is { } schema && McpSchemaValidator.Validate(schema, args) is { } invalid)
        {
            await RefuseToolAsync(http, state, id, (int)ModelContextProtocol.McpErrorCode.InvalidParams, $"Invalid arguments for tool '{entry.Tool.Name}': {invalid}",
                400, RequestOutcome.Rejected, GatewayErrorCodes.McpInvalidArguments);
            return;
        }

        // 3. Rate limit: MCP calls per minute.
        var rate = await rateLimiter.AcquireMcpCallAsync(key.Id, key.McpCallsPerMinute, ct);
        if (!rate.Allowed)
        {
            var retryAfter = Math.Max(1, (int)Math.Ceiling(rate.RetryAfter.TotalSeconds));
            http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
            await RefuseToolAsync(http, state, id, McpErrors.RateLimited, $"Gränsen för MCP-anrop per minut är nådd för nyckeln. Försök igen om {retryAfter} s.",
                429, RequestOutcome.RateLimited, GatewayErrorCodes.RateLimited, new JsonObject { ["code"] = GatewayErrorCodes.RateLimited, ["retryAfterSeconds"] = retryAfter });
            return;
        }

        // 4. PII guard on the arguments.
        PiiScanResult? argumentScan = null;
        if (key.PiiPolicy != PiiPolicy.Off)
        {
            argumentScan = McpPii.ScanArguments(args, redact: key.PiiPolicy == PiiPolicy.Redact);
            state.PiiCategories = argumentScan.Summary;
            if (argumentScan.HasPii)
            {
                state.PiiAction = key.PiiPolicy;
                var decision = PiiJsonScanner.Decide(key.PiiPolicy, argumentScan);
                http.Response.Headers[GatewayHeaders.Pii] = decision switch
                {
                    PiiDecision.ForwardRedacted => "redacted",
                    PiiDecision.Block => "blocked",
                    PiiDecision.ForwardOnPremOnly => server.Residency == DataResidency.OnPrem ? "rerouted-onprem" : "blocked",
                    _ => "detected",
                };
                if (decision == PiiDecision.Block)
                {
                    await RefuseToolAsync(http, state, id, McpErrors.PiiRefused,
                        $"Argumenten innehåller personuppgifter ({argumentScan.Summary}) och nyckelns policy tillåter inte det. Ta bort uppgifterna och försök igen.",
                        400, RequestOutcome.PiiBlocked, GatewayErrorCodes.PiiBlocked, new JsonObject { ["code"] = GatewayErrorCodes.PiiBlocked });
                    return;
                }

                if (decision == PiiDecision.ForwardOnPremOnly && server.Residency != DataResidency.OnPrem)
                {
                    await RefuseToolAsync(http, state, id, McpErrors.PiiRefused,
                        $"Argumenten innehåller personuppgifter ({argumentScan.Summary}); nyckeln får då bara använda MCP-servrar som körs on-prem.",
                        400, RequestOutcome.PiiBlocked, GatewayErrorCodes.PiiBlocked, new JsonObject { ["code"] = GatewayErrorCodes.PiiBlocked });
                    return;
                }
            }
        }

        // 5. Budget, for priced tools.
        var price = entry.Tool.PriceSek is > 0 ? entry.Tool.PriceSek.Value : 0m;
        if (price > 0)
        {
            var reservation = await budgets.ReserveAsync(BudgetService.ApplicableBudgets(snapshot, key), price, ct);
            state.Reservation = reservation;
            if (!reservation.Allowed)
            {
                await RefuseToolAsync(http, state, id, McpErrors.BudgetExceeded, BudgetExceededMessage(reservation.Exceeded!.Budget, "Kontakta budgetansvarig."),
                    402, RequestOutcome.BudgetExceeded, GatewayErrorCodes.BudgetExceeded, new JsonObject { ["code"] = GatewayErrorCodes.BudgetExceeded });
                return;
            }
        }

        // 6. The upstream call. From here on the reservation must be reconciled whatever happens.
        try
        {
            await SendToolCallAsync(http, state, snapshot, entry, args, id, price, argumentScan, ct);
        }
        catch (Exception ex) when (!state.Accounted)
        {
            if (!await AccountUnexpectedAsync(http, state, snapshot, ex))
            {
                throw;
            }
        }
    }

    private async Task SendToolCallAsync(HttpContext http, RequestState state, CatalogSnapshot snapshot, McpToolEntry entry, JsonObject args, JsonNode id,
        decimal price, PiiScanResult? argumentScan, CancellationToken ct)
    {
        var server = entry.Server;
        McpUpstreamAuth auth;
        try
        {
            auth = snapshot.McpCredentials.Get(server, credentials);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            LogMcpCredentialUnavailable(logger, server.Slug, ex.GetType().Name);
            await RefuseToolAsync(http, state, id, McpErrors.UpstreamFailed, $"MCP-servern '{server.Slug}' kan inte nås just nu.",
                502, RequestOutcome.ProviderError, GatewayErrorCodes.McpUpstreamFailed, new JsonObject { ["code"] = GatewayErrorCodes.McpUpstreamFailed });
            return;
        }

        var forwarded = new List<KeyValuePair<string, string>>();
        foreach (var name in server.ForwardHeaders)
        {
            if (McpUpstream.IsForwardable(name) && http.Request.Headers.TryGetValue(name, out var value) && value.Count > 0)
            {
                forwarded.Add(new(name, value.ToString()));
            }
        }

        if (args.Parent is { } parent)
        {
            ((JsonObject)parent).Remove("arguments");
        }

        var call = new McpUpstreamCall(server, auth, "tools/call", new JsonObject { ["name"] = entry.Tool.Name, ["arguments"] = args }, forwarded, entry.Definition.MayRetry);
        var started = time.GetTimestamp();
        using var activity = McpActivities.StartActivity("tools/call " + entry.Tool.Name, ActivityKind.Client);
        activity?.SetTag("mcp.method.name", "tools/call");
        activity?.SetTag("gen_ai.tool.name", entry.Tool.Name);
        activity?.SetTag("gen_ai.operation.name", "execute_tool");
        activity?.SetTag("ume.mcp.server", server.Slug);
        activity?.SetTag("ume.request_id", state.RequestId);

        McpUpstreamResult result;
        try
        {
            result = await mcp.SendAsync(call, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            metrics.McpCall(server.Slug, "tools/call", "cancelled", time.GetElapsedTime(started).TotalMilliseconds);
            await AccountAsync(state, null, default, 499, RequestOutcome.ClientCancelled, null, snapshot);
            return;
        }

        activity?.SetTag("ume.mcp.attempts", result.Attempts);
        metrics.McpCall(server.Slug, "tools/call", result.Failure == McpUpstreamFailure.None ? "success" : result.Failure.ToString().ToLowerInvariant(),
            time.GetElapsedTime(started).TotalMilliseconds);
        await RecordMcpCircuitAsync(state, server, result);
        if (!result.Succeeded)
        {
            activity?.SetStatus(ActivityStatusCode.Error, result.Failure.ToString());
            LogMcpUpstreamFailed(logger, state.RequestId, server.Slug, result.Failure.ToString(), result.HttpStatus ?? 0, result.Attempts);
            var (code, message, status, error) = result.Failure switch
            {
                McpUpstreamFailure.JsonRpcError => (result.ErrorCode, UpstreamErrorMessage(http, state, result.ErrorMessage, argumentScan), 400, GatewayErrorCodes.ProviderRejected),
                McpUpstreamFailure.TooLarge => (McpErrors.ResultTooLarge,
                    $"Svaret från MCP-servern är större än tillåtet ({server.MaxResultBytes.ToString(CultureInfo.InvariantCulture)} byte).", 502, GatewayErrorCodes.McpResultTooLarge),
                McpUpstreamFailure.Timeout => (McpErrors.UpstreamFailed, $"MCP-servern '{server.Slug}' svarade inte i tid.", 504, GatewayErrorCodes.McpUpstreamFailed),
                _ => (McpErrors.UpstreamFailed, $"MCP-servern '{server.Slug}' kan inte nås just nu.", 502, GatewayErrorCodes.McpUpstreamFailed),
            };
            await RefuseToolAsync(http, state, id, code, message, status, RequestOutcome.ProviderError, error,
                result.Failure == McpUpstreamFailure.JsonRpcError ? null : new JsonObject { ["code"] = error });
            return;
        }

        var cost = price > 0 ? new Cost(snapshot.SekPerUsd > 0 ? price / snapshot.SekPerUsd : 0, price) : default;

        // 7. PII guard on the result: it reaches the client and, on the LLM path, a model.
        var outcome = RequestOutcome.Success;
        var answer = result.Result;
        if (state.Key!.PiiPolicy != PiiPolicy.Off)
        {
            var resultScan = McpPii.ScanResult(answer, redact: state.Key.PiiPolicy == PiiPolicy.Redact);
            if (resultScan.HasPii)
            {
                var all = argumentScan is null ? resultScan : PiiJsonScanner.Merge(argumentScan, resultScan);
                state.PiiCategories = all.Summary;
                state.PiiAction = state.Key.PiiPolicy;
                if (state.Key.PiiPolicy == PiiPolicy.Block)
                {
                    http.Response.Headers[GatewayHeaders.Pii] = "blocked";
                    await WriteRpcErrorAsync(http, id, McpErrors.PiiRefused,
                        $"Svaret från verktyget innehåller personuppgifter ({resultScan.Summary}) och nyckelns policy tillåter inte det.",
                        data: new JsonObject { ["code"] = GatewayErrorCodes.PiiBlocked });
                    await AccountAsync(state, null, default, 200, RequestOutcome.PiiBlocked, GatewayErrorCodes.PiiBlocked, snapshot, cost);
                    return;
                }

                http.Response.Headers[GatewayHeaders.Pii] = state.Key.PiiPolicy == PiiPolicy.Redact ? "redacted" : "detected";
            }
        }

        using (usageWriter.TrackPending())
        {
            if (price > 0)
            {
                http.Response.Headers[GatewayHeaders.CostSek] = price.ToString("0.000000", CultureInfo.InvariantCulture);
            }

            await WriteRpcResultAsync(http, id, w =>
            {
                if (answer is null)
                {
                    w.WriteNullValue();
                }
                else
                {
                    answer.WriteTo(w);
                }
            }, CancellationToken.None);
            await AccountAsync(state, null, default, 200, outcome, null, snapshot, cost);
        }
    }

    /// <summary>
    /// The server's own JSON-RPC error message, passed on to the client: cut to <see cref="McpMaxErrorMessage"/>
    /// characters and put through the key's PII policy like a result text.
    /// </summary>
    private static string UpstreamErrorMessage(HttpContext http, RequestState state, string? message, PiiScanResult? argumentScan)
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
        http.Response.Headers[GatewayHeaders.Pii] = policy switch
        {
            PiiPolicy.Redact => "redacted",
            PiiPolicy.Block => "blocked",
            _ => "detected",
        };
        return policy == PiiPolicy.Block
            ? "Verktyget svarade med ett fel vars text innehöll personuppgifter; texten har tagits bort."
            : holder["message"]!.GetValue<string>();
    }

    /// <summary>
    /// What an MCP call's usage record holds in <c>RequestedModel</c>: <c>mcp:{slug}/{tool}</c> (the server's name for the
    /// tool), so reports that group by model show MCP calls apart from models and per tool. For a name that matched no
    /// tool, the name as the client sent it.
    /// </summary>
    private static string McpUsageName(string? slug, string tool) =>
        ModelNames.Truncate(slug is null ? "mcp:" + tool : $"mcp:{slug}/{tool}");

    /// <summary>Answers a refused <c>tools/call</c> with a JSON-RPC error and accounts it.</summary>
    private async Task RefuseToolAsync(HttpContext http, RequestState state, JsonNode id, int code, string message, int recordedStatus, RequestOutcome outcome,
        string errorCode, JsonObject? data = null)
    {
        await WriteRpcErrorAsync(http, id, code, message, data: data);
        await AccountAsync(state, null, default, recordedStatus, outcome, errorCode, null);
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
        var tools = new List<McpToolEntry>();
        foreach (var (server, grant) in servers)
        {
            var circuitOpen = open.Contains(server.Id);
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

        return servers;
    }

    private async Task<bool> IsMcpCircuitOpenAsync(Guid serverId, CancellationToken ct) => (await OpenMcpCircuitsAsync([serverId], ct)).Contains(serverId);

    /// <summary>Servers whose circuit is open. The circuit store being unavailable counts as closed, as for providers.</summary>
    private async Task<IReadOnlySet<Guid>> OpenMcpCircuitsAsync(IReadOnlyCollection<Guid> serverIds, CancellationToken ct)
    {
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

    private async Task RecordMcpCircuitAsync(RequestState state, McpServer server, McpUpstreamResult result)
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
            LogCircuitStoreFailed(logger, ex.GetType().Name, state.RequestId);
        }
    }

    private static void WriteInitializeResult(Utf8JsonWriter w, CatalogSnapshot snapshot, VirtualKey key, McpServer? only, JsonObject? parameters)
    {
        var requested = parameters?["protocolVersion"] is JsonValue v && v.TryGetValue<string>(out var version) ? version : null;
        w.WriteStartObject();
        w.WriteString("protocolVersion", requested is not null && McpHandshakeVersions.Contains(requested) ? requested : McpHandshakeVersions[0]);
        WriteCapabilities(w);
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
        WriteCapabilities(w);
        WriteInstructions(w, snapshot, key, only);
        WriteCacheHints(w);
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
            WriteCacheHints(w);
        }

        w.WriteEndObject();
    }

    private static void WriteCapabilities(Utf8JsonWriter w)
    {
        w.WriteStartObject("capabilities");
        w.WriteStartObject("tools");
        w.WriteBoolean("listChanged", false);
        w.WriteEndObject();
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

    /// <summary>The lists depend on the key's grants, so shared caches must not keep them.</summary>
    private void WriteCacheHints(Utf8JsonWriter w)
    {
        w.WriteNumber("ttlMs", options.CurrentValue.CatalogCacheSeconds * 1000L);
        w.WriteString("cacheScope", "private");
        w.WriteString("resultType", "complete");
    }

    /// <summary>The instructions of the servers the key may use, each under its name, capped in total.</summary>
    private static void WriteInstructions(Utf8JsonWriter w, CatalogSnapshot snapshot, VirtualKey key, McpServer? only)
    {
        var text = new StringBuilder();
        foreach (var (server, _) in VisibleServers(snapshot, key, only).OrderBy(s => s.Server.Slug, StringComparer.Ordinal))
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

    private static Task WriteRpcResultAsync(HttpContext http, JsonNode id, Action<Utf8JsonWriter> result, CancellationToken? ct = null) =>
        WriteRpcAsync(http, id, w =>
        {
            w.WritePropertyName("result");
            result(w);
        }, 200, ct);

    private static Task WriteRpcErrorAsync(HttpContext http, JsonNode? id, int code, string message, int httpStatus = 200, JsonObject? data = null) =>
        WriteRpcAsync(http, id, w =>
        {
            w.WriteStartObject("error");
            w.WriteNumber("code", code);
            w.WriteString("message", message);
            if (data is not null)
            {
                w.WritePropertyName("data");
                data.WriteTo(w);
            }

            w.WriteEndObject();
        }, httpStatus, null);

    private static async Task WriteRpcAsync(HttpContext http, JsonNode? id, Action<Utf8JsonWriter> body, int status, CancellationToken? ct)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            if (id is null)
            {
                w.WriteNullValue();
            }
            else
            {
                id.WriteTo(w);
            }

            body(w);
            w.WriteEndObject();
        }

        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json";
        http.Response.ContentLength = buffer.WrittenCount;
        await http.Response.Body.WriteAsync(buffer.WrittenMemory, ct ?? http.RequestAborted);
    }

    private static string Truncate(string text, int length) => text.Length > length ? text[..length] : text;

    [LoggerMessage(Level = LogLevel.Error, Message = "Credential for MCP server {Server} cannot be used ({ExceptionType})")]
    private static partial void LogMcpCredentialUnavailable(ILogger logger, string server, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Request {RequestId}: MCP server {Server} failed ({Failure}, HTTP {Status}) after {Attempts} attempt(s)")]
    private static partial void LogMcpUpstreamFailed(ILogger logger, string requestId, string server, string failure, int status, int attempts);
}
