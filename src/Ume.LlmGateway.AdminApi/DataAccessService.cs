using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.AdminApi;

public sealed record DataApiKeyRequest(
    [property: Required, StringLength(200)] string Name,
    [property: Required, MinLength(1), MaxLength(10)] List<string> Permissions,
    [property: StringLength(1000)] string? Description = null,
    DateTimeOffset? ExpiresAt = null) : AdminRequest, IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            yield return new ValidationResult("Enter a name.", [nameof(Name)]);
        }
        if (Permissions?.Any(p => !DataPermissions.IsKnown(p)) == true)
        {
            yield return new ValidationResult($"Unknown permission. Allowed: {string.Join(", ", DataPermissions.All)}.", [nameof(Permissions)]);
        }
    }
}

/// <summary>
/// Data API keys (docs/data-access.md): created, changed, revoked and deleted by gateway admins only. Only the key's hash
/// is stored; the plaintext is returned once on creation. The Data API caches keys briefly
/// (<c>DataApi:KeyCacheSeconds</c>), so no invalidation is published.
/// </summary>
public sealed class DataAccessService(AdminContext ctx, IOptions<DataApiLinkOptions> link)
{
    public DataAccessDto Settings() => new(link.Value.Address, DataPermissions.All);

    public async Task<List<DataApiKeyDto>> ListAsync(CancellationToken ct)
    {
        var keys = await ctx.Db.DataApiKeys.AsNoTracking().OrderBy(k => k.Name).ToListAsync(ct);
        return [.. keys.Select(k => DataApiKeyDto.Of(k, ctx.Now))];
    }

    public async Task<DataApiKeyDto> GetAsync(Guid id, CancellationToken ct) => DataApiKeyDto.Of(await FindAsync(id, ct), ctx.Now);

    public async Task<DataApiKeyCreatedDto> CreateAsync(DataApiKeyRequest input, CancellationToken ct)
    {
        await EnsureValidAsync(input, null, ct);
        var generated = DataApiKeyHasher.Generate();
        var key = new DataApiKey { Name = input.Name.Trim(), Prefix = generated.Prefix, KeyHash = generated.Hash, CreatedAt = ctx.Now, CreatedBy = ctx.Actor };
        Apply(key, input);
        ctx.Db.DataApiKeys.Add(key);
        await ctx.SaveAsync(AuditActions.Create, AuditEntities.DataApiKey, key.Id, null, DataApiKeyAudit.Of(key), [], ct);
        return new DataApiKeyCreatedDto(DataApiKeyDto.Of(key, ctx.Now), generated.PlainText);
    }

    public async Task<DataApiKeyDto> UpdateAsync(Guid id, DataApiKeyRequest input, CancellationToken ct)
    {
        var key = await FindAsync(id, ct);
        if (key.RevokedAt is not null) { throw new ApiFaultException(409, "The key is revoked and cannot be changed."); }
        await EnsureValidAsync(input, id, ct);
        var before = DataApiKeyAudit.Of(key);
        Apply(key, input);
        await ctx.SaveAsync(AuditActions.Update, AuditEntities.DataApiKey, id, before, DataApiKeyAudit.Of(key), [], ct);
        return DataApiKeyDto.Of(key, ctx.Now);
    }

    public async Task<DataApiKeyDto> RevokeAsync(Guid id, CancellationToken ct)
    {
        var key = await FindAsync(id, ct);
        if (key.RevokedAt is not null) { throw new ApiFaultException(409, "The key is already revoked."); }
        var before = DataApiKeyAudit.Of(key);
        key.RevokedAt = ctx.Now;
        await ctx.SaveAsync(AuditActions.Revoke, AuditEntities.DataApiKey, id, before, DataApiKeyAudit.Of(key), [], ct);
        return DataApiKeyDto.Of(key, ctx.Now);
    }

    /// <summary>Only a revoked or expired key can be deleted, so a working integration is never cut off by a delete.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var key = await FindAsync(id, ct);
        if (key.GetStatus(ctx.Now) == KeyStatus.Active) { throw new ApiFaultException(409, "Revoke the key before deleting it."); }
        ctx.Db.DataApiKeys.Remove(key);
        await ctx.SaveAsync(AuditActions.Delete, AuditEntities.DataApiKey, id, DataApiKeyAudit.Of(key), null, [], ct);
    }

    private async Task<DataApiKey> FindAsync(Guid id, CancellationToken ct) =>
        await ctx.Db.DataApiKeys.SingleOrDefaultAsync(k => k.Id == id, ct) ?? throw new ApiFaultException(404, "The key does not exist.");

    private async Task EnsureValidAsync(DataApiKeyRequest input, Guid? id, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var name = input.Name.Trim();
        if (await ctx.Db.DataApiKeys.AnyAsync(k => k.Name == name && k.Id != id, ct))
        {
            errors["name"] = ["There is already a key with that name."];
        }
        if (input.ExpiresAt is { } expires && expires <= ctx.Now)
        {
            errors["expiresAt"] = ["The expiry date must be in the future."];
        }
        if (errors.Count > 0) { throw ApiFaultException.Validation(errors); }
    }

    private static void Apply(DataApiKey key, DataApiKeyRequest input)
    {
        key.Name = input.Name.Trim();
        key.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        // Stored in the canonical order, so the audit log and the UI do not depend on the order they were ticked in.
        key.Permissions = [.. DataPermissions.All.Where(input.Permissions.Contains)];
        key.ExpiresAt = input.ExpiresAt?.ToUniversalTime();
    }
}
