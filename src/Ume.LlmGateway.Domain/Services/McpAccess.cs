using Ume.LlmGateway.Domain.Entities;

namespace Ume.LlmGateway.Domain.Services;

/// <summary>Why a key may not see or call an MCP tool. Clients only ever see "unknown tool"; the reason is logged.</summary>
public enum McpDenial
{
    None = 0,
    NotGranted = 1,
    ToolNotGranted = 2,
    ServerDisabled = 3,
    Residency = 4,
    NotApproved = 5,
    CircuitOpen = 6,
    OnPremOnly = 7,
}

/// <summary>
/// The one authorisation rule for MCP tools, used for <c>tools/list</c> and <c>tools/call</c> alike, so a tool that is
/// not listed can never be called. Every check can only narrow: grant, enabled server, residency, approval, circuit.
/// </summary>
public static class McpAccess
{
    /// <summary>Whether the key may use the server at all (before looking at single tools).</summary>
    public static McpDenial CheckServer(VirtualKey key, McpServer server, VirtualKeyMcpGrant? grant, bool circuitOpen, DataResidency? restrictTo = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(server);
        if (grant is null || grant.McpServerId != server.Id || grant.VirtualKeyId != key.Id)
        {
            return McpDenial.NotGranted;
        }

        if (!server.IsEnabled)
        {
            return McpDenial.ServerDisabled;
        }

        if (key.AllowedResidencies.Count > 0 && !key.AllowedResidencies.Contains(server.Residency))
        {
            return McpDenial.Residency;
        }

        if (restrictTo is { } only && server.Residency != only)
        {
            return McpDenial.OnPremOnly;
        }

        return circuitOpen ? McpDenial.CircuitOpen : McpDenial.None;
    }

    /// <summary>Whether the key may see and call <paramref name="tool"/> on <paramref name="server"/>.</summary>
    public static McpDenial Check(VirtualKey key, McpServer server, VirtualKeyMcpGrant? grant, McpTool tool, bool circuitOpen, DataResidency? restrictTo = null)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var denial = CheckServer(key, server, grant, circuitOpen, restrictTo);
        if (denial != McpDenial.None)
        {
            return denial;
        }

        if (tool.McpServerId != server.Id || !tool.IsExposed)
        {
            return McpDenial.NotApproved;
        }

        return grant!.Allows(tool.Name) ? McpDenial.None : McpDenial.ToolNotGranted;
    }

    /// <summary>The <c>Reason</c> attribute of the <c>gateway.mcp.tool_denied</c> security event.</summary>
    public static string ReasonName(McpDenial denial) => denial switch
    {
        McpDenial.NotGranted => "not_granted",
        McpDenial.ToolNotGranted => "tool_not_granted",
        McpDenial.ServerDisabled => "server_disabled",
        McpDenial.Residency => "residency",
        McpDenial.NotApproved => "not_approved",
        McpDenial.CircuitOpen => "circuit_open",
        McpDenial.OnPremOnly => "pii_onprem_only",
        _ => "unknown_tool",
    };
}
