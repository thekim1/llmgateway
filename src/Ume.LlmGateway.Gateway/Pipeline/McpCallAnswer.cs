using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>
/// Where the outcome of an accounted MCP request goes: a JSON-RPC answer on <c>/mcp</c> (<see cref="McpRpcAnswer"/>), or
/// the REST answer of <c>POST /v1/mcp/tools/call</c> (<see cref="McpToolCallAnswer"/>). The accounted steps are the same
/// for both.
/// </summary>
internal interface IMcpCallAnswer
{
    HttpContext Http { get; }

    /// <summary>Sets a response header while headers can still be sent; afterwards it is dropped.</summary>
    void SetHeader(string name, string value);

    /// <summary>
    /// Answers a refusal or failure: <paramref name="rpcCode"/> on JSON-RPC, <paramref name="status"/> and
    /// <paramref name="errorCode"/> on REST. Returns the HTTP status that was sent.
    /// </summary>
    Task<int> ErrorAsync(int rpcCode, string message, int status, string errorCode, JsonObject? data, CancellationToken ct);

    /// <summary>Answers with the server's result (after the PII guard).</summary>
    Task ResultAsync(JsonNode? result, CancellationToken ct);
}

/// <summary>The JSON-RPC answer to request <paramref name="id"/> on <c>/mcp</c>.</summary>
internal sealed class McpRpcAnswer(McpResponse response, JsonNode id) : IMcpCallAnswer
{
    public HttpContext Http => response.Http;

    public void SetHeader(string name, string value) => response.SetHeader(name, value);

    public async Task<int> ErrorAsync(int rpcCode, string message, int status, string errorCode, JsonObject? data, CancellationToken ct)
    {
        await response.ErrorAsync(id, rpcCode, message, data, ct: ct);
        return 200;
    }

    public Task ResultAsync(JsonNode? result, CancellationToken ct) => response.ResultAsync(id, w =>
    {
        if (result is null)
        {
            w.WriteNullValue();
        }
        else
        {
            result.WriteTo(w);
        }
    }, ct);
}

/// <summary>
/// The answer of <c>POST /v1/mcp/tools/call</c>: the result in the shape of <paramref name="call"/>'s format, ready to
/// append to the conversation, or the gateway's usual HTTP error.
/// </summary>
internal sealed class McpToolCallAnswer(HttpContext http, LlmToolCall call, string docsUrl) : IMcpCallAnswer
{
    public HttpContext Http => http;

    public void SetHeader(string name, string value)
    {
        if (!http.Response.HasStarted)
        {
            http.Response.Headers[name] = value;
        }
    }

    public async Task<int> ErrorAsync(int rpcCode, string message, int status, string errorCode, JsonObject? data, CancellationToken ct)
    {
        await GatewayErrors.WriteAsync(http, GatewayEndpoint.McpToolCall, status, errorCode, message, docsUrl);
        return status;
    }

    public async Task ResultAsync(JsonNode? result, CancellationToken ct)
    {
        http.Response.StatusCode = 200;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(McpToolFormats.Result(call, result).ToJsonString(), ct);
    }
}
