namespace Ume.LlmGateway.Domain.Entities;

/// <summary>
/// An administrator-defined routing rule: when <see cref="Condition"/> matches a request, it is sent to
/// <see cref="Targets"/> (weighted) with <see cref="Fallbacks"/> instead of the alias it asked for. Evaluated by
/// <see cref="Routing.RoutingRuleSet"/>. <see cref="ScopeIds"/> are the keys, teams or departments (of the kind
/// <see cref="Scope"/> names) the rule is shared by; no foreign keys: an owner that no longer exists simply never matches.
/// </summary>
public sealed class RoutingRule
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool IsEnabled { get; set; } = true;

    /// <summary>Lower is checked first within a scope.</summary>
    public int Priority { get; set; }

    public Routing.RoutingScope Scope { get; set; } = Routing.RoutingScope.Global;

    /// <summary>The owners the rule applies to (empty for a global rule, or for a disabled rule waiting for a new owner).</summary>
    public List<Guid> ScopeIds { get; set; } = [];

    /// <summary>A shipped example (disabled until an administrator adopts it). Editing or enabling it makes it an ordinary rule.</summary>
    public bool IsTemplate { get; set; }

    /// <summary>Condition in the routing expression language; empty always matches.</summary>
    public string Condition { get; set; } = "";

    /// <summary>When true the rewritten model is routed through the rules again.</summary>
    public bool Chain { get; set; }

    public List<RoutingRuleTarget> Targets { get; set; } = [];

    /// <summary>Models (alias or deployment names) tried in order after all targets.</summary>
    public List<string> Fallbacks { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Routing.RoutingRuleDefinition ToDefinition() => new(
        Id, Name, IsEnabled, Priority, Scope, [.. ScopeIds], Condition, Chain,
        [.. Targets.Select(t => new Routing.RuleTarget(t.Model, t.Weight))], [.. Fallbacks]);
}
