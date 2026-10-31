using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Ume.LlmGateway.Infrastructure.Stores;

public enum CircuitState
{
    Closed = 0,
    Open = 1,
}

/// <summary>Providers whose circuit is open and model deployments on cooldown, from one lookup.</summary>
public sealed record CircuitStatus(IReadOnlySet<Guid> OpenProviders, IReadOnlySet<Guid> CoolingDeployments)
{
    public static CircuitStatus None { get; } = new(new HashSet<Guid>(), new HashSet<Guid>());

    /// <summary>The provider's circuit is open or the deployment is on cooldown: try it only after everything else.</summary>
    public bool IsUnavailable(Guid providerId, Guid deploymentId) => OpenProviders.Contains(providerId) || CoolingDeployments.Contains(deploymentId);
}

/// <summary>
/// Shared per-provider circuit breaker state and per-deployment cooldowns, so all gateway instances and the admin API
/// agree. A cooldown is what a provider asked for with <c>Retry-After</c> (a quota, not a health problem), so it
/// applies to the one deployment and does not count against the provider's circuit.
/// </summary>
public interface ICircuitBreakerStore
{
    Task<IReadOnlySet<Guid>> GetOpenAsync(IReadOnlyCollection<Guid> providerIds, CancellationToken cancellationToken);

    /// <summary>Open circuits of <paramref name="providerIds"/> and cooldowns of <paramref name="deploymentIds"/> in one round trip.</summary>
    Task<CircuitStatus> GetStatusAsync(IReadOnlyCollection<Guid> providerIds, IReadOnlyCollection<Guid> deploymentIds, CancellationToken cancellationToken);

    Task RecordFailureAsync(Guid providerId, CancellationToken cancellationToken);

    /// <summary>Takes the deployment out of rotation for <paramref name="duration"/>; an existing longer cooldown is kept.</summary>
    Task CoolDownAsync(Guid deploymentId, TimeSpan duration, CancellationToken cancellationToken);

    Task RecordSuccessAsync(Guid providerId, CancellationToken cancellationToken);
    Task SetStateAsync(Guid providerId, CircuitState state, TimeSpan? openFor, CancellationToken cancellationToken);
}

public sealed class CircuitBreakerOptions
{
    public const string SectionName = "CircuitBreaker";

    public int FailureThreshold { get; set; } = 5;
    public TimeSpan SamplingWindow { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The longest cooldown a provider's <c>Retry-After</c> can put a deployment on; zero ignores <c>Retry-After</c>.</summary>
    [System.ComponentModel.DataAnnotations.Range(typeof(TimeSpan), "00:00:00", "01:00:00")]
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromMinutes(5);
}

public sealed class InMemoryCircuitBreakerStore(TimeProvider time, Microsoft.Extensions.Options.IOptions<CircuitBreakerOptions> options) : ICircuitBreakerStore
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _openUntil = new();
    private readonly ConcurrentDictionary<Guid, List<DateTimeOffset>> _failures = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _coolUntil = new();

    public Task<IReadOnlySet<Guid>> GetOpenAsync(IReadOnlyCollection<Guid> providerIds, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        IReadOnlySet<Guid> open = providerIds.Where(id => _openUntil.TryGetValue(id, out var until) && until > now).ToHashSet();
        return Task.FromResult(open);
    }

    public async Task<CircuitStatus> GetStatusAsync(IReadOnlyCollection<Guid> providerIds, IReadOnlyCollection<Guid> deploymentIds, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var open = await GetOpenAsync(providerIds, cancellationToken);
        var cooling = deploymentIds.Where(id => _coolUntil.TryGetValue(id, out var until) && until > now).ToHashSet();
        return open.Count == 0 && cooling.Count == 0 ? CircuitStatus.None : new CircuitStatus(open, cooling);
    }

    public Task CoolDownAsync(Guid deploymentId, TimeSpan duration, CancellationToken cancellationToken)
    {
        var until = time.GetUtcNow() + duration;
        _coolUntil.AddOrUpdate(deploymentId, until, (_, existing) => existing > until ? existing : until);
        return Task.CompletedTask;
    }

    public Task RecordFailureAsync(Guid providerId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var o = options.Value;
        var list = _failures.GetOrAdd(providerId, _ => []);
        lock (list)
        {
            list.RemoveAll(t => t < now - o.SamplingWindow);
            list.Add(now);
            if (list.Count >= o.FailureThreshold)
            {
                _openUntil[providerId] = now + o.BreakDuration;
                list.Clear();
            }
        }

        return Task.CompletedTask;
    }

    public Task RecordSuccessAsync(Guid providerId, CancellationToken cancellationToken)
    {
        if (_failures.TryGetValue(providerId, out var list))
        {
            lock (list)
            {
                list.Clear();
            }
        }

        return Task.CompletedTask;
    }

    public Task SetStateAsync(Guid providerId, CircuitState state, TimeSpan? openFor, CancellationToken cancellationToken)
    {
        if (state == CircuitState.Open)
        {
            _openUntil[providerId] = time.GetUtcNow() + (openFor ?? options.Value.BreakDuration);
        }
        else
        {
            _openUntil.TryRemove(providerId, out _);
            _failures.TryRemove(providerId, out _);
        }

        return Task.CompletedTask;
    }
}

public sealed class RedisCircuitBreakerStore(IConnectionMultiplexer redis, IOptions<CircuitBreakerOptions> options) : ICircuitBreakerStore
{
    private const string FailureScript = """
        local n = redis.call('INCR', KEYS[1])
        if n == 1 then redis.call('EXPIRE', KEYS[1], ARGV[1]) end
        if n >= tonumber(ARGV[2]) then
          redis.call('SET', KEYS[2], 'auto', 'EX', ARGV[3])
          redis.call('DEL', KEYS[1])
          return 1
        end
        return 0
        """;

    // Never shortens a cooldown: SET only creates the key, PEXPIRE GT only lengthens it (Redis 7+; both in the gateway ACL).
    private const string CooldownScript = """
        if not redis.call('SET', KEYS[1], 'retry-after', 'NX', 'PX', ARGV[1]) then
          redis.call('PEXPIRE', KEYS[1], ARGV[1], 'GT')
        end
        return 0
        """;

    private static RedisKey OpenKey(Guid id) => RedisKeys.CircuitOpen(id);
    private static RedisKey FailKey(Guid id) => RedisKeys.CircuitFailures(id);

    public async Task<IReadOnlySet<Guid>> GetOpenAsync(IReadOnlyCollection<Guid> providerIds, CancellationToken cancellationToken)
    {
        if (providerIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var ids = providerIds.ToArray();
        var values = await redis.GetDatabase().StringGetAsync([.. ids.Select(OpenKey)]);
        return ids.Where((_, i) => values[i].HasValue).ToHashSet();
    }

    public async Task<CircuitStatus> GetStatusAsync(IReadOnlyCollection<Guid> providerIds, IReadOnlyCollection<Guid> deploymentIds, CancellationToken cancellationToken)
    {
        if (providerIds.Count == 0 && deploymentIds.Count == 0)
        {
            return CircuitStatus.None;
        }

        // One MGET for both: circuits by provider, then cooldowns by deployment.
        var providers = providerIds.ToArray();
        var deployments = deploymentIds.ToArray();
        var keys = new RedisKey[providers.Length + deployments.Length];
        for (var i = 0; i < providers.Length; i++)
        {
            keys[i] = OpenKey(providers[i]);
        }

        for (var i = 0; i < deployments.Length; i++)
        {
            keys[providers.Length + i] = RedisKeys.Cooldown(deployments[i]);
        }

        var values = await redis.GetDatabase().StringGetAsync(keys);
        HashSet<Guid>? open = null;
        HashSet<Guid>? cooling = null;
        for (var i = 0; i < values.Length; i++)
        {
            if (!values[i].HasValue)
            {
                continue;
            }

            if (i < providers.Length)
            {
                (open ??= []).Add(providers[i]);
            }
            else
            {
                (cooling ??= []).Add(deployments[i - providers.Length]);
            }
        }

        return open is null && cooling is null ? CircuitStatus.None
            : new CircuitStatus(open ?? CircuitStatus.None.OpenProviders, cooling ?? CircuitStatus.None.CoolingDeployments);
    }

    public Task CoolDownAsync(Guid deploymentId, TimeSpan duration, CancellationToken cancellationToken) =>
        redis.GetDatabase().ScriptEvaluateAsync(CooldownScript, [RedisKeys.Cooldown(deploymentId)], [Math.Max(1, (long)duration.TotalMilliseconds)]);

    public Task RecordFailureAsync(Guid providerId, CancellationToken cancellationToken)
    {
        var o = options.Value;
        return redis.GetDatabase().ScriptEvaluateAsync(
            FailureScript,
            [FailKey(providerId), OpenKey(providerId)],
            [(long)o.SamplingWindow.TotalSeconds, o.FailureThreshold, (long)o.BreakDuration.TotalSeconds]);
    }

    /// <summary>Fire-and-forget: called on every successful provider call, before the response starts.</summary>
    public Task RecordSuccessAsync(Guid providerId, CancellationToken cancellationToken) =>
        redis.GetDatabase().KeyDeleteAsync(FailKey(providerId), CommandFlags.FireAndForget);

    public async Task SetStateAsync(Guid providerId, CircuitState state, TimeSpan? openFor, CancellationToken cancellationToken)
    {
        var db = redis.GetDatabase();
        if (state == CircuitState.Open)
        {
            await db.StringSetAsync(OpenKey(providerId), "forced", openFor ?? options.Value.BreakDuration);
        }
        else
        {
            await db.KeyDeleteAsync([OpenKey(providerId), FailKey(providerId)]);
        }
    }
}
