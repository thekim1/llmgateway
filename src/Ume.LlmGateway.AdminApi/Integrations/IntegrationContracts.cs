using System.ComponentModel.DataAnnotations;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;

namespace Ume.LlmGateway.AdminApi.Integrations;

// Request and response bodies; the admin UI's types (src/admin-ui/src/api/types.ts) mirror the responses.

public sealed record IntegrationRequest(
    [property: Required, StringLength(100, MinimumLength = 1)] string Name,
    Guid OwnerTeamId,
    [property: StringLength(1000)] string? Description = null,
    bool IsActive = true) : AdminRequest;

/// <summary>
/// An integration with its owner team and how many keys (not revoked) it has of each kind. Every key manager can list
/// integrations, to mark a key as assigned to one; only gateway admins change them.
/// </summary>
public sealed record IntegrationDto(
    Guid Id, string Name, string? Description, Guid OwnerTeamId, string? OwnerTeamName, Guid? OwnerDepartmentId, string? OwnerDepartmentName,
    bool IsActive, DateTimeOffset CreatedAt, string? CreatedBy, int CredentialKeys, int AssignedKeys)
{
    public static IntegrationDto Of(Integration i, int credentialKeys, int assignedKeys) => new(
        i.Id, i.Name, i.Description, i.OwnerTeamId, i.OwnerTeam?.Name, i.OwnerTeam?.DepartmentId, i.OwnerTeam?.Department?.Name,
        i.IsActive, i.CreatedAt, i.CreatedBy, credentialKeys, assignedKeys);
}

/// <summary>What a key can be in an integration, for the key form.</summary>
public sealed record IntegrationOptionsDto(IReadOnlyList<string> Scopes, IReadOnlyList<IntegrationRole> Roles);
