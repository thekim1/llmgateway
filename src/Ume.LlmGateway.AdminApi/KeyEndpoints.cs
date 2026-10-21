using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Security;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi;

public sealed record KeyRequest(
    Guid TeamId,
    [property: Required, StringLength(200)] string Name,
    [property: Required, MaxLength(1000)] List<string> AllowedModels,
    [property: Required, MaxLength(3)] List<DataResidency> AllowedResidencies,
    [property: EnumDataType(typeof(PiiPolicy))] PiiPolicy PiiPolicy,
    [property: StringLength(1000)] string? Description = null,
    DateTimeOffset? ExpiresAt = null,
    [property: Range(1, int.MaxValue)] int? RequestsPerMinute = null,
    [property: Range(1, int.MaxValue)] int? TokensPerMinute = null,
    bool IsEnabled = true,
    [property: MaxLength(100)] List<string>? AllowedProviders = null,
    [property: EnumDataType(typeof(AttachmentPolicy))] AttachmentPolicy? AttachmentPolicy = null,
    [property: MaxLength(NetworkRanges.MaxEntries)] List<string>? AllowedIpRanges = null,
    bool? AllowProviderTools = null,
    bool? AllowProviderStorage = null) : AdminRequest, IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (AllowedModels?.Any(m => string.IsNullOrWhiteSpace(m) || m.Length > 200) == true)
        {
            yield return new ValidationResult("Model names must be 1–200 characters.", [nameof(AllowedModels)]);
        }
        if (AllowedProviders?.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 100) == true)
        {
            yield return new ValidationResult("Provider names must be 1–100 characters.", [nameof(AllowedProviders)]);
        }
        if (AllowedIpRanges is not null && NetworkRanges.Normalize(AllowedIpRanges, out _) is { } rangeError)
        {
            yield return new ValidationResult(rangeError, [nameof(AllowedIpRanges)]);
        }
        if (AllowedResidencies?.Any(r => !Enum.IsDefined(r)) == true)
        {
            yield return new ValidationResult("The data residency is invalid.", [nameof(AllowedResidencies)]);
        }
    }
}
public sealed record RotateRequest([property: EnumDataType(typeof(KeyRotationMode))] KeyRotationMode Mode = KeyRotationMode.RevokeImmediately) : AdminRequest;

public enum RevealPurpose { Reveal, Copy }
public sealed record RevealRequest([property: EnumDataType(typeof(RevealPurpose))] RevealPurpose Purpose = RevealPurpose.Reveal) : AdminRequest;

public static class KeyEndpoints
{
    public static void MapKeys(this RouteGroupBuilder api)
    {
        var keys = api.MapGroup("/keys").RequireAuthorization(AdminPolicies.Manage).WithTags("Keys");
        keys.MapGet("", async (Guid? teamId, Guid? departmentId, KeyStatus? status, AdminContext ctx, CancellationToken ct) =>
        {
            var query = ctx.Keys();
            if (teamId is { } team) { query = query.Where(k => k.TeamId == team); }
            if (departmentId is { } department) { query = query.Where(k => k.Team!.DepartmentId == department); }
            var items = await query.OrderBy(k => k.Name).ToListAsync(ct);
            return TypedResults.Ok(items.Where(k => status is null || k.GetStatus(ctx.Now) == status).Select(k => KeyDto.Of(k, ctx.Now)).ToList());
        }).WithName("ListKeys").WithSummary("Keys in the user's departments; never their secrets");
        keys.MapGet("/{id:guid}", async (Guid id, AdminContext ctx, CancellationToken ct) =>
            TypedResults.Ok(KeyDto.Of(await FindAsync(id, ctx, ct), ctx.Now)))
            .WithName("GetKey");
        keys.MapPost("", async (KeyRequest input, AdminContext ctx, VirtualKeyHasher hasher, CredentialProtector protector, CancellationToken ct) =>
        {
            var team = await ctx.Teams().SingleOrDefaultAsync(t => t.Id == input.TeamId, ct) ?? throw new ApiFaultException(404, "The team does not exist.");
            var generated = hasher.Generate();
            var key = new VirtualKey
            {
                Team = team, TeamId = team.Id, Name = input.Name.Trim(), Prefix = generated.Prefix,
                KeyHash = generated.Hash, EncryptedSecret = protector.Protect(generated.PlainText), CreatedAt = ctx.Now, CreatedBy = ctx.Subject,
            };
            Apply(key, input);
            ctx.Db.VirtualKeys.Add(key);
            await ctx.SaveAsync(AuditActions.Create, AuditEntities.VirtualKey, key.Id, null, KeyAudit.Of(key), InvalidationKind.Keys, ct);
            return TypedResults.Created($"/api/keys/{key.Id}", new KeyCreatedDto(KeyDto.Of(key, ctx.Now), generated.PlainText));
        }).WithName("CreateKey").WithSummary("Creates a key; the response holds its secret, shown this once");
        keys.MapPut("/{id:guid}", async (Guid id, KeyRequest input, AdminContext ctx, CancellationToken ct) =>
        {
            var key = await FindAsync(id, ctx, ct);
            var before = KeyAudit.Of(key);
            Apply(key, input);
            await ctx.SaveAsync(AuditActions.Update, AuditEntities.VirtualKey, id, before, KeyAudit.Of(key), InvalidationKind.Keys, ct);
            return TypedResults.Ok(KeyDto.Of(key, ctx.Now));
        }).WithName("UpdateKey");
        keys.MapPost("/{id:guid}/rotate", async (Guid id, RotateRequest input, AdminContext ctx, VirtualKeyHasher hasher, CredentialProtector protector, CancellationToken ct) =>
        {
            var old = await FindAsync(id, ctx, ct);
            if (!old.IsUsable(ctx.Now) || old.RotatedToKeyId is not null) { throw new ApiFaultException(409, "Only active keys that have not been rotated yet can be rotated."); }
            var before = KeyAudit.Of(old);
            var generated = hasher.Generate();
            var replacement = KeyRotation.Rotate(old, generated, input.Mode, ctx.Now, ctx.Subject);
            replacement.Team = old.Team;
            replacement.EncryptedSecret = protector.Protect(generated.PlainText);
            ctx.Db.VirtualKeys.Add(replacement);
            // Keys: the old key's status changed. Config: key budgets now cover the replacement too.
            await ctx.SaveAsync(AuditActions.Rotate, AuditEntities.VirtualKey, id, before, new KeyRotationAudit(KeyAudit.Of(old), KeyAudit.Of(replacement)),
                [InvalidationKind.Keys, InvalidationKind.Config], ct);
            return TypedResults.Ok(new KeyRotatedDto(KeyDto.Of(replacement, ctx.Now), generated.PlainText, KeyDto.Of(old, ctx.Now)));
        }).WithName("RotateKey").WithSummary("Replaces a key; the response holds the new secret, shown this once");
        // Only gateway-admin. The secret is masked in the UI by default; every reveal or copy is audited.
        keys.MapPost("/{id:guid}/reveal", async (Guid id, RevealRequest input, AdminContext ctx, CredentialProtector protector, CancellationToken ct) =>
        {
            var key = await FindAsync(id, ctx, ct);
            if (key.RevokedAt is not null) { throw new ApiFaultException(409, "The key is revoked."); }
            var secret = protector.Unprotect(key.EncryptedSecret)
                ?? throw new ApiFaultException(409, "The key was created before key secrets were stored and cannot be shown. Rotate the key to get one that can be shown.");
            await ctx.AuditAsync(input.Purpose == RevealPurpose.Copy ? AuditActions.Copy : AuditActions.Reveal, AuditEntities.VirtualKey, id, new KeyAccessAudit(key.Name, key.Prefix), ct);
            return TypedResults.Ok(new KeySecretDto(secret));
        }).RequireAuthorization(AdminPolicies.Admin).WithName("RevealKey").WithSummary("Returns a key's secret (audited)");
        keys.MapPost("/{id:guid}/revoke", async (Guid id, AdminContext ctx, CancellationToken ct) =>
        {
            var key = await FindAsync(id, ctx, ct);
            var before = KeyAudit.Of(key);
            KeyRotation.Revoke(key, ctx.Now);
            await ctx.SaveAsync(AuditActions.Revoke, AuditEntities.VirtualKey, id, before, KeyAudit.Of(key), InvalidationKind.Keys, ct);
            return TypedResults.Ok(KeyDto.Of(key, ctx.Now));
        }).WithName("RevokeKey");
    }

    private static async Task<VirtualKey> FindAsync(Guid id, AdminContext ctx, CancellationToken ct) =>
        await ctx.Keys().SingleOrDefaultAsync(k => k.Id == id, ct) ?? throw new ApiFaultException(404, "The key does not exist.");
    private static void Apply(VirtualKey key, KeyRequest input)
    {
        key.Name = input.Name.Trim(); key.Description = input.Description; key.ExpiresAt = input.ExpiresAt?.ToUniversalTime();
        key.AllowedModels = [.. input.AllowedModels]; key.AllowedResidencies = [.. input.AllowedResidencies]; if (input.AllowedProviders is not null) { key.AllowedProviders = [.. input.AllowedProviders.Select(p => p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)]; } key.PiiPolicy = input.PiiPolicy;
        if (input.AttachmentPolicy is { } attachments) { key.AttachmentPolicy = attachments; }
        if (input.AllowProviderTools is { } tools) { key.AllowProviderTools = tools; }
        if (input.AllowProviderStorage is { } storage) { key.AllowProviderStorage = storage; }
        if (input.AllowedIpRanges is not null) { NetworkRanges.Normalize(input.AllowedIpRanges, out var ranges); key.AllowedIpRanges = ranges; }
        key.RequestsPerMinute = input.RequestsPerMinute; key.TokensPerMinute = input.TokensPerMinute; key.IsEnabled = input.IsEnabled;
    }
}
