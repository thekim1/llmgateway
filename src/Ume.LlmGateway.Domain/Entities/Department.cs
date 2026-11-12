namespace Ume.LlmGateway.Domain.Entities;

/// <summary>Förvaltning – top level of cost attribution.</summary>
public sealed class Department
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }

    /// <summary>Ansvarskod / cost-centre code used for internal invoicing.</summary>
    public required string CostCenterCode { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Values of the identity provider's group claim (an Entra object id, an AD FS or Keycloak group name) whose members
    /// belong to this department: a signed-in user in one of them gets the department's scope, never a role.
    /// </summary>
    public List<string> MemberGroups { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public List<Team> Teams { get; set; } = [];
}
