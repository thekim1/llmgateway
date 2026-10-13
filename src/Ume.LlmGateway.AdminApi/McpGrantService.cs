using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi;

/// <summary>A key's grant on one MCP server: <c>AllowedTools</c> are the server's tool names, or <c>["*"]</c> for all.</summary>
public sealed record McpGrantRequest(
    Guid ServerId,
    [property: Required, MaxLength(1000)] string[] AllowedTools,
    [property: EnumDataType(typeof(McpApprovalPolicy))] McpApprovalPolicy ApprovalPolicy = McpApprovalPolicy.Always,
    bool AllowResources = false,
    bool AllowPrompts = false) : AdminRequest, IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (AllowedTools?.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > McpToolSync.MaxNameLength) == true)
        {
            yield return new ValidationResult($"Verktygsnamn måste vara 1–{McpToolSync.MaxNameLength} tecken.", [nameof(AllowedTools)]);
        }
    }

    /// <summary>The names as stored: <c>*</c> alone when it is among them, otherwise each name once.</summary>
    public List<string> Tools() => AllowedTools.Contains(VirtualKeyMcpGrant.AllTools)
        ? [VirtualKeyMcpGrant.AllTools]
        : [.. AllowedTools.Distinct(StringComparer.Ordinal)];
}

/// <summary>
/// Which MCP servers a key may use. A user sees and changes a key's grants only on servers the user can see (gateway
/// admins every server; department admins global servers and their departments' servers), for keys in the user's departments.
/// </summary>
public sealed class McpGrantService(AdminContext ctx, McpServerService servers)
{
    public const int MaxGrants = 500;

    public async Task<List<McpGrantDto>> ListAsync(ClaimsPrincipal user, Guid keyId, CancellationToken ct)
    {
        await EnsureKeyAsync(user, keyId, ct);
        var visible = servers.Visible(user);
        var grants = await ctx.Db.VirtualKeyMcpGrants.AsNoTracking()
            .Where(g => g.VirtualKeyId == keyId && visible.Any(s => s.Id == g.McpServerId)).ToListAsync(ct);
        return await DtosAsync(grants, ct);
    }

    /// <summary>
    /// Replaces the key's grants on the servers the user can see with <paramref name="input"/>. Tool names the server does
    /// not offer (yet) are kept and reported in <see cref="McpGrantDto.UnknownTools"/>: they allow nothing until it does.
    /// </summary>
    public async Task<List<McpGrantDto>> ReplaceAsync(ClaimsPrincipal user, Guid keyId, IReadOnlyList<McpGrantRequest> input, CancellationToken ct)
    {
        await EnsureKeyAsync(user, keyId, ct);
        if (input.Count > MaxGrants)
        {
            throw new ApiFaultException(400, $"En nyckel kan ha högst {MaxGrants} MCP-servrar.");
        }
        if (input.Select(g => g.ServerId).Distinct().Count() != input.Count)
        {
            throw new ApiFaultException(400, "Varje MCP-server kan bara anges en gång.");
        }

        var ids = input.Select(g => g.ServerId).ToList();
        var visible = servers.Visible(user);
        if (await visible.CountAsync(s => ids.Contains(s.Id), ct) != ids.Count)
        {
            throw new ApiFaultException(400, "En MCP-server i listan finns inte.");
        }

        var existing = await ctx.Db.VirtualKeyMcpGrants
            .Where(g => g.VirtualKeyId == keyId && visible.Any(s => s.Id == g.McpServerId)).OrderBy(g => g.McpServerId).ToListAsync(ct);
        var before = existing.Select(McpGrantAudit.Of).ToList();
        var requested = input.ToDictionary(g => g.ServerId);
        ctx.Db.VirtualKeyMcpGrants.RemoveRange(existing.Where(g => !requested.ContainsKey(g.McpServerId)));
        var kept = existing.Where(g => requested.ContainsKey(g.McpServerId)).ToDictionary(g => g.McpServerId);
        foreach (var grant in input)
        {
            if (!kept.TryGetValue(grant.ServerId, out var stored))
            {
                stored = new VirtualKeyMcpGrant { VirtualKeyId = keyId, McpServerId = grant.ServerId, CreatedAt = ctx.Now };
                ctx.Db.VirtualKeyMcpGrants.Add(stored);
                kept[grant.ServerId] = stored;
            }

            stored.AllowedTools = grant.Tools();
            stored.ApprovalPolicy = grant.ApprovalPolicy;
            stored.AllowResources = grant.AllowResources;
            stored.AllowPrompts = grant.AllowPrompts;
        }

        var after = kept.Values.OrderBy(g => g.McpServerId).Select(McpGrantAudit.Of).ToList();
        await ctx.SaveAsync(user, AuditActions.Update, AuditEntities.McpGrants, keyId, before, after, InvalidationKind.Config, ct);
        return await DtosAsync([.. kept.Values], ct);
    }

    private async Task EnsureKeyAsync(ClaimsPrincipal user, Guid keyId, CancellationToken ct)
    {
        if (!await ctx.Keys(user).AnyAsync(k => k.Id == keyId, ct))
        {
            throw new ApiFaultException(404, "Nyckeln finns inte.");
        }
    }

    private async Task<List<McpGrantDto>> DtosAsync(IReadOnlyList<VirtualKeyMcpGrant> grants, CancellationToken ct)
    {
        var ids = grants.Select(g => g.McpServerId).ToList();
        var names = await ctx.Db.McpServers.AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => (s.Slug, s.DisplayName), ct);
        var tools = (await ctx.Db.McpTools.AsNoTracking().Where(t => ids.Contains(t.McpServerId)).Select(t => new { t.McpServerId, t.Name }).ToListAsync(ct))
            .ToLookup(t => t.McpServerId, t => t.Name);
        return [.. grants.OrderBy(g => names[g.McpServerId].Slug, StringComparer.Ordinal).Select(g =>
        {
            var known = tools[g.McpServerId].ToHashSet(StringComparer.Ordinal);
            return new McpGrantDto(
                g.McpServerId, names[g.McpServerId].Slug, names[g.McpServerId].DisplayName, g.AllowedTools, g.ApprovalPolicy, g.AllowResources, g.AllowPrompts,
                [.. g.AllowedTools.Where(t => t != VirtualKeyMcpGrant.AllTools && !known.Contains(t))]);
        })];
    }
}
