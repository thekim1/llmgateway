using Ume.LlmGateway.AdminApi.Security;

namespace Ume.LlmGateway.AdminApi.DataAccess;

/// <summary>Access to the read-only Data API: Data API keys for BI, management and security integrations. Gateway admins only.</summary>
public static class DataAccessEndpoints
{
    public static void MapDataAccess(this RouteGroupBuilder api)
    {
        var access = api.MapGroup("/data-access").RequireAuthorization(AdminPolicies.Admin).WithTags("Data access");
        access.MapGet("", (DataAccessService service) => TypedResults.Ok(service.Settings()))
            .WithName("GetDataAccess").WithSummary("The Data API's public address and the permissions a key can hold");
        var keys = access.MapGroup("/keys");
        keys.MapGet("", async (DataAccessService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(ct)))
            .WithName("ListDataApiKeys").WithSummary("Data API keys; never their secrets");
        keys.MapGet("/{id:guid}", async (Guid id, DataAccessService service, CancellationToken ct) => TypedResults.Ok(await service.GetAsync(id, ct)))
            .WithName("GetDataApiKey");
        keys.MapPost("", async (DataApiKeyRequest input, DataAccessService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(input, ct);
            return TypedResults.Created($"/api/data-access/keys/{created.Key.Id}", created);
        }).WithName("CreateDataApiKey").WithSummary("Creates a Data API key; the response holds its secret, shown this once");
        keys.MapPut("/{id:guid}", async (Guid id, DataApiKeyRequest input, DataAccessService service, CancellationToken ct) =>
            TypedResults.Ok(await service.UpdateAsync(id, input, ct)))
            .WithName("UpdateDataApiKey").WithSummary("Changes a key's name, description, permissions or expiry");
        keys.MapPost("/{id:guid}/revoke", async (Guid id, DataAccessService service, CancellationToken ct) =>
            TypedResults.Ok(await service.RevokeAsync(id, ct)))
            .WithName("RevokeDataApiKey").WithSummary("Revokes a key; the Data API refuses it within DataApi:KeyCacheSeconds");
        keys.MapDelete("/{id:guid}", async (Guid id, DataAccessService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return TypedResults.NoContent();
        }).WithName("DeleteDataApiKey").WithSummary("Deletes a revoked or expired key");
    }
}
