using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.AdminApi.Auditing;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Security;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi.Keys;

/// <summary>
/// Virtual keys of the teams the user manages (<see cref="AdminContext.ManagedKeys"/>): team admins manage their own teams'
/// keys like a department admin does their departments'. Secrets are returned once, at creation and rotation, and to a
/// gateway admin on an audited reveal.
/// </summary>
public sealed class KeyService(AdminContext ctx, VirtualKeyHasher hasher, CredentialProtector protector)
{
    public async Task<List<KeyDto>> ListAsync(Guid? teamId, Guid? departmentId, KeyStatus? status, CancellationToken ct)
    {
        var query = ctx.ManagedKeys();
        if (teamId is { } team) { query = query.Where(k => k.TeamId == team); }
        if (departmentId is { } department) { query = query.Where(k => k.Team!.DepartmentId == department); }
        var items = await query.OrderBy(k => k.Name).ToListAsync(ct);
        return [.. items.Where(k => status is null || k.GetStatus(ctx.Now) == status).Select(k => KeyDto.Of(k, ctx.Now))];
    }

    public async Task<KeyDto> GetAsync(Guid id, CancellationToken ct) => KeyDto.Of(await FindAsync(id, ct), ctx.Now);

    public async Task<KeyCreatedDto> CreateAsync(KeyRequest input, CancellationToken ct)
    {
        var team = await ctx.KeyTeams().SingleOrDefaultAsync(t => t.Id == input.TeamId, ct) ?? throw new ApiFaultException(404, "The team does not exist.");
        var generated = hasher.Generate();
        var key = new VirtualKey
        {
            Team = team, TeamId = team.Id, Name = input.Name.Trim(), Prefix = generated.Prefix,
            KeyHash = generated.Hash, EncryptedSecret = protector.Protect(generated.PlainText), CreatedAt = ctx.Now, CreatedBy = ctx.Subject,
        };
        Apply(key, input);
        ctx.Db.VirtualKeys.Add(key);
        await ctx.SaveAsync(AuditActions.Create, AuditEntities.VirtualKey, key.Id, null, KeyAudit.Of(key), InvalidationKind.Keys, ct);
        return new KeyCreatedDto(KeyDto.Of(key, ctx.Now), generated.PlainText);
    }

    public async Task<KeyDto> UpdateAsync(Guid id, KeyRequest input, CancellationToken ct)
    {
        var key = await FindAsync(id, ct);
        var before = KeyAudit.Of(key);
        Apply(key, input);
        await ctx.SaveAsync(AuditActions.Update, AuditEntities.VirtualKey, id, before, KeyAudit.Of(key), InvalidationKind.Keys, ct);
        return KeyDto.Of(key, ctx.Now);
    }

    public async Task<KeyRotatedDto> RotateAsync(Guid id, KeyRotationMode mode, CancellationToken ct)
    {
        var old = await FindAsync(id, ct);
        if (!old.IsUsable(ctx.Now) || old.RotatedToKeyId is not null) { throw new ApiFaultException(409, "Only active keys that have not been rotated yet can be rotated."); }
        var before = KeyAudit.Of(old);
        var generated = hasher.Generate();
        var replacement = KeyRotation.Rotate(old, generated, mode, ctx.Now, ctx.Subject);
        replacement.Team = old.Team;
        replacement.EncryptedSecret = protector.Protect(generated.PlainText);
        ctx.Db.VirtualKeys.Add(replacement);
        // Keys: the old key's status changed. Config: key budgets now cover the replacement too.
        await ctx.SaveAsync(AuditActions.Rotate, AuditEntities.VirtualKey, id, before, new KeyRotationAudit(KeyAudit.Of(old), KeyAudit.Of(replacement)),
            [InvalidationKind.Keys, InvalidationKind.Config], ct);
        return new KeyRotatedDto(KeyDto.Of(replacement, ctx.Now), generated.PlainText, KeyDto.Of(old, ctx.Now));
    }

    /// <summary>The key's secret; every reveal or copy is audited with the key's name and prefix.</summary>
    public async Task<KeySecretDto> RevealAsync(Guid id, RevealPurpose purpose, CancellationToken ct)
    {
        var key = await FindAsync(id, ct);
        if (key.RevokedAt is not null) { throw new ApiFaultException(409, "The key is revoked."); }
        var secret = protector.Unprotect(key.EncryptedSecret)
            ?? throw new ApiFaultException(409, "The key was created before key secrets were stored and cannot be shown. Rotate the key to get one that can be shown.");
        await ctx.AuditAsync(purpose == RevealPurpose.Copy ? AuditActions.Copy : AuditActions.Reveal, AuditEntities.VirtualKey, id, new KeyAccessAudit(key.Name, key.Prefix), ct);
        return new KeySecretDto(secret);
    }

    public async Task<KeyDto> RevokeAsync(Guid id, CancellationToken ct)
    {
        var key = await FindAsync(id, ct);
        var before = KeyAudit.Of(key);
        KeyRotation.Revoke(key, ctx.Now);
        await ctx.SaveAsync(AuditActions.Revoke, AuditEntities.VirtualKey, id, before, KeyAudit.Of(key), InvalidationKind.Keys, ct);
        return KeyDto.Of(key, ctx.Now);
    }

    private async Task<VirtualKey> FindAsync(Guid id, CancellationToken ct) =>
        await ctx.ManagedKeys().SingleOrDefaultAsync(k => k.Id == id, ct) ?? throw new ApiFaultException(404, "The key does not exist.");

    private static void Apply(VirtualKey key, KeyRequest input)
    {
        key.Name = input.Name.Trim(); key.Description = input.Description; key.ExpiresAt = input.ExpiresAt?.ToUniversalTime();
        key.AllowedModels = [.. input.AllowedModels]; key.AllowedResidencies = [.. input.AllowedResidencies]; key.PiiPolicy = input.PiiPolicy;
        if (input.AllowedProviders is not null) { key.AllowedProviders = [.. input.AllowedProviders.Select(p => p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)]; }
        if (input.AttachmentPolicy is { } attachments) { key.AttachmentPolicy = attachments; }
        if (input.AllowProviderTools is { } tools) { key.AllowProviderTools = tools; }
        if (input.AllowProviderStorage is { } storage) { key.AllowProviderStorage = storage; }
        if (input.AllowedIpRanges is not null) { NetworkRanges.Normalize(input.AllowedIpRanges, out var ranges); key.AllowedIpRanges = ranges; }
        key.RequestsPerMinute = input.RequestsPerMinute; key.TokensPerMinute = input.TokensPerMinute; key.IsEnabled = input.IsEnabled;
        key.MaxConcurrentRequests = input.MaxConcurrentRequests;
        if (input.RequestPolicy is { } policy) { key.RequestPolicy = policy.IsEmpty ? null : policy; }
        if (input.ContentGuardPolicy is { } guard) { key.ContentGuardPolicy = guard; }
    }
}
