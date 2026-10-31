using Ume.LlmGateway.AdminApi.Security;
using Ume.LlmGateway.Domain;

namespace Ume.LlmGateway.AdminApi.Keys;

/// <summary>Virtual keys (see <see cref="KeyService"/>).</summary>
public static class KeyEndpoints
{
    public static void MapKeys(this RouteGroupBuilder api)
    {
        var keys = api.MapGroup("/keys").RequireAuthorization(AdminPolicies.ManageKeys).WithTags("Keys");
        keys.MapGet("", async (Guid? teamId, Guid? departmentId, KeyStatus? status, KeyService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ListAsync(teamId, departmentId, status, ct)))
            .WithName("ListKeys").WithSummary("Keys the user manages (their departments, or their teams for a team admin); never their secrets");
        keys.MapGet("/{id:guid}", async (Guid id, KeyService service, CancellationToken ct) => TypedResults.Ok(await service.GetAsync(id, ct)))
            .WithName("GetKey");
        keys.MapPost("", async (KeyRequest input, KeyService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(input, ct);
            return TypedResults.Created($"/api/keys/{created.Key.Id}", created);
        }).WithName("CreateKey").WithSummary("Creates a key; the response holds its secret, shown this once");
        keys.MapPut("/{id:guid}", async (Guid id, KeyRequest input, KeyService service, CancellationToken ct) =>
            TypedResults.Ok(await service.UpdateAsync(id, input, ct)))
            .WithName("UpdateKey");
        keys.MapPost("/{id:guid}/rotate", async (Guid id, RotateRequest input, KeyService service, CancellationToken ct) =>
            TypedResults.Ok(await service.RotateAsync(id, input.Mode, ct)))
            .WithName("RotateKey").WithSummary("Replaces a key; the response holds the new secret, shown this once");
        // Only gateway-admin. The secret is masked in the UI by default.
        keys.MapPost("/{id:guid}/reveal", async (Guid id, RevealRequest input, KeyService service, CancellationToken ct) =>
            TypedResults.Ok(await service.RevealAsync(id, input.Purpose, ct)))
            .RequireAuthorization(AdminPolicies.Admin).WithName("RevealKey").WithSummary("Returns a key's secret (audited)");
        keys.MapPost("/{id:guid}/revoke", async (Guid id, KeyService service, CancellationToken ct) =>
            TypedResults.Ok(await service.RevokeAsync(id, ct)))
            .WithName("RevokeKey");
    }
}
