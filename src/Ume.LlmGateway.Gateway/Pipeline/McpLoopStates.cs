using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>A tool call of a stopped <c>/v1/responses</c> MCP loop that still has to be answered.</summary>
/// <param name="CallId">The provider's <c>call_id</c>.</param>
/// <param name="ServerLabel">The server's slug.</param>
/// <param name="Tool">The server's name of the tool.</param>
/// <param name="Exposed">The function name the model called.</param>
/// <param name="Arguments">The arguments exactly as the model produced them.</param>
/// <param name="ApprovalId">The <c>mcp_approval_request</c> id when the call waits for approval.</param>
/// <param name="NotRun">Why the call was not run (a limit), when it is not to be run at all.</param>
internal sealed record McpLoopCall(string CallId, string ServerLabel, string Tool, string Exposed, string Arguments, string? ApprovalId, string? NotRun);

/// <summary>A stopped loop with <c>store</c> on: the provider response the client continues from, and the calls it owes.</summary>
internal sealed record McpPendingLoop(string ProviderResponseId, Guid DeploymentId, string ParentRequestId, IReadOnlyList<McpLoopCall> Calls);

/// <summary>
/// The state of stopped MCP loops in the shared store: encrypted with Data Protection (tool arguments can hold personal
/// data; the purpose includes the key, so a value only opens for the key it was written for), stored under the key's id,
/// with a TTL, and taken once. Tool results are never stored.
/// </summary>
public sealed class McpLoopStates(IMcpLoopStore store, IDataProtectionProvider protection, IOptionsMonitor<GatewayOptions> options)
{
    private readonly IDataProtector _protector = protection.CreateProtector("Ume.LlmGateway.McpLoopState.v1");

    private TimeSpan TimeToLive => TimeSpan.FromMinutes(options.CurrentValue.Mcp.Loop.ApprovalTtlMinutes);

    internal Task SaveAsync(Guid keyId, string? pendingId, McpPendingLoop? pending, IReadOnlyList<McpLoopCall> approvals, string parentRequestId,
        CancellationToken ct)
    {
        var values = new List<(McpLoopRecord, string, byte[])>();
        if (pendingId is not null && pending is not null)
        {
            values.Add((McpLoopRecord.Pending, pendingId, Protect(keyId, new JsonObject
            {
                ["response"] = pending.ProviderResponseId,
                ["deployment"] = pending.DeploymentId.ToString("D"),
                ["parent"] = pending.ParentRequestId,
                ["calls"] = new JsonArray([.. pending.Calls.Select(c => (JsonNode)Write(c))]),
            })));
        }

        foreach (var call in approvals)
        {
            var record = Write(call);
            record["parent"] = parentRequestId;
            values.Add((McpLoopRecord.Approval, call.ApprovalId!, Protect(keyId, record)));
        }

        return values.Count == 0 ? Task.CompletedTask : store.SaveAsync(keyId, values, TimeToLive, ct);
    }

    internal async Task<McpPendingLoop?> GetPendingAsync(Guid keyId, string id, bool take, CancellationToken ct)
    {
        if (Unprotect(keyId, take ? await store.TakeAsync(keyId, McpLoopRecord.Pending, id, ct) : await store.GetAsync(keyId, McpLoopRecord.Pending, id, ct)) is not { } json)
        {
            return null;
        }

        return new McpPendingLoop(json["response"]!.GetValue<string>(), Guid.Parse(json["deployment"]!.GetValue<string>()), json["parent"]!.GetValue<string>(),
            [.. json["calls"]!.AsArray().Select(c => Read(c!.AsObject()))]);
    }

    /// <summary>An approval request's call and the request it belongs to.</summary>
    internal async Task<(McpLoopCall Call, string ParentRequestId)?> GetApprovalAsync(Guid keyId, string id, bool take, CancellationToken ct)
    {
        return Unprotect(keyId, take ? await store.TakeAsync(keyId, McpLoopRecord.Approval, id, ct) : await store.GetAsync(keyId, McpLoopRecord.Approval, id, ct)) is { } json
            ? (Read(json), json["parent"]!.GetValue<string>())
            : null;
    }

    private static JsonObject Write(McpLoopCall call) => new()
    {
        ["callId"] = call.CallId,
        ["server"] = call.ServerLabel,
        ["tool"] = call.Tool,
        ["exposed"] = call.Exposed,
        ["arguments"] = call.Arguments,
        ["approvalId"] = call.ApprovalId,
        ["notRun"] = call.NotRun,
    };

    private static McpLoopCall Read(JsonObject json) => new(json["callId"]!.GetValue<string>(), json["server"]!.GetValue<string>(),
        json["tool"]!.GetValue<string>(), json["exposed"]!.GetValue<string>(), json["arguments"]!.GetValue<string>(),
        json["approvalId"]?.GetValue<string>(), json["notRun"]?.GetValue<string>());

    private byte[] Protect(Guid keyId, JsonObject value) =>
        _protector.CreateProtector(keyId.ToString("N")).Protect(Encoding.UTF8.GetBytes(value.ToJsonString()));

    private JsonObject? Unprotect(Guid keyId, byte[]? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(_protector.CreateProtector(keyId.ToString("N")).Unprotect(value)) as JsonObject;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return null;
        }
    }
}
