using System.Collections.Concurrent;
using StackExchange.Redis;

namespace Ume.LlmGateway.Infrastructure.Stores;

/// <summary>
/// Where the usage writer keeps batches of usage records the database could not take yet, so a database outage neither
/// drops records nor holds up requests, and a batch survives its gateway instance. Batches are opaque payloads; a batch
/// is named <c>{batchId}:{records}</c>. Like the other stores, the Redis implementation is bounded by the multiplexer's
/// timeouts and does not observe cancellation.
/// </summary>
public interface IUsageJournal
{
    /// <summary>Whether batches outlive this process (Redis); the in-memory journal only outlives a database outage.</summary>
    bool IsDurable { get; }

    /// <summary>Keeps a batch. False when the journal is full (<paramref name="maxRecords"/>) or unavailable: the caller keeps it in memory.</summary>
    Task<bool> AddAsync(string batch, ReadOnlyMemory<byte> payload, int records, long maxRecords, CancellationToken cancellationToken);

    /// <summary>The batch's payload, or null when it is gone (another instance wrote it).</summary>
    Task<byte[]?> GetAsync(string batch, CancellationToken cancellationToken);

    /// <summary>Forgets a batch once it is in the database.</summary>
    Task RemoveAsync(string batch, CancellationToken cancellationToken);

    /// <summary>Tells other instances these batches are still being worked on.</summary>
    Task TouchAsync(IReadOnlyCollection<string> batches, CancellationToken cancellationToken);

    /// <summary>Takes over up to <paramref name="max"/> batches nobody has touched for <paramref name="idle"/> (their instance stopped).</summary>
    Task<IReadOnlyList<string>> ClaimAbandonedAsync(TimeSpan idle, int max, CancellationToken cancellationToken);

    /// <summary>Records held, for the operations status.</summary>
    Task<long> CountRecordsAsync(CancellationToken cancellationToken);
}

public static class UsageJournalBatches
{
    public static string Name(string id, int records) => $"{id}:{records}";

    public static int Records(string batch) => int.TryParse(batch.AsSpan(batch.LastIndexOf(':') + 1), out var n) ? n : 0;
}

/// <summary>
/// Batches in Redis: the payload as a string key, the batch in a sorted set scored by its last heartbeat, and a record
/// count for the cap. Redis is not persistent in this deployment (counters are rebuilt from the database), so a batch
/// survives its gateway instance and a database outage, not a Redis restart.
/// </summary>
public sealed class RedisUsageJournal(IConnectionMultiplexer redis, TimeProvider time) : IUsageJournal
{
    // KEYS: index, record count, payload. ARGV: batch, records, cap, now (ms), payload.
    private const string AddScript = """
        local held = tonumber(redis.call('GET', KEYS[2]) or '0')
        if held + tonumber(ARGV[2]) > tonumber(ARGV[3]) then return 0 end
        redis.call('SET', KEYS[3], ARGV[5])
        redis.call('ZADD', KEYS[1], ARGV[4], ARGV[1])
        redis.call('INCRBY', KEYS[2], ARGV[2])
        return 1
        """;

    // KEYS: index, record count, payload. ARGV: batch, records. Counts down only for a batch that was still there.
    private const string RemoveScript = """
        if redis.call('ZREM', KEYS[1], ARGV[1]) == 1 then
          redis.call('DECRBY', KEYS[2], ARGV[2])
        end
        redis.call('DEL', KEYS[3])
        return 1
        """;

    // KEYS: index. ARGV: idle before (ms), now (ms), max. Claiming re-scores the batch, so one instance gets it.
    private const string ClaimScript = """
        local batches = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', ARGV[1], 'LIMIT', 0, ARGV[3])
        for _, batch in ipairs(batches) do
          redis.call('ZADD', KEYS[1], 'XX', ARGV[2], batch)
        end
        return batches
        """;

    public bool IsDurable => true;

    public async Task<bool> AddAsync(string batch, ReadOnlyMemory<byte> payload, int records, long maxRecords, CancellationToken cancellationToken)
    {
        var result = await redis.GetDatabase().ScriptEvaluateAsync(AddScript,
            [RedisKeys.UsageJournalIndex, RedisKeys.UsageJournalRecords, RedisKeys.UsageJournalBatch(batch)],
            [batch, records, maxRecords, Now(), payload]);
        return (long)result == 1;
    }

    public async Task<byte[]?> GetAsync(string batch, CancellationToken cancellationToken)
    {
        var value = await redis.GetDatabase().StringGetAsync(RedisKeys.UsageJournalBatch(batch));
        return value.HasValue ? (byte[]?)value : null;
    }

    public Task RemoveAsync(string batch, CancellationToken cancellationToken) =>
        redis.GetDatabase().ScriptEvaluateAsync(RemoveScript,
            [RedisKeys.UsageJournalIndex, RedisKeys.UsageJournalRecords, RedisKeys.UsageJournalBatch(batch)],
            [batch, UsageJournalBatches.Records(batch)]);

    public Task TouchAsync(IReadOnlyCollection<string> batches, CancellationToken cancellationToken)
    {
        if (batches.Count == 0)
        {
            return Task.CompletedTask;
        }

        var now = Now();
        return redis.GetDatabase().SortedSetAddAsync(RedisKeys.UsageJournalIndex,
            [.. batches.Select(b => new SortedSetEntry(b, now))], SortedSetWhen.Exists);
    }

    public async Task<IReadOnlyList<string>> ClaimAbandonedAsync(TimeSpan idle, int max, CancellationToken cancellationToken)
    {
        var now = Now();
        var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(ClaimScript,
            [RedisKeys.UsageJournalIndex], [now - (long)idle.TotalMilliseconds, now, max]))!;
        return [.. result.Select(r => (string)r!)];
    }

    public async Task<long> CountRecordsAsync(CancellationToken cancellationToken)
    {
        var value = await redis.GetDatabase().StringGetAsync(RedisKeys.UsageJournalRecords);
        return value.HasValue ? (long)value : 0;
    }

    private long Now() => time.GetUtcNow().ToUnixTimeMilliseconds();
}

/// <summary>
/// Batches in this process's memory (development and tests without Redis): rides out a database outage without
/// dropping records or holding up requests, but not a restart.
/// </summary>
public sealed class InMemoryUsageJournal(TimeProvider time) : IUsageJournal
{
    private readonly ConcurrentDictionary<string, (byte[] Payload, DateTimeOffset Touched)> _batches = new();
    private long _records;

    public bool IsDurable => false;

    public Task<bool> AddAsync(string batch, ReadOnlyMemory<byte> payload, int records, long maxRecords, CancellationToken cancellationToken)
    {
        if (Interlocked.Add(ref _records, records) > maxRecords)
        {
            Interlocked.Add(ref _records, -records);
            return Task.FromResult(false);
        }

        _batches[batch] = (payload.ToArray(), time.GetUtcNow());
        return Task.FromResult(true);
    }

    public Task<byte[]?> GetAsync(string batch, CancellationToken cancellationToken) =>
        Task.FromResult(_batches.TryGetValue(batch, out var entry) ? entry.Payload : null);

    public Task RemoveAsync(string batch, CancellationToken cancellationToken)
    {
        if (_batches.TryRemove(batch, out _))
        {
            Interlocked.Add(ref _records, -UsageJournalBatches.Records(batch));
        }

        return Task.CompletedTask;
    }

    public Task TouchAsync(IReadOnlyCollection<string> batches, CancellationToken cancellationToken)
    {
        foreach (var batch in batches)
        {
            if (_batches.TryGetValue(batch, out var entry))
            {
                _batches[batch] = (entry.Payload, time.GetUtcNow());
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ClaimAbandonedAsync(TimeSpan idle, int max, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]); // one process: nothing is ever abandoned by another instance

    public Task<long> CountRecordsAsync(CancellationToken cancellationToken) => Task.FromResult(Interlocked.Read(ref _records));
}
