namespace Ume.LlmGateway.Domain.Entities;

/// <summary>
/// Another system that calls the gateway for others (a RAG or agent platform): its credential keys, the keys assigned
/// to it by the teams it serves, and what it may read (docs/integrations.md). Made and changed by gateway admins.
/// </summary>
public sealed class Integration
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>The team that owns the integration's credential keys (the platform's own team).</summary>
    public Guid OwnerTeamId { get; set; }
    public Team? OwnerTeam { get; set; }

    /// <summary>An inactive integration's keys are refused, credential and assigned alike.</summary>
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}
