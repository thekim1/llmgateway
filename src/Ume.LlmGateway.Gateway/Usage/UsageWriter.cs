using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Gateway.Budgets;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Security;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.Gateway.Usage;

/// <summary>Work item for the background writer: one usage record plus any budget alerts it triggered.</summary>
public sealed record UsageWork(UsageRecord Record, IReadOnlyList<AlertCandidate> Alerts);

/// <summary>
/// Decouples request latency from database writes: usage metadata is queued in a bounded channel and written
/// in batches. Budget alerts are stored with the batch and handed to <see cref="AlertNotifier"/> once it has committed.
/// <para>
/// When the database refuses a batch, the <see cref="IUsageJournal"/> keeps it (<see cref="UsageJournalMode.Spill"/>,
/// the default): the writer keeps draining the queue into the journal, so requests are not held up and nothing is
/// dropped, and writes the journaled batches once the database is back. Batches of an instance that stopped are taken
/// over by another one. With <see cref="UsageJournalMode.Always"/> every batch is journaled before it is written, so a
/// crash loses nothing. Without a journal (or when it is full) a failed batch is kept in memory and retried; the queue
/// then fills and callers wait (back-pressure) for a bounded time, after which the caller drops and logs the record.
/// None of this is on the request path: requests only ever add to the in-memory queue.
/// </para>
/// </summary>
public sealed partial class UsageWriter : BackgroundService
{
    private const int Capacity = 10_000;

    /// <summary>Most records written at once. COPY makes a large batch cheap; under load the queue drains in a few writes.</summary>
    private const int MaxBatch = 5_000;
    private readonly Channel<UsageWork> _channel = Channel.CreateBounded<UsageWork>(new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
    });

    /// <summary>How long after a refused write the database is tried again, and how often journaled batches are retried.</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    /// <summary>Journaled batches written per pass, so new records are not kept waiting behind a long backlog.</summary>
    private const int MaxJournalBatchesPerPass = 20;

    private static readonly System.Text.Json.JsonSerializerOptions JournalJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopes;
    private readonly AlertNotifier _alerts;
    private readonly TimeProvider _time;
    private readonly ILogger<UsageWriter> _logger;
    private readonly IUsageJournal? _journal;
    private readonly UsageJournalOptions _journalOptions;

    /// <summary>This instance's journaled batches not yet in the database, oldest first: added by the writer loop, written by the journal loop.</summary>
    private readonly List<string> _journaled = [];
    private readonly Lock _journaledLock = new();
    private int _journaledCount;
    private long _databaseRetryAt;
    private long _nextHousekeeping;

    /// <summary>
    /// Without <paramref name="journal"/> (or with <see cref="UsageJournalMode.Off"/> in <paramref name="options"/>) a
    /// refused batch is retried in memory, as before the journal existed.
    /// </summary>
    public UsageWriter(IServiceScopeFactory scopes, AlertNotifier alerts, TimeProvider time, ILogger<UsageWriter> logger, GatewayMetrics metrics,
        IUsageJournal? journal = null, Microsoft.Extensions.Options.IOptions<GatewayOptions>? options = null)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        _scopes = scopes;
        _alerts = alerts;
        _time = time;
        _logger = logger;
        _journalOptions = options?.Value.UsageJournal ?? new UsageJournalOptions { Mode = UsageJournalMode.Off };
        _journal = _journalOptions.Mode == UsageJournalMode.Off ? null : journal;
        metrics.ObserveQueueDepth(() => _channel.Reader.Count);
    }

    public int QueueDepth => _channel.Reader.Count;
    private int _consecutiveFailures;
    private int _inFlightRecords;
    private long _lastWriteTicks;
    public UsageWriterStatus Status => new(QueueDepth, Capacity, Volatile.Read(ref _inFlightRecords),
        Interlocked.Read(ref _lastWriteTicks) is > 0 and var ticks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null,
        Volatile.Read(ref _consecutiveFailures), Volatile.Read(ref _journaledCount));

    /// <summary>Queues the work if there is room right now (the usual case, without waiting or allocating).</summary>
    public bool TryEnqueue(UsageWork work) => _channel.Writer.TryWrite(work);

    public ValueTask EnqueueAsync(UsageWork work, CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(work, cancellationToken);

    /// <summary>
    /// Marks a request whose response has been sent but whose usage is not queued yet, so <see cref="FlushAsync"/>
    /// also waits for it. Dispose once the record is queued.
    /// </summary>
    public PendingUsage TrackPending()
    {
        Interlocked.Increment(ref _pending);
        return new PendingUsage(this);
    }

    /// <summary>
    /// Waits until everything queued so far has been written (tests, graceful shutdown). With
    /// <paramref name="includeJournal"/> false, batches in a durable journal count as safe: they need not be written yet.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken, bool includeJournal = true)
    {
        while (Volatile.Read(ref _pending) > 0 || _channel.Reader.Count > 0 || Interlocked.CompareExchange(ref _inFlight, 0, 0) > 0
               || (includeJournal && Volatile.Read(ref _journaledCount) > 0))
        {
            await Task.Delay(20, cancellationToken);
        }
    }

    private int _inFlight;
    private int _pending;

    public readonly struct PendingUsage(UsageWriter writer) : IDisposable
    {
        public void Dispose() => Interlocked.Decrement(ref writer._pending);
    }

    /// <summary>When this instance last wrote each key's LastUsedAt; the column is kept to the minute, not per request.</summary>
    private readonly Dictionary<Guid, DateTimeOffset> _lastUsedWritten = [];
    private static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);
    private DateTimeOffset _lastPruned;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Drain queued usage before shutdown so billing data is not lost on deploys. Batches in a durable journal are safe
        // there (another instance, or this one after the restart, writes them), so a database outage does not hold up the stop.
        try
        {
            await FlushAsync(cancellationToken, includeJournal: _journal is not { IsDurable: true });
        }
        catch (OperationCanceledException)
        {
            LogShutdownDropped(_logger, _channel.Reader.Count);
        }

        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Journaled batches are retried and abandoned ones taken over by a loop of their own, so the writer loop below is
        // the same with or without a journal while the database is healthy.
        var journalLoop = _journal is null ? Task.CompletedTask : JournalLoopAsync(stoppingToken);
        var batch = new List<UsageWork>(MaxBatch);
        try
        {
            while (await _channel.Reader.WaitToReadAsync(stoppingToken))
            {
                Interlocked.Increment(ref _inFlight);
                try
                {
                    while (batch.Count < MaxBatch && _channel.Reader.TryRead(out var item))
                    {
                        batch.Add(item);
                    }

                    Volatile.Write(ref _inFlightRecords, batch.Count);
                    await ProcessAsync(batch, stoppingToken);
                }
                finally
                {
                    batch.Clear();
                    Volatile.Write(ref _inFlightRecords, 0);
                    Interlocked.Decrement(ref _inFlight);
                }
            }
        }
        finally
        {
            await journalLoop;
        }
    }

    private async Task JournalLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RetryInterval, _time);
        try
        {
            do
            {
                try
                {
                    await HousekeepingAsync(stoppingToken);
                    await WriteJournaledAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogJournalFailed(_logger, ex.GetType().Name, 0);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Writes a new batch, journaling it first (Always) or when the database refuses it (Spill).</summary>
    private async Task ProcessAsync(List<UsageWork> batch, CancellationToken cancellationToken)
    {
        if (_journal is null)
        {
            await WriteUntilDoneAsync(batch, cancellationToken);
            return;
        }

        string? journaled = null;
        if (_journalOptions.Mode == UsageJournalMode.Always)
        {
            journaled = await TryJournalAsync(batch, cancellationToken);
        }

        // While the database is known to be down, new batches go straight to the journal.
        if (_time.GetTimestamp() < Interlocked.Read(ref _databaseRetryAt))
        {
            journaled ??= await TryJournalAsync(batch, cancellationToken);
            if (journaled is not null)
            {
                Remember(journaled);
                return;
            }

            await WriteUntilDoneAsync(batch, cancellationToken);
            return;
        }

        if (await TryWriteAsync(batch, retry: false, cancellationToken))
        {
            if (journaled is not null)
            {
                await ForgetAsync(journaled, cancellationToken);
            }

            return;
        }

        journaled ??= await TryJournalAsync(batch, cancellationToken);
        if (journaled is not null)
        {
            Remember(journaled);
            return;
        }

        // The journal is full or unavailable: keep the batch in memory and retry, as without one.
        await WriteUntilDoneAsync(batch, cancellationToken);
    }

    /// <summary>Retries the batch until it is written (no journal, or the journal is full).</summary>
    private async Task WriteUntilDoneAsync(List<UsageWork> batch, CancellationToken cancellationToken)
    {
        for (var attempt = 0; !await TryWriteAsync(batch, retry: attempt > 0, cancellationToken); attempt++)
        {
            await Task.Delay(RetryInterval, _time, cancellationToken);
        }
    }

    private async Task<bool> TryWriteAsync(List<UsageWork> batch, bool retry, CancellationToken cancellationToken)
    {
        try
        {
            await WriteBatchAsync(batch, retry, cancellationToken);
            Interlocked.Exchange(ref _lastWriteTicks, _time.GetUtcNow().UtcTicks);
            Volatile.Write(ref _consecutiveFailures, 0);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Interlocked.Increment(ref _consecutiveFailures);
            Interlocked.Exchange(ref _databaseRetryAt, _time.GetTimestamp() + (long)(RetryInterval.TotalSeconds * _time.TimestampFrequency));
            LogWriteFailed(_logger, ex.GetType().Name, batch.Count);
            return false;
        }
    }

    /// <summary>The journaled batch's name, or null when the journal is full or unavailable.</summary>
    private async Task<string?> TryJournalAsync(List<UsageWork> batch, CancellationToken cancellationToken)
    {
        var name = UsageJournalBatches.Name(Guid.NewGuid().ToString("N"), batch.Count);
        try
        {
            var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(JournalBatch.Of(batch), JournalJson);
            if (await _journal!.AddAsync(name, payload, batch.Count, _journalOptions.MaxRecords, cancellationToken))
            {
                return name;
            }

            LogJournalFull(_logger, batch.Count, _journalOptions.MaxRecords);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogJournalFailed(_logger, ex.GetType().Name, batch.Count);
        }

        return null;
    }

    private void Remember(string batch)
    {
        lock (_journaledLock)
        {
            if (!_journaled.Contains(batch))
            {
                _journaled.Add(batch);
            }

            Volatile.Write(ref _journaledCount, _journaled.Count);
        }
    }

    private void Written(string batch)
    {
        lock (_journaledLock)
        {
            _journaled.Remove(batch);
            Volatile.Write(ref _journaledCount, _journaled.Count);
        }
    }

    private async Task ForgetAsync(string batch, CancellationToken cancellationToken)
    {
        try
        {
            await _journal!.RemoveAsync(batch, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Already in the database: an instance that takes the batch over finds its records stored and skips them.
            LogJournalFailed(_logger, ex.GetType().Name, UsageJournalBatches.Records(batch));
        }
    }

    /// <summary>Writes this instance's journaled batches, oldest first, while the database takes them.</summary>
    private async Task WriteJournaledAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < MaxJournalBatchesPerPass; i++)
        {
            if (_time.GetTimestamp() < Interlocked.Read(ref _databaseRetryAt))
            {
                return;
            }

            string name;
            lock (_journaledLock)
            {
                if (_journaled.Count == 0)
                {
                    return;
                }

                name = _journaled[0];
            }

            byte[]? payload;
            try
            {
                payload = await _journal!.GetAsync(name, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogJournalFailed(_logger, ex.GetType().Name, UsageJournalBatches.Records(name));
                return;
            }

            if (payload is not null)
            {
                var batch = System.Text.Json.JsonSerializer.Deserialize<JournalBatch>(payload, JournalJson)!.ToWork();
                // The batch may already be stored (a commit whose answer was lost, an instance that wrote it before it
                // stopped): written as a retry, which skips request ids already there.
                if (!await TryWriteAsync(batch, retry: true, cancellationToken))
                {
                    return;
                }

                LogJournalWritten(_logger, batch.Count);
                await ForgetAsync(name, cancellationToken);
            }

            Written(name);
        }
    }

    /// <summary>
    /// Now and then: tells other instances this one still works on its journaled batches, and takes over batches of
    /// instances that stopped (after <see cref="UsageJournalOptions.AbandonedAfterSeconds"/> without a sign of life).
    /// </summary>
    private async Task HousekeepingAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetTimestamp();
        if (now < _nextHousekeeping)
        {
            return;
        }

        var abandoned = TimeSpan.FromSeconds(_journalOptions.AbandonedAfterSeconds);
        var interval = TimeSpan.FromSeconds(Math.Min(30, _journalOptions.AbandonedAfterSeconds / 4.0));
        _nextHousekeeping = now + (long)(interval.TotalSeconds * _time.TimestampFrequency);
        try
        {
            string[] mine;
            lock (_journaledLock)
            {
                mine = [.. _journaled];
            }

            await _journal!.TouchAsync(mine, cancellationToken);
            foreach (var batch in await _journal.ClaimAbandonedAsync(abandoned, 100, cancellationToken))
            {
                if (!mine.Contains(batch))
                {
                    LogJournalTakenOver(_logger, UsageJournalBatches.Records(batch));
                    Remember(batch);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogJournalFailed(_logger, ex.GetType().Name, 0);
        }
    }

    /// <summary>A batch as journaled: the records, and the alerts without the budget entity (only the fields alerts use).</summary>
    private sealed record JournalBatch(List<UsageRecord> Records, List<JournalAlert> Alerts)
    {
        public static JournalBatch Of(List<UsageWork> batch) => new(
            [.. batch.Select(w => w.Record)],
            [.. batch.SelectMany((w, i) => w.Alerts.Select(a => new JournalAlert(i, a.Budget.Id, a.Budget.Scope, a.Budget.ScopeId, a.Budget.LimitUsd,
                a.Window.Start, a.Window.End, a.ThresholdPercent, a.SpentUsd, a.Budget.Unit, a.Budget.LimitTokens)))]);

        public List<UsageWork> ToWork() => [.. Records.Select((r, i) => new UsageWork(r, [.. Alerts.Where(a => a.Record == i).Select(a =>
            new AlertCandidate(new Budget { Id = a.BudgetId, Scope = a.Scope, ScopeId = a.ScopeId, LimitUsd = a.LimitUsd, Unit = a.Unit, LimitTokens = a.LimitTokens },
                new Domain.Services.PeriodWindow(a.Start, a.End), a.ThresholdPercent, a.SpentUsd))]))];
    }

    private sealed record JournalAlert(int Record, Guid BudgetId, BudgetScope Scope, Guid ScopeId, decimal LimitUsd, DateTimeOffset Start,
        DateTimeOffset End, int ThresholdPercent, decimal SpentUsd, BudgetUnit Unit = BudgetUnit.Usd, long LimitTokens = 0);

    private async Task WriteBatchAsync(List<UsageWork> batch, bool retry, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        // Last-used timestamps: one statement for all keys in the batch, each key at most once a minute.
        var now = _time.GetUtcNow();
        List<(Guid KeyId, DateTimeOffset LastUsed)> lastUsed = [];
        foreach (var keyGroup in batch.GroupBy(b => b.Record.VirtualKeyId))
        {
            var last = keyGroup.Max(b => b.Record.Timestamp);
            if (!_lastUsedWritten.TryGetValue(keyGroup.Key, out var written) || last - written >= LastUsedResolution)
            {
                lastUsed.Add((keyGroup.Key, last));
            }
        }

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        // The retrying execution strategy only allows a transaction it can re-run as a whole.
        var attempts = retry ? 1 : 0;
        var newAlerts = await db.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            HashSet<string> persisted = new(StringComparer.Ordinal);
            if (attempts++ > 0)
            {
                // The batch is written in one transaction, so only a commit whose answer was lost leaves it written already.
                // Request ids are set before the write (unlike row ids), so they find such rows.
                var requestIds = batch.Select(b => b.Record.RequestId).ToArray();
                persisted.UnionWith(await db.UsageRecords.AsNoTracking().Where(r => requestIds.Contains(r.RequestId)).Select(r => r.RequestId)
                    .ToListAsync(ct));
            }

            await db.CopyUsageRecordsAsync(batch.Select(b => b.Record).Where(r => !persisted.Contains(r.RequestId)), ct);
            var alerts = await AddNewAlertsAsync(db, batch, ct);
            await db.UpdateKeysLastUsedAsync(lastUsed, ct);
            await transaction.CommitAsync(ct);
            return alerts;
        }, cancellationToken);

        foreach (var (keyId, last) in lastUsed)
        {
            _lastUsedWritten[keyId] = last;
        }

        PruneLastUsed(now);

        // Committed: notify now. A retry of a batch whose commit answer was lost finds these alerts stored and does not raise them again.
        foreach (var alert in newAlerts)
        {
            LogBudgetAlert(_logger, alert.Scope.ToString(), alert.ScopeId, alert.ThresholdPercent);
            _alerts.Enqueue(alert);
        }
    }

    /// <summary>Stores the batch's alerts not raised before: unique per (budget, period, threshold), one query for the whole batch.</summary>
    private async Task<List<AlertEvent>> AddNewAlertsAsync(GatewayDbContext db, List<UsageWork> batch, CancellationToken cancellationToken)
    {
        var newAlerts = new List<AlertEvent>();
        var groups = batch.SelectMany(b => b.Alerts).GroupBy(a => (a.Budget.Id, a.Window.Start, a.ThresholdPercent)).ToList();
        if (groups.Count == 0)
        {
            return newAlerts;
        }

        var budgetIds = groups.Select(g => g.Key.Id).Distinct().ToArray();
        var starts = groups.Select(g => g.Key.Start).Distinct().ToArray();
        var raised = (await db.AlertEvents.AsNoTracking()
                .Where(a => budgetIds.Contains(a.BudgetId) && starts.Contains(a.PeriodStart))
                .Select(a => new { a.BudgetId, a.PeriodStart, a.ThresholdPercent })
                .ToListAsync(cancellationToken))
            .Select(a => (a.BudgetId, a.PeriodStart, a.ThresholdPercent))
            .ToHashSet();
        foreach (var group in groups.Where(g => !raised.Contains(g.Key)))
        {
            var (budgetId, start, threshold) = group.Key;
            var a = group.MaxBy(x => x.SpentUsd)!;
            var tokens = a.Budget.Unit == BudgetUnit.Tokens;
            newAlerts.Add(new AlertEvent
            {
                BudgetId = budgetId,
                Scope = a.Budget.Scope,
                ScopeId = a.Budget.ScopeId,
                PeriodStart = start,
                ThresholdPercent = threshold,
                SpentUsd = tokens ? 0 : decimal.Round(a.SpentUsd, 2),
                LimitUsd = tokens ? 0 : a.Budget.LimitUsd,
                SpentTokens = tokens ? (long)a.SpentUsd : null,
                LimitTokens = tokens ? a.Budget.LimitTokens : null,
                Timestamp = _time.GetUtcNow(),
            });
        }

        db.AlertEvents.AddRange(newAlerts);
        await db.SaveChangesAsync(cancellationToken);
        return newAlerts;
    }

    /// <summary>Forgets keys not written for a while, so the map holds only recently active keys.</summary>
    private void PruneLastUsed(DateTimeOffset now)
    {
        if (now - _lastPruned < LastUsedResolution)
        {
            return;
        }

        _lastPruned = now;
        foreach (var (keyId, written) in _lastUsedWritten)
        {
            if (now - written >= LastUsedResolution)
            {
                _lastUsedWritten.Remove(keyId);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to write {Count} usage records ({ExceptionType}); retaining batch for retry")]
    private static partial void LogWriteFailed(ILogger logger, string exceptionType, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Usage journal is full ({MaxRecords} records); keeping {Count} usage records in memory")]
    private static partial void LogJournalFull(ILogger logger, int count, long maxRecords);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Usage journal unavailable ({ExceptionType}) for {Count} usage records")]
    private static partial void LogJournalFailed(ILogger logger, string exceptionType, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Wrote {Count} journaled usage records")]
    private static partial void LogJournalWritten(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Took over a journaled batch of {Count} usage records from a stopped gateway instance")]
    private static partial void LogJournalTakenOver(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Budget alert: {Scope} {ScopeId} reached {Threshold} %")]
    private static partial void LogBudgetAlert(ILogger logger, string scope, Guid scopeId, int threshold);

    [LoggerMessage(Level = LogLevel.Error, Message = "Shutdown timed out with {Count} usage records unwritten")]
    private static partial void LogShutdownDropped(ILogger logger, int count);
}
