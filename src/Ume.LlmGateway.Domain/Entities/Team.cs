using Ume.LlmGateway.Domain.Requests;

namespace Ume.LlmGateway.Domain.Entities;

public sealed class Team
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid DepartmentId { get; set; }
    public Department? Department { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// Matched against the <c>teamCodes</c> claim of team admins, who manage this team's keys. Unique; null = no team admins.
    /// </summary>
    public string? Code { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public List<VirtualKey> VirtualKeys { get; set; } = [];

    /// <summary>Applies to every key of the team; a key's own policy wins where it sets something.</summary>
    public RequestPolicy? RequestPolicy { get; set; }
}
