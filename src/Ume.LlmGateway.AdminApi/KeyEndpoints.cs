using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
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
    [property: MaxLength(100)] List<string>? AllowedProviders = null) : AdminRequest, IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (AllowedModels?.Any(m => string.IsNullOrWhiteSpace(m) || m.Length > 200) == true)
        {
            yield return new ValidationResult("Modellnamn måste vara 1–200 tecken.", [nameof(AllowedModels)]);
        }
        if (AllowedProviders?.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 100) == true)
        {
            yield return new ValidationResult("Leverantörsnamn måste vara 1–100 tecken.", [nameof(AllowedProviders)]);
        }
        if (AllowedResidencies?.Any(r => !Enum.IsDefined(r)) == true)
        {
            yield return new ValidationResult("Dataplatsen är ogiltig.", [nameof(AllowedResidencies)]);
        }
    }
}
public sealed record RotateRequest([property: EnumDataType(typeof(KeyRotationMode))] KeyRotationMode Mode = KeyRotationMode.RevokeImmediately) : AdminRequest;

public enum RevealPurpose { Reveal, Copy }
public sealed record RevealRequest([property: EnumDataType(typeof(RevealPurpose))] RevealPurpose Purpose = RevealPurpose.Reveal) : AdminRequest;

public static class KeyEndpoints
{
    public static object KeyDto(VirtualKey k, DateTimeOffset now) => new
    {
        k.Id, k.TeamId, teamName = k.Team?.Name, departmentId = k.Team?.DepartmentId,
        departmentName = k.Team?.Department?.Name, k.Name, k.Description, k.Prefix,
        status = k.GetStatus(now), k.IsEnabled, k.CreatedAt, k.CreatedBy, k.ExpiresAt,
        k.RevokedAt, k.GraceUntil, k.LastUsedAt, k.AllowedModels, k.AllowedResidencies, k.AllowedProviders,
        k.PiiPolicy, k.RequestsPerMinute, k.TokensPerMinute, k.RotatedToKeyId,
        canReveal = k.EncryptedSecret is not null,
    };

    public static void MapKeys(this RouteGroupBuilder api)
    {
        var keys = api.MapGroup("/keys").RequireAuthorization("manage");
        keys.MapGet("", async (Guid? teamId, Guid? departmentId, KeyStatus? status, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var query = ctx.Keys(user);
            if (teamId is { } team) { query = query.Where(k => k.TeamId == team); }
            if (departmentId is { } department) { query = query.Where(k => k.Team!.DepartmentId == department); }
            var items = await query.OrderBy(k => k.Name).ToListAsync(ct);
            return items.Where(k => status is null || k.GetStatus(ctx.Now) == status).Select(k => KeyDto(k, ctx.Now));
        });
        keys.MapGet("/{id:guid}", async (Guid id, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok(KeyDto(await FindAsync(id, ctx, user, ct), ctx.Now)));
        keys.MapPost("", async (KeyRequest input, AdminContext ctx, ClaimsPrincipal user, VirtualKeyHasher hasher, CredentialProtector protector, CancellationToken ct) =>
        {
            var team = await ctx.Teams(user).SingleOrDefaultAsync(t => t.Id == input.TeamId, ct) ?? throw new AdminFaultException(404, "Teamet finns inte.");
            var generated = hasher.Generate();
            var key = new VirtualKey
            {
                Team = team, TeamId = team.Id, Name = input.Name.Trim(), Prefix = generated.Prefix,
                KeyHash = generated.Hash, EncryptedSecret = protector.Protect(generated.PlainText), CreatedAt = ctx.Now, CreatedBy = user.FindFirstValue("sub"),
            };
            Apply(key, input);
            ctx.Db.VirtualKeys.Add(key);
            await ctx.SaveAsync(user, "create", "VirtualKey", key.Id, null, KeyDto(key, ctx.Now), InvalidationKind.Keys, ct);
            return Results.Created($"/api/keys/{key.Id}", new { key = KeyDto(key, ctx.Now), secret = generated.PlainText });
        });
        keys.MapPut("/{id:guid}", async (Guid id, KeyRequest input, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var key = await FindAsync(id, ctx, user, ct);
            var before = KeyDto(key, ctx.Now);
            Apply(key, input);
            await ctx.SaveAsync(user, "update", "VirtualKey", id, before, KeyDto(key, ctx.Now), InvalidationKind.Keys, ct);
            return Results.Ok(KeyDto(key, ctx.Now));
        });
        keys.MapPost("/{id:guid}/rotate", async (Guid id, RotateRequest input, AdminContext ctx, ClaimsPrincipal user, VirtualKeyHasher hasher, CredentialProtector protector, CancellationToken ct) =>
        {
            var old = await FindAsync(id, ctx, user, ct);
            if (!old.IsUsable(ctx.Now) || old.RotatedToKeyId is not null) { throw new AdminFaultException(409, "Endast aktiva, ännu inte roterade nycklar kan roteras."); }
            var before = KeyDto(old, ctx.Now);
            var generated = hasher.Generate();
            var replacement = KeyRotation.Rotate(old, generated, input.Mode, ctx.Now, user.FindFirstValue("sub"));
            replacement.Team = old.Team;
            replacement.EncryptedSecret = protector.Protect(generated.PlainText);
            ctx.Db.VirtualKeys.Add(replacement);
            await ctx.SaveAsync(user, "rotate", "VirtualKey", id, before, new { previousKey = KeyDto(old, ctx.Now), key = KeyDto(replacement, ctx.Now) }, InvalidationKind.Keys, ct);
            await ctx.SaveAsync(user, "invalidate", "Config", id, null, null, InvalidationKind.Config, ct);
            return Results.Ok(new { key = KeyDto(replacement, ctx.Now), secret = generated.PlainText, previousKey = KeyDto(old, ctx.Now) });
        });
        // Only gateway-admin. The secret is masked in the UI by default; every reveal or copy is audited.
        keys.MapPost("/{id:guid}/reveal", async (Guid id, RevealRequest input, AdminContext ctx, ClaimsPrincipal user, CredentialProtector protector, CancellationToken ct) =>
        {
            var key = await FindAsync(id, ctx, user, ct);
            if (key.RevokedAt is not null) { throw new AdminFaultException(409, "Nyckeln är återkallad."); }
            var secret = protector.Unprotect(key.EncryptedSecret)
                ?? throw new AdminFaultException(409, "Nyckeln skapades innan lagring av nyckeltext infördes och kan inte visas. Rotera nyckeln för att få en som kan visas.");
            await ctx.AuditAsync(user, input.Purpose == RevealPurpose.Copy ? "copy" : "reveal", "VirtualKey", id, new { key.Name, key.Prefix }, ct);
            return Results.Ok(new { secret });
        }).RequireAuthorization("admin");
        keys.MapPost("/{id:guid}/revoke", async (Guid id, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var key = await FindAsync(id, ctx, user, ct);
            var before = KeyDto(key, ctx.Now);
            KeyRotation.Revoke(key, ctx.Now);
            await ctx.SaveAsync(user, "revoke", "VirtualKey", id, before, KeyDto(key, ctx.Now), InvalidationKind.Keys, ct);
            return Results.Ok(KeyDto(key, ctx.Now));
        });
    }

    private static async Task<VirtualKey> FindAsync(Guid id, AdminContext ctx, ClaimsPrincipal user, CancellationToken ct) =>
        await ctx.Keys(user).SingleOrDefaultAsync(k => k.Id == id, ct) ?? throw new AdminFaultException(404, "Nyckeln finns inte.");
    private static void Apply(VirtualKey key, KeyRequest input)
    {
        key.Name = input.Name.Trim(); key.Description = input.Description; key.ExpiresAt = input.ExpiresAt?.ToUniversalTime();
        key.AllowedModels = [.. input.AllowedModels]; key.AllowedResidencies = [.. input.AllowedResidencies]; if (input.AllowedProviders is not null) { key.AllowedProviders = [.. input.AllowedProviders.Select(p => p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)]; } key.PiiPolicy = input.PiiPolicy;
        key.RequestsPerMinute = input.RequestsPerMinute; key.TokensPerMinute = input.TokensPerMinute; key.IsEnabled = input.IsEnabled;
    }
}
