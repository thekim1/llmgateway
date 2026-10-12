using System.Text.Json;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain.Entities;

namespace Ume.LlmGateway.Domain.Services;

/// <summary>A tool as a server offers it in <c>tools/list</c>: its name and definition (<see cref="McpToolDefinition.FromTool"/>).</summary>
public sealed record McpDiscoveredTool(string Name, string Definition);

/// <summary>What one sync changed. Names are the servers' own tool names.</summary>
public sealed class McpToolSyncResult
{
    public List<McpTool> Added { get; } = [];
    public List<McpTool> Removed { get; } = [];

    /// <summary>Approved tools whose definition changed upstream (now <see cref="McpToolApprovalState.Changed"/>), or changed again.</summary>
    public List<string> Changed { get; } = [];

    /// <summary>Changed tools whose upstream definition is the approved one again.</summary>
    public List<string> Reverted { get; } = [];

    /// <summary>Pending or rejected tools whose definition changed; nothing clients see.</summary>
    public List<string> Updated { get; } = [];

    /// <summary>Tools that could not be taken over, with why (no content of the tool).</summary>
    public List<string> Errors { get; } = [];

    /// <summary>Whether any tool row changed (stored hashes may also be refreshed without a change worth reporting).</summary>
    public bool ToolsChanged => Added.Count + Removed.Count + Changed.Count + Reverted.Count + Updated.Count > 0;
}

/// <summary>
/// Tool pinning: applies a server's current tool list to its stored tools. Upstream changes never reach clients by
/// themselves; only an admin's approval (or <see cref="McpServer.AutoApproveNewTools"/> for new tools) sets
/// <see cref="McpTool.ApprovedDefinition"/>.
/// <list type="bullet">
/// <item>A new tool is <see cref="McpToolApprovalState.Pending"/>, or approved at once when the server auto-approves new tools.</item>
/// <item>An approved tool whose definition changed becomes <see cref="McpToolApprovalState.Changed"/>: clients keep the approved definition.</item>
/// <item>A changed tool whose definition is the approved one again is approved again.</item>
/// <item>A tool the server no longer offers is deleted, so it is hidden at once; if it comes back it is a new tool.</item>
/// <item>A new tool whose exposed name another tool already has (after sanitising) is left out with an error, never merged.</item>
/// </list>
/// Hashes are compared canonically (<see cref="McpToolDefinition.Hash"/>).
/// </summary>
public static class McpToolSync
{
    public const int MaxNameLength = 128;
    public const int MaxTitleLength = 200;
    public const string AutoApprover = "system:auto-approve";

    /// <param name="server">The server with all its stored tools loaded; tools are added to and removed from <see cref="McpServer.Tools"/>.</param>
    /// <param name="discovered">The server's tools now.</param>
    /// <param name="exposedElsewhere">Exposed names of other servers' tools (unique across servers).</param>
    /// <param name="now">The time of the sync.</param>
    public static McpToolSyncResult Apply(McpServer server, IReadOnlyList<McpDiscoveredTool> discovered, IReadOnlySet<string> exposedElsewhere, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(discovered);
        ArgumentNullException.ThrowIfNull(exposedElsewhere);
        var result = new McpToolSyncResult();
        var offered = new Dictionary<string, McpDiscoveredTool>(StringComparer.Ordinal);
        foreach (var tool in discovered)
        {
            if (!offered.TryAdd(tool.Name, tool))
            {
                result.Errors.Add($"Verktyget {tool.Name} finns flera gånger i serverns lista; bara det första används.");
            }
        }

        foreach (var gone in server.Tools.Where(t => !offered.ContainsKey(t.Name)).ToList())
        {
            server.Tools.Remove(gone);
            result.Removed.Add(gone);
        }

        var exposed = server.Tools.ToDictionary(t => t.ExposedName, t => t.Name, StringComparer.Ordinal);
        var stored = server.Tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        foreach (var tool in offered.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            string hash;
            try
            {
                hash = McpToolDefinition.Hash(tool.Definition);
            }
            catch (JsonException)
            {
                result.Errors.Add($"Verktyget {tool.Name} har ingen giltig definition.");
                continue;
            }

            if (stored.TryGetValue(tool.Name, out var existing))
            {
                Update(existing, tool.Definition, hash, result);
                continue;
            }

            if (tool.Name.Length > MaxNameLength)
            {
                result.Errors.Add($"Ett verktygsnamn är längre än {MaxNameLength} tecken och togs inte med.");
                continue;
            }

            var exposedName = McpToolNames.Exposed(server.Slug, tool.Name);
            if (exposed.TryGetValue(exposedName, out var holder) || exposedElsewhere.Contains(exposedName))
            {
                result.Errors.Add(holder is null
                    ? $"Verktyget {tool.Name} får namnet {exposedName}, som ett verktyg på en annan server redan har. Det togs inte med."
                    : $"Verktyget {tool.Name} får samma namn på /mcp som {holder} ({exposedName}). Det togs inte med.");
                continue;
            }

            var added = new McpTool
            {
                McpServerId = server.Id,
                Name = tool.Name,
                Title = Title(tool.Definition),
                Definition = tool.Definition,
                DefinitionHash = hash,
                ExposedName = exposedName,
                ApprovalState = McpToolApprovalState.Pending,
                DiscoveredAt = now,
            };
            if (server.AutoApproveNewTools)
            {
                Approve(added, AutoApprover, now);
            }

            server.Tools.Add(added);
            exposed[exposedName] = tool.Name;
            result.Added.Add(added);
        }

        return result;
    }

    /// <summary>Approves the tool's current definition: clients see it from the next catalogue on.</summary>
    public static void Approve(McpTool tool, string approvedBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tool);
        tool.ApprovalState = McpToolApprovalState.Approved;
        tool.ApprovedDefinition = tool.Definition;
        tool.Title = Title(tool.Definition);
        tool.ApprovedAt = now;
        tool.ApprovedBy = approvedBy;
    }

    /// <summary>The <c>title</c> of a definition, cut to <see cref="MaxTitleLength"/>.</summary>
    public static string? Title(string definition)
    {
        try
        {
            return JsonNode.Parse(definition) is JsonObject json && json["title"] is JsonValue value && value.TryGetValue<string>(out var title)
                && !string.IsNullOrWhiteSpace(title)
                ? title.Length > MaxTitleLength ? title[..MaxTitleLength] : title
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Update(McpTool tool, string definition, string hash, McpToolSyncResult result)
    {
        if (tool.DefinitionHash == hash)
        {
            return;
        }

        var differs = !SameDefinition(tool.Definition, hash);
        tool.Definition = definition;
        tool.DefinitionHash = hash;
        if (!differs)
        {
            return;
        }

        var approved = tool.ApprovedDefinition is { } approvedDefinition && SameDefinition(approvedDefinition, hash);
        switch (tool.ApprovalState)
        {
            case McpToolApprovalState.Approved or McpToolApprovalState.Changed when approved:
                if (tool.ApprovalState == McpToolApprovalState.Changed)
                {
                    tool.ApprovalState = McpToolApprovalState.Approved;
                    result.Reverted.Add(tool.Name);
                }

                break;
            case McpToolApprovalState.Approved or McpToolApprovalState.Changed:
                tool.ApprovalState = McpToolApprovalState.Changed;
                result.Changed.Add(tool.Name);
                break;
            default:
                tool.Title = Title(definition);
                result.Updated.Add(tool.Name);
                break;
        }
    }

    private static bool SameDefinition(string definition, string hash)
    {
        try
        {
            return McpToolDefinition.Hash(definition) == hash;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
