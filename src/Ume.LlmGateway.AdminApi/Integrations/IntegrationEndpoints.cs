using Ume.LlmGateway.AdminApi.Security;

namespace Ume.LlmGateway.AdminApi.Integrations;

/// <summary>Integrations (see <see cref="IntegrationService"/>): listed by every key manager, changed by gateway admins.</summary>
public static class IntegrationEndpoints
{
    public static void MapIntegrations(this RouteGroupBuilder api)
    {
        var integrations = api.MapGroup("/integrations").RequireAuthorization(AdminPolicies.ManageKeys).WithTags("Integrations");
        integrations.MapGet("", async (IntegrationService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(ct)))
            .WithName("ListIntegrations").WithSummary("Integrations, for assigning a key to one");
        integrations.MapGet("/options", () => TypedResults.Ok(IntegrationService.Options()))
            .WithName("GetIntegrationOptions").WithSummary("The roles a key can have in an integration and the scopes a credential key can hold");
        integrations.MapGet("/{id:guid}", async (Guid id, IntegrationService service, CancellationToken ct) => TypedResults.Ok(await service.GetAsync(id, ct)))
            .WithName("GetIntegration");
        integrations.MapPost("", async (IntegrationRequest input, IntegrationService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(input, ct);
            return TypedResults.Created($"/api/integrations/{created.Id}", created);
        }).RequireAuthorization(AdminPolicies.Admin).WithName("CreateIntegration");
        integrations.MapPut("/{id:guid}", async (Guid id, IntegrationRequest input, IntegrationService service, CancellationToken ct) =>
            TypedResults.Ok(await service.UpdateAsync(id, input, ct)))
            .RequireAuthorization(AdminPolicies.Admin).WithName("UpdateIntegration").WithSummary("Changes an integration; deactivating it refuses all its keys");
        integrations.MapDelete("/{id:guid}", async (Guid id, IntegrationService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return TypedResults.NoContent();
        }).RequireAuthorization(AdminPolicies.Admin).WithName("DeleteIntegration").WithSummary("Deletes an integration whose keys are all revoked");
    }
}
