using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.Infrastructure.Persistence;

/// <summary>
/// Queries shared by the gateway and the admin API, so both read budgets, key lineage and exchange rates the same way.
/// </summary>
public static class GatewayQueries
{
    /// <summary>The currency provider prices are entered in.</summary>
    public const string PriceCurrency = "USD";

    /// <summary>The exchange rate in effect at <paramref name="now"/>, or <c>null</c> when none has been entered.</summary>
    public static Task<ExchangeRate?> CurrentRateAsync(this GatewayDbContext db, DateTimeOffset now, CancellationToken cancellationToken, string currency = PriceCurrency) =>
        db.ExchangeRates.AsNoTracking()
            .Where(r => r.Currency == currency && r.EffectiveFrom <= now)
            .OrderByDescending(r => r.EffectiveFrom)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>Rotated key id → its replacement, for <see cref="KeyRotation"/>.</summary>
    public static Task<Dictionary<Guid, Guid>> KeyRotationMapAsync(this GatewayDbContext db, CancellationToken cancellationToken) =>
        db.VirtualKeys.AsNoTracking()
            .Where(k => k.RotatedToKeyId != null)
            .ToDictionaryAsync(k => k.Id, k => k.RotatedToKeyId!.Value, cancellationToken);

    /// <summary>
    /// SEK spent against <paramref name="budget"/> in <paramref name="window"/>. A key budget covers the key and every key it was rotated into.
    /// Pass <paramref name="rotations"/> when it is already loaded (e.g. when summing many budgets).
    /// </summary>
    public static async Task<decimal> SpentAsync(this GatewayDbContext db, Budget budget, PeriodWindow window, CancellationToken cancellationToken,
        IReadOnlyDictionary<Guid, Guid>? rotations = null)
    {
        var query = db.UsageRecords.AsNoTracking().Where(u => u.Timestamp >= window.Start && u.Timestamp < window.End);
        switch (budget.Scope)
        {
            case BudgetScope.Department:
                query = query.Where(u => u.DepartmentId == budget.ScopeId);
                break;
            case BudgetScope.Team:
                query = query.Where(u => u.TeamId == budget.ScopeId);
                break;
            case BudgetScope.VirtualKey:
                var keyIds = KeyRotation.Descendants(budget.ScopeId, rotations ?? await db.KeyRotationMapAsync(cancellationToken)).ToArray();
                query = query.Where(u => keyIds.Contains(u.VirtualKeyId));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(budget), budget.Scope, "Unknown budget scope.");
        }

        return await query.SumAsync(u => u.CostSek, cancellationToken);
    }
}
