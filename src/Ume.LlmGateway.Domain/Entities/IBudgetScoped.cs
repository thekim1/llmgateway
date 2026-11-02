namespace Ume.LlmGateway.Domain.Entities;

/// <summary>Something that belongs to a budget owner (a department, team or key): a budget or one of its alerts.</summary>
public interface IBudgetScoped
{
    BudgetScope Scope { get; }

    Guid ScopeId { get; }
}
