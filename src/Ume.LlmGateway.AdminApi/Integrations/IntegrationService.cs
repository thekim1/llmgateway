using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.AdminApi.Auditing;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Keys;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi.Integrations;

/// <summary>
/// Integrations (docs/integrations.md): other systems that call the gateway for the teams they serve. Gateway admins
/// create, change and delete them; every key manager can list them to assign a key. A change publishes
/// <see cref="InvalidationKind.Keys"/>, because cached keys carry their integration.
/// </summary>
public sealed class IntegrationService(AdminContext ctx)
{
    public async Task<List<IntegrationDto>> ListAsync(CancellationToken ct)
    {
        var integrations = await ctx.Db.Integrations.AsNoTracking().Include(i => i.OwnerTeam!).ThenInclude(t => t.Department)
            .OrderBy(i => i.Name).ToListAsync(ct);
        var counts = await KeyCountsAsync(ct);
        return [.. integrations.Select(i => Dto(i, counts))];
    }

    public async Task<IntegrationDto> GetAsync(Guid id, CancellationToken ct)
    {
        var integration = await FindAsync(id, ct);
        return Dto(integration, await KeyCountsAsync(ct, id));
    }

    public static IntegrationOptionsDto Options() => new(IntegrationScopes.All, [IntegrationRole.Credential, IntegrationRole.Assigned]);

    public async Task<IntegrationDto> CreateAsync(IntegrationRequest input, CancellationToken ct)
    {
        var team = await OwnerTeamAsync(input.OwnerTeamId, ct);
        var name = await UniqueNameAsync(input.Name, null, ct);
        var integration = new Integration
        {
            Name = name, Description = Clean(input.Description), OwnerTeamId = team.Id, OwnerTeam = team, IsActive = input.IsActive,
            CreatedAt = ctx.Now, CreatedBy = ctx.Subject,
        };
        ctx.Db.Integrations.Add(integration);
        await ctx.SaveAsync(AuditActions.Create, AuditEntities.Integration, integration.Id, null, IntegrationAudit.Of(integration), InvalidationKind.Keys, ct);
        return IntegrationDto.Of(integration, 0, 0);
    }

    /// <summary>
    /// Changes name, description, owner team or state. The owner team can change only while the integration has no
    /// credential keys, because those belong to the owner team.
    /// </summary>
    public async Task<IntegrationDto> UpdateAsync(Guid id, IntegrationRequest input, CancellationToken ct)
    {
        var integration = await FindAsync(id, ct);
        var before = IntegrationAudit.Of(integration);
        if (input.OwnerTeamId != integration.OwnerTeamId)
        {
            if (await ctx.Db.VirtualKeys.AnyAsync(k => k.IntegrationId == id && k.IntegrationRole == IntegrationRole.Credential && k.RevokedAt == null, ct))
            {
                throw new ApiFaultException(409, "The owner team cannot change while the integration has credential keys. Revoke them first.");
            }

            var team = await OwnerTeamAsync(input.OwnerTeamId, ct);
            integration.OwnerTeamId = team.Id;
            integration.OwnerTeam = team;
        }

        integration.Name = await UniqueNameAsync(input.Name, id, ct);
        integration.Description = Clean(input.Description);
        integration.IsActive = input.IsActive;
        await ctx.SaveAsync(AuditActions.Update, AuditEntities.Integration, id, before, IntegrationAudit.Of(integration), InvalidationKind.Keys, ct);
        return Dto(integration, await KeyCountsAsync(ct, id));
    }

    /// <summary>Deletes an integration whose keys are all revoked; the revoked keys keep their history and lose the link.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var integration = await FindAsync(id, ct);
        if (await ctx.Db.VirtualKeys.AnyAsync(k => k.IntegrationId == id && k.RevokedAt == null, ct))
        {
            throw new ApiFaultException(409, "The integration has keys that are not revoked and cannot be deleted. Deactivate it instead, or revoke its keys first.");
        }

        // Tracked, so the keys lose the link in the same transaction that deletes the integration.
        foreach (var key in await ctx.Db.VirtualKeys.Where(k => k.IntegrationId == id).ToListAsync(ct))
        {
            key.IntegrationId = null;
        }

        ctx.Db.Integrations.Remove(integration);
        await ctx.SaveAsync(AuditActions.Delete, AuditEntities.Integration, id, IntegrationAudit.Of(integration), null, InvalidationKind.Keys, ct);
    }

    private async Task<Integration> FindAsync(Guid id, CancellationToken ct) =>
        await ctx.Db.Integrations.Include(i => i.OwnerTeam!).ThenInclude(t => t.Department).SingleOrDefaultAsync(i => i.Id == id, ct)
        ?? throw NotFound.Integration();

    private async Task<Team> OwnerTeamAsync(Guid id, CancellationToken ct) =>
        await ctx.Db.Teams.Include(t => t.Department).SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw NotFound.Team();

    private async Task<string> UniqueNameAsync(string name, Guid? id, CancellationToken ct)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            throw ApiFaultException.Validation(new Dictionary<string, string[]> { ["name"] = ["Enter a name."] });
        }

        if (await ctx.Db.Integrations.AnyAsync(i => i.Name == trimmed && i.Id != id, ct))
        {
            throw ApiFaultException.Validation(new Dictionary<string, string[]> { ["name"] = ["There is already an integration with that name."] });
        }

        return trimmed;
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private async Task<Dictionary<(Guid, IntegrationRole), int>> KeyCountsAsync(CancellationToken ct, Guid? id = null) =>
        await ctx.Db.VirtualKeys.AsNoTracking()
            .Where(k => k.IntegrationId != null && k.RevokedAt == null && (id == null || k.IntegrationId == id))
            .GroupBy(k => new { Id = k.IntegrationId!.Value, k.IntegrationRole })
            .Select(g => new { g.Key.Id, g.Key.IntegrationRole, Count = g.Count() })
            .ToDictionaryAsync(x => (x.Id, x.IntegrationRole), x => x.Count, ct);

    private static IntegrationDto Dto(Integration i, Dictionary<(Guid, IntegrationRole), int> counts) => IntegrationDto.Of(i,
        counts.GetValueOrDefault((i.Id, IntegrationRole.Credential)), counts.GetValueOrDefault((i.Id, IntegrationRole.Assigned)));
}
