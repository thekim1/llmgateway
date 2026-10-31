namespace Ume.LlmGateway.Domain.Entities;

public sealed class RouteTarget
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid RouteAliasId { get; set; }
    public Guid ModelDeploymentId { get; set; }
    public ModelDeployment? ModelDeployment { get; set; }

    /// <summary>Lower = tried first. Targets with equal priority are load-balanced by weight.</summary>
    public int Priority { get; set; }

    public int Weight { get; set; } = 1;
}
