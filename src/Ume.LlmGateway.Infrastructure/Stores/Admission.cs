using StackExchange.Redis;

namespace Ume.LlmGateway.Infrastructure.Stores;

/// <summary>What a key's rate limits allow, in the terms of <see cref="IRateLimiter.AcquireAsync"/>.</summary>
public readonly record struct RateLimitRequest(Guid KeyId, int? RequestsPerMinute, int? TokensPerMinute, int? MaxConcurrent, string? LeaseId);

/// <summary>The rate-limit decision and, when the request was admitted, the budget reservation (null when refused: nothing was reserved).</summary>
public sealed record Admission(RateLimitDecision Rate, SpendReservation? Spend);

/// <summary>
/// The rate limit and the budget reservation of a request together: the reservation is made only when the rate limit
/// admits the request, as two separate calls would do, but in one round trip to a shared store.
/// </summary>
public interface IAdmissionStore
{
    /// <summary>
    /// <see cref="IRateLimiter.AcquireAsync"/> with <paramref name="rate"/>, then, only when it admits the request,
    /// <see cref="ISpendLedger.ReserveAsync"/> with <paramref name="counters"/> and <paramref name="amounts"/> (missing
    /// counters are reported, not counted from zero).
    /// </summary>
    Task<Admission> AdmitAsync(RateLimitRequest rate, IReadOnlyList<SpendCounter> counters, IReadOnlyList<long> amounts, CancellationToken cancellationToken);
}

/// <summary>The two calls one after the other: the in-memory stores, which have no round trip to save.</summary>
public sealed class SequentialAdmissionStore(IRateLimiter limiter, ISpendLedger ledger) : IAdmissionStore
{
    public async Task<Admission> AdmitAsync(RateLimitRequest rate, IReadOnlyList<SpendCounter> counters, IReadOnlyList<long> amounts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(counters);
        var decision = await limiter.AcquireAsync(rate.KeyId, rate.RequestsPerMinute, rate.TokensPerMinute, rate.MaxConcurrent, rate.LeaseId, cancellationToken);
        return decision.Allowed
            ? new Admission(decision, await ledger.ReserveAsync(counters, amounts, missingAsZero: false, cancellationToken))
            : new Admission(decision, null);
    }
}

/// <summary>
/// One Lua script runs the rate limiter's <c>rate()</c> and, when it admits the request, the ledger's <c>reserve()</c>.
/// When only one of them has anything to do, or the stores are not the Redis ones, the calls are made as usual.
/// </summary>
public sealed class RedisAdmissionStore(IConnectionMultiplexer redis, IRateLimiter limiter, ISpendLedger ledger, TimeProvider time) : IAdmissionStore
{
    // Keys: the rate limiter's, then the counters. Arguments: the rate limiter's, then the ledger's.
    // Returns {rate} when the rate limit refused (nothing reserved), else {rate, reservation}.
    private const string Script = RedisRateLimiter.RateFunction + RedisSpendLedger.ReserveFunction + """
        local r = rate()
        if r[1] < 0 then return {r} end
        return {r, reserve(5, 9)}
        """;

    private readonly SequentialAdmissionStore _sequential = new(limiter, ledger);

    public async Task<Admission> AdmitAsync(RateLimitRequest rate, IReadOnlyList<SpendCounter> counters, IReadOnlyList<long> amounts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(counters);
        if (counters.Count == 0 || limiter is not RedisRateLimiter || ledger is not RedisSpendLedger
            || !RedisRateLimiter.IsLimited(rate.RequestsPerMinute, rate.TokensPerMinute, rate.MaxConcurrent, rate.LeaseId))
        {
            return await _sequential.AdmitAsync(rate, counters, amounts, cancellationToken);
        }

        var command = RedisRateLimiter.Command(rate.KeyId, rate.RequestsPerMinute, rate.TokensPerMinute, rate.MaxConcurrent, rate.LeaseId, time.GetUtcNow());
        var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(Script,
            [.. command.Keys, .. RedisSpendLedger.CounterKeys(counters)],
            [.. command.Args, .. RedisSpendLedger.ReserveArgs(counters, amounts, missingAsZero: false)]))!;
        var decision = RedisRateLimiter.Decide((RedisResult[])result[0]!, command);
        return new Admission(decision, result.Length > 1 ? RedisSpendLedger.Reservation((RedisResult[])result[1]!) : null);
    }
}
