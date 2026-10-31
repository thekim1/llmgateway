namespace Ume.LlmGateway.Domain.Entities;

/// <summary>A weighted destination of a <see cref="RoutingRule"/>: a route alias or deployment name.</summary>
public sealed class RoutingRuleTarget
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid RoutingRuleId { get; set; }
    public required string Model { get; set; }
    public int Weight { get; set; } = 1;
}
