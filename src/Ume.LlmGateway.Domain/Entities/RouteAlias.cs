namespace Ume.LlmGateway.Domain.Entities;

/// <summary>Client-facing model alias (e.g. <c>ume/chat-standard</c>) mapping to targets with fallback.</summary>
public sealed class RouteAlias
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public ModelKind Kind { get; set; }
    public bool IsEnabled { get; set; } = true;
    public List<RouteTarget> Targets { get; set; } = [];
}
