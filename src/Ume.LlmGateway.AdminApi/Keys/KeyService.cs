using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.AdminApi.Auditing;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Keys;
using Ume.LlmGateway.Domain.Networking;
using Ume.LlmGateway.Infrastructure.Security;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi.Keys;

/// <summary>
/// Virtual keys of the teams the user manages (<see cref="AdminContext.ManagedKeys"/>): team admins manage their own teams'
/// keys like a department admin does their departments'. Secrets are returned once, at creation and rotation, and to a
/// gateway admin on an audited reveal. A key can belong to an integration (docs/integrations.md): an <em>assigned</em> key is made by
/// its team's managers and has no usable secret (the platform uses it by reference); a <em>credential</em> key is the platform's
/// own, made and changed by gateway admins in the integration's owner team.
/// </summary>
public sealed class KeyService(AdminContext ctx, VirtualKeyHasher hasher, CredentialProtector protector)
{
    public async Task<List<KeyDto>> ListAsync(Guid? teamId, Guid? departmentId, KeyStatus? status, CancellationToken ct, Guid? integrationId = null)
    {
        var query = ctx.ManagedKeys();
        if (teamId is { } team) { query = query.Where(k => k.TeamId == team); }
        if (integrationId is { } integration) { query = query.Where(k => k.IntegrationId == integration); }
        if (departmentId is { } department) { query = query.Where(k => k.Team!.DepartmentId == department); }
        if (status is { } wanted) { query = query.Where(VirtualKey.HasStatus(wanted, ctx.Now)); }
        var items = await query.OrderBy(k => k.Name).ToListAsync(ct);
        return [.. items.Select(k => KeyDto.Of(k, ctx.Now))];
    }

    public async Task<KeyDto> GetAsync(Guid id, CancellationToken ct) => KeyDto.Of(await FindAsync(id, ct), ctx.Now);

    public async Task<KeyCreatedDto> CreateAsync(KeyRequest input, CancellationToken ct)
    {
        var team = await ctx.KeyTeams().SingleOrDefaultAsync(t => t.Id == input.TeamId, ct) ?? throw NotFound.Team();
        var (integration, role) = await IntegrationForNewKeyAsync(input, team, ct);
        var generated = hasher.Generate();
        // An assigned key is used by reference only: its secret is not kept or shown, so nobody can present it.
        var assigned = role == IntegrationRole.Assigned;
        var key = new VirtualKey
        {
            Team = team, TeamId = team.Id, Name = input.Name.Trim(), Prefix = generated.Prefix,
            KeyHash = generated.Hash, EncryptedSecret = assigned ? null : protector.Protect(generated.PlainText), CreatedAt = ctx.Now, CreatedBy = ctx.Subject,
            Integration = integration, IntegrationId = integration?.Id, IntegrationRole = role,
        };
        Apply(key, input);
        if (role == IntegrationRole.Credential && input.IntegrationScopes is { } scopes) { key.IntegrationScopes = Scopes(scopes); }
        ctx.Db.VirtualKeys.Add(key);
        await ctx.SaveAsync(AuditActions.Create, AuditEntities.VirtualKey, key.Id, null, KeyAudit.Of(key), InvalidationKind.Keys, ct);
        return new KeyCreatedDto(KeyDto.Of(key, ctx.Now), assigned ? null : generated.PlainText);
    }

    public async Task<KeyDto> UpdateAsync(Guid id, KeyRequest input, CancellationToken ct)
    {
        var key = await FindAsync(id, ct);
        EnsureIntegrationUnchanged(key, input);
        var before = KeyAudit.Of(key);
        Apply(key, input);
        if (input.IntegrationScopes is { } scopes && !Scopes(scopes).SequenceEqual(key.IntegrationScopes, StringComparer.Ordinal))
        {
            if (key.IntegrationRole != IntegrationRole.Credential) { throw Field("integrationScopes", "Only an integration's credential keys hold scopes."); }
            key.IntegrationScopes = Scopes(scopes);
        }
        await ctx.SaveAsync(AuditActions.Update, AuditEntities.VirtualKey, id, before, KeyAudit.Of(key), InvalidationKind.Keys, ct);
        return KeyDto.Of(key, ctx.Now);
    }

    public async Task<KeyRotatedDto> RotateAsync(Guid id, KeyRotationMode mode, CancellationToken ct)
    {
        var old = await FindAsync(id, ct);
        if (old.IntegrationRole == IntegrationRole.Assigned) { throw new ApiFaultException(409, AssignedHasNoSecret); }
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
        if (key.IntegrationRole == IntegrationRole.Assigned) { throw new ApiFaultException(409, AssignedHasNoSecret); }
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
        await ctx.ManagedKeys().SingleOrDefaultAsync(k => k.Id == id, ct) ?? throw NotFound.Key();

    private const string AssignedHasNoSecret =
        "The key is assigned to an integration: it has no secret to show or rotate, and is used through the integration. Revoke it and create a new one if needed.";

    /// <summary>
    /// The integration and role of a new key, checked: an active integration; a credential key only by a gateway admin and in
    /// the integration's owner team; scopes only on a credential key.
    /// </summary>
    private async Task<(Integration? Integration, IntegrationRole Role)> IntegrationForNewKeyAsync(KeyRequest input, Team team, CancellationToken ct)
    {
        if (input.IntegrationId is not { } integrationId)
        {
            if (input.IntegrationScopes is { Count: > 0 }) { throw Field("integrationScopes", "Only an integration's credential keys hold scopes."); }
            return (null, IntegrationRole.None);
        }

        var role = input.IntegrationRole is null or IntegrationRole.None ? IntegrationRole.Assigned : input.IntegrationRole.Value;
        var integration = await ctx.Db.Integrations.SingleOrDefaultAsync(i => i.Id == integrationId, ct) ?? throw NotFound.Integration();
        if (!integration.IsActive) { throw new ApiFaultException(409, "The integration is deactivated; keys cannot be added to it."); }
        if (role == IntegrationRole.Credential)
        {
            if (!ctx.IsAdmin) { throw new ApiFaultException(403, "Only gateway admins can create an integration's credential keys."); }
            if (team.Id != integration.OwnerTeamId) { throw Field("teamId", "A credential key belongs to the integration's owner team."); }
        }
        else if (input.IntegrationScopes is { Count: > 0 })
        {
            throw Field("integrationScopes", "Only an integration's credential keys hold scopes.");
        }

        return (integration, role);
    }

    /// <summary>
    /// An update cannot move a key into, out of or between integrations, and only gateway admins change a credential key or
    /// its scopes: its settings apply to every team the platform serves, across departments.
    /// </summary>
    private void EnsureIntegrationUnchanged(VirtualKey key, KeyRequest input)
    {
        if ((input.IntegrationId is { } integrationId && integrationId != key.IntegrationId) ||
            (input.IntegrationRole is { } role && role != key.IntegrationRole))
        {
            throw Field("integrationId", "A key's integration is set when it is created and cannot be changed.");
        }

        var scopesChanged = input.IntegrationScopes is { } scopes && !Scopes(scopes).SequenceEqual(key.IntegrationScopes, StringComparer.Ordinal);
        if ((key.IntegrationRole == IntegrationRole.Credential || scopesChanged) && !ctx.IsAdmin)
        {
            throw new ApiFaultException(403, "Only gateway admins can change an integration's credential keys.");
        }
    }

    private static List<string> Scopes(IEnumerable<string> scopes) => [.. scopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static ApiFaultException Field(string field, string message) =>
        ApiFaultException.Validation(new Dictionary<string, string[]> { [field] = [message] });

    private static void Apply(VirtualKey key, KeyRequest input)
    {
        key.Name = input.Name.Trim(); key.Description = input.Description; key.ExpiresAt = input.ExpiresAt?.ToUniversalTime();
        key.AllowedModels = [.. input.AllowedModels]; key.AllowedResidencies = [.. input.AllowedResidencies]; key.PiiPolicy = input.PiiPolicy;
        if (input.AllowedProviders is not null) { key.AllowedProviders = [.. input.AllowedProviders.Select(p => p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)]; }
        if (input.AttachmentPolicy is { } attachments) { key.AttachmentPolicy = attachments; }
        if (input.AllowProviderTools is { } tools) { key.AllowProviderTools = tools; }
        if (input.AllowProviderStorage is { } storage) { key.AllowProviderStorage = storage; }
        if (input.RequireMaxResidency is { } requireCeiling) { key.RequireMaxResidency = requireCeiling; }
        if (input.AllowedIpRanges is not null) { NetworkRanges.Normalize(input.AllowedIpRanges, out var ranges); key.AllowedIpRanges = ranges; }
        key.RequestsPerMinute = input.RequestsPerMinute; key.TokensPerMinute = input.TokensPerMinute; key.IsEnabled = input.IsEnabled;
        key.MaxConcurrentRequests = input.MaxConcurrentRequests;
        if (input.RequestPolicy is { } policy) { key.RequestPolicy = policy.IsEmpty ? null : policy; }
        if (input.ContentGuardPolicy is { } guard) { key.ContentGuardPolicy = guard; }
    }
}
