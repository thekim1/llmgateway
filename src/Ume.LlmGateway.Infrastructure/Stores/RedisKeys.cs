using StackExchange.Redis;
using Ume.LlmGateway.Domain;

namespace Ume.LlmGateway.Infrastructure.Stores;

/// <summary>
/// Every Redis key and channel the gateway uses, with their lifetimes. All live under <c>ume:</c>: the restricted
/// gateway ACL user (<c>deploy/redis/users.acl.example</c>) may only touch <c>~ume:*</c> and publish on <c>&amp;ume:invalidate</c>.
/// </summary>
internal static class RedisKeys
{
    public const string Prefix = "ume:";

    public const string InvalidateChannelName = Prefix + "invalidate";

    /// <summary>Cache invalidation (key revoked, configuration changed), published by the admin API.</summary>
    public static readonly RedisChannel InvalidateChannel = RedisChannel.Literal(InvalidateChannelName);

    /// <summary>A per-minute rate-limit counter outlives its minute a little, so late token reports still land.</summary>
    public const int RateWindowSeconds = 120;

    public static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(RateWindowSeconds);

    /// <summary>Requests a key made in the minute <paramref name="minute"/> (Unix minutes).</summary>
    public static string RateRequests(Guid keyId, long minute) => $"ume:rl:req:{keyId:N}:{minute}";

    /// <summary>Tokens a key used in the minute <paramref name="minute"/> (Unix minutes).</summary>
    public static string RateTokens(Guid keyId, long minute) => $"ume:rl:tok:{keyId:N}:{minute}";

    /// <summary>Open realtime sessions of a key (sorted set of session id → expiry).</summary>
    public static string RealtimeSessions(Guid keyId) => $"ume:rt:{keyId:N}";

    /// <summary>Set while a provider's circuit is open (<c>auto</c> or <c>forced</c>), with the break duration as TTL.</summary>
    public static string CircuitOpen(Guid providerId) => $"ume:circuit:{providerId:N}";

    /// <summary>Failures of a provider in the current sampling window.</summary>
    public static string CircuitFailures(Guid providerId) => $"ume:circuit-fail:{providerId:N}";

    /// <summary>Spend of one budget scope in one period window, in micro-SEK.</summary>
    public static string Spend(BudgetScope scope, Guid scopeId, BudgetPeriod period, DateTimeOffset periodStart) =>
        $"ume:spend:{scope}:{scopeId:N}:{period}:{periodStart.ToUnixTimeSeconds()}";
}
