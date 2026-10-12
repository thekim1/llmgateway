using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>
/// MCP tools for clients that run their own tool loop against the LLM APIs, without speaking MCP:
/// <c>GET /v1/mcp/tools</c> lists the tools the key may use as ready-to-send tool definitions, and
/// <c>POST /v1/mcp/tools/call</c> runs one tool call a model made and answers in the same format. Visibility is that of
/// <c>tools/list</c> on <c>/mcp</c>, and a call goes through the same accounted steps as <c>tools/call</c>.
/// </summary>
public sealed partial class GatewayRequestHandler
{
    /// <summary>Request header that links a tool call to the client's LLM request (<c>UsageRecord.ParentRequestId</c>).</summary>
    public const string McpParentRequestIdHeader = "x-ume-parent-request-id";

    private const int MaxParentRequestIdLength = 64;

    public async Task ListMcpToolsAsync(HttpContext http, string? format)
    {
        ArgumentNullException.ThrowIfNull(http);
        var ct = http.RequestAborted;
        var key = await AuthenticateAsync(http, GatewayEndpoint.McpToolCall, time.GetUtcNow(), ct);
        if (key is null)
        {
            return;
        }

        if (!McpToolFormats.TryParseFormat(format, out var toolFormat))
        {
            await GatewayErrors.WriteAsync(http, GatewayEndpoint.McpToolCall, 400, GatewayErrorCodes.InvalidRequest,
                "Okänt format. Använd 'openai', 'responses' eller 'anthropic'.", options.CurrentValue.DocsUrl);
            return;
        }

        var snapshot = await catalog.GetAsync(ct);
        var data = new JsonArray();
        foreach (var entry in (await VisibleToolsAsync(snapshot, key, null, ct)).OrderBy(t => t.Tool.ExposedName, StringComparer.Ordinal))
        {
            data.Add(McpToolFormats.Definition(toolFormat, entry.Tool.ExposedName, entry.Definition));
        }

        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(new JsonObject
        {
            ["object"] = "list",
            ["format"] = toolFormat switch
            {
                LlmToolFormat.Responses => "responses",
                LlmToolFormat.Anthropic => "anthropic",
                _ => "openai",
            },
            ["data"] = data,
        }.ToJsonString(), ct);
    }

    public async Task CallMcpToolAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var ct = http.RequestAborted;
        var state = new RequestState(RequestIdMiddleware.Get(http), GatewayEndpoint.McpToolCall, time.GetTimestamp()) { McpMethod = "tools/call" };
        var key = await AuthenticateAsync(http, GatewayEndpoint.McpToolCall, time.GetUtcNow(), ct);
        if (key is null)
        {
            return;
        }

        state.Key = key;
        var parent = http.Request.Headers[McpParentRequestIdHeader];
        if (parent.Count > 0)
        {
            if (parent.Count > 1 || !IsValidParentRequestId(parent.ToString()))
            {
                await RejectAsync(http, state, 400, GatewayErrorCodes.InvalidRequest,
                    $"'{McpParentRequestIdHeader}' får vara högst {MaxParentRequestIdLength} tecken: bokstäver (a–z, A–Z), siffror och _ - . :", RequestOutcome.Rejected);
                return;
            }

            state.ParentRequestId = parent.ToString();
        }

        if (!http.Request.HasJsonContentType())
        {
            await RejectAsync(http, state, 415, GatewayErrorCodes.UnsupportedMediaType, "Content-Type måste vara application/json.", RequestOutcome.Rejected);
            return;
        }

        var (body, _, status) = await ReadJsonAsync(http.Request, ct);
        if (status != 0)
        {
            await RejectAsync(http, state, status, status == 413 ? GatewayErrorCodes.RequestTooLarge : GatewayErrorCodes.InvalidRequest,
                status == 413 ? "Förfrågan är för stor." : "Förfrågan är inte giltig JSON.", RequestOutcome.Rejected);
            return;
        }

        if (body is null || McpToolFormats.ParseCall(body) is not { } call)
        {
            await RejectAsync(http, state, 400, GatewayErrorCodes.InvalidRequest,
                "Skicka ett verktygsanrop med id och namn: ett 'tool_calls'-element (Chat Completions), ett 'function_call' (Responses) eller ett 'tool_use'-block (Anthropic).",
                RequestOutcome.Rejected);
            return;
        }

        var snapshot = await catalog.GetAsync(ct);
        await CallToolAsync(new McpToolCallAnswer(http, call, options.CurrentValue.DocsUrl), state, snapshot, null, call.Name, call.Arguments, null, ct);
    }

    private static bool IsValidParentRequestId(string value) =>
        value.Length is > 0 and <= MaxParentRequestIdLength && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':');
}
