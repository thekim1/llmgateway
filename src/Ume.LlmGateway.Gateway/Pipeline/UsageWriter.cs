using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>Work item for the background writer: one usage record plus any budget alerts it triggered.</summary>
public sealed record UsageWork(UsageRecord Record, IReadOnlyList<AlertCandidate> Alerts);

/// <summary>
/// Decouples request latency from database writes: usage metadata is queued in a bounded channel and written
/// in batches. When the queue is full callers wait (back-pressure) for a bounded time; a record that still cannot be
/// queued then is dropped and logged by the caller (<c>GatewayRequestHandler</c>) rather than holding the request
/// forever. A batch that fails to write is kept and retried, never dropped. Budget alerts are stored with the batch
/// and handed to <see cref="AlertNotifier"/> once it has committed.
/// </summary>
public sealed partial class UsageWriter : BackgroundService
{
    private const int Capacity = 10_000;
    private readonly Channel<UsageWork> _channel = Channel.CreateBounded<UsageWork>(new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
    });

    private readonly IServiceScopeFactory _scopes;
    private readonly AlertNotifier _alerts;
    private readonly TimeProvider _time;
    private readonly ILogger<UsageWriter> _logger;

    public UsageWriter(IServiceScopeFactory scopes, AlertNotifier alerts, TimeProvider time, ILogger<UsageWriter> logger, GatewayMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        _scopes = scopes;
        _alerts = alerts;
        _time = time;
        _logger = logger;
        metrics.ObserveQueueDepth(() => _channel.Reader.Count);
    }

    public int QueueDepth => _channel.Reader.Count;
    private int _consecutiveFailures;
    private int _inFlightRecords;
    private long _lastWriteTicks;
    public UsageWriterStatus Status => new(QueueDepth, Capacity, Volatile.Read(ref _inFlightRecords),
        Interlocked.Read(ref _lastWriteTicks) is > 0 and var ticks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null,
        Volatile.Read(ref _consecutiveFailures));

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

    /// <summary>Waits until everything queued so far has been written (used by tests and graceful shutdown).</summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        while (Volatile.Read(ref _pending) > 0 || _channel.Reader.Count > 0 || Interlocked.CompareExchange(ref _inFlight, 0, 0) > 0)
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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Drain queued usage before shutdown so billing data is not lost on deploys.
        try
        {
            await FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            LogShutdownDropped(_logger, _channel.Reader.Count);
        }

        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<UsageWork>(500);
        while (await _channel.Reader.WaitToReadAsync(stoppingToken))
        {
            Interlocked.Increment(ref _inFlight);
            try
            {
                while (batch.Count < 500 && _channel.Reader.TryRead(out var item))
                {
                    batch.Add(item);
                }

                Volatile.Write(ref _inFlightRecords, batch.Count);
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        await WriteBatchAsync(batch, retry: attempt > 0, stoppingToken);
                        Interlocked.Exchange(ref _lastWriteTicks, _time.GetUtcNow().UtcTicks);
                        Volatile.Write(ref _consecutiveFailures, 0);
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Interlocked.Increment(ref _consecutiveFailures);
                        LogWriteFailed(_logger, ex.GetType().Name, batch.Count);
                        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    }
                }
            }
            finally
            {
                batch.Clear();
                Volatile.Write(ref _inFlightRecords, 0);
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    private async Task WriteBatchAsync(List<UsageWork> batch, bool retry, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        HashSet<long> persisted = [];
        if (retry)
        {
            // Only a retried batch can be partly written already (e.g. the insert committed, a later step failed).
            var ids = batch.Select(b => b.Record.Id).ToArray();
            persisted = [.. await db.UsageRecords.Where(r => ids.Contains(r.Id)).Select(r => r.Id).ToListAsync(cancellationToken)];
        }

        db.UsageRecords.AddRange(batch.Select(b => b.Record).Where(r => !persisted.Contains(r.Id)));

        // Alerts: unique per (budget, period, threshold) – skip ones already raised (one query for the whole batch).
        var newAlerts = new List<AlertEvent>();
        var groups = batch.SelectMany(b => b.Alerts).GroupBy(a => (a.Budget.Id, a.Window.Start, a.ThresholdPercent)).ToList();
        if (groups.Count > 0)
        {
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
                var a = group.MaxBy(x => x.SpentSek)!;
                newAlerts.Add(new AlertEvent
                {
                    BudgetId = budgetId,
                    Scope = a.Budget.Scope,
                    ScopeId = a.Budget.ScopeId,
                    PeriodStart = start,
                    ThresholdPercent = threshold,
                    SpentSek = decimal.Round(a.SpentSek, 2),
                    LimitSek = a.Budget.LimitSek,
                    Timestamp = _time.GetUtcNow(),
                });
            }
        }

        db.AlertEvents.AddRange(newAlerts);
        await db.SaveChangesAsync(cancellationToken);

        // Committed: notify now. Should a later step fail, the retry finds these alerts stored and does not raise them again.
        foreach (var alert in newAlerts)
        {
            LogBudgetAlert(_logger, alert.Scope.ToString(), alert.ScopeId, alert.ThresholdPercent);
            _alerts.Enqueue(alert);
        }

        // Last-used timestamps: at most one statement per key and minute, instead of one per batch.
        foreach (var keyGroup in batch.GroupBy(b => b.Record.VirtualKeyId))
        {
            var last = keyGroup.Max(b => b.Record.Timestamp);
            if (_lastUsedWritten.TryGetValue(keyGroup.Key, out var written) && last - written < LastUsedResolution)
            {
                continue;
            }

            await db.VirtualKeys.Where(k => k.Id == keyGroup.Key && (k.LastUsedAt == null || k.LastUsedAt < last))
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, last), cancellationToken);
            _lastUsedWritten[keyGroup.Key] = last;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to write {Count} usage records ({ExceptionType}); retaining batch for retry")]
    private static partial void LogWriteFailed(ILogger logger, string exceptionType, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Budget alert: {Scope} {ScopeId} reached {Threshold} %")]
    private static partial void LogBudgetAlert(ILogger logger, string scope, Guid scopeId, int threshold);

    [LoggerMessage(Level = LogLevel.Error, Message = "Shutdown timed out with {Count} usage records unwritten")]
    private static partial void LogShutdownDropped(ILogger logger, int count);
}

/// <summary>OpenTelemetry metrics (no content, no key secrets; key prefix is not used as a tag to bound cardinality).</summary>
public sealed class GatewayMetrics
{
    private readonly Counter<long> _requests;
    private readonly Counter<long> _tokens;
    private readonly Counter<double> _costSek;
    private readonly Counter<long> _fallbacks;
    private readonly Counter<long> _ruleRouted;
    private readonly Histogram<double> _latency;
    private readonly Histogram<double> _sessionDuration;
    private readonly UpDownCounter<long> _openSessions;
    private readonly Counter<double> _audioSeconds;
    private readonly Meter _meter;

    public GatewayMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        _meter = meterFactory.Create(Extensions.TelemetryName);
        _requests = _meter.CreateCounter<long>("ume.gateway.requests", description: "Gateway requests by outcome");
        _tokens = _meter.CreateCounter<long>("ume.gateway.tokens", description: "Tokens processed");
        _costSek = _meter.CreateCounter<double>("ume.gateway.cost", unit: "SEK", description: "Cost in SEK");
        _fallbacks = _meter.CreateCounter<long>("ume.gateway.fallbacks", description: "Fallback attempts");
        _ruleRouted = _meter.CreateCounter<long>("ume.gateway.routing.rule_routed", description: "Requests routed by a routing rule (no per-rule label: rule names are unbounded)");
        _latency = _meter.CreateHistogram<double>("ume.gateway.request.duration", unit: "ms", description: "End-to-end latency");
        _sessionDuration = _meter.CreateHistogram<double>("ume.gateway.realtime.session.duration", unit: "s", description: "Length of live audio sessions");
        _openSessions = _meter.CreateUpDownCounter<long>("ume.gateway.realtime.sessions", description: "Live audio sessions open on this instance");
        _audioSeconds = _meter.CreateCounter<double>("ume.gateway.audio", unit: "s", description: "Seconds of audio processed");
    }

    public void SessionOpened(GatewayEndpoint endpoint) => _openSessions.Add(1, new TagList { { "endpoint", endpoint.ToString() } });

    public void SessionClosed(GatewayEndpoint endpoint) => _openSessions.Add(-1, new TagList { { "endpoint", endpoint.ToString() } });

    public void ObserveQueueDepth(Func<int> depth) =>
        _meter.CreateObservableGauge("ume.gateway.usage_queue.depth", depth, description: "Usage records waiting to be written");

    public void Record(UsageRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var tags = new TagList
        {
            { "outcome", record.Outcome.ToString() },
            { "provider", record.ProviderName ?? "none" },
            { "endpoint", record.Endpoint.ToString() },
        };
        _requests.Add(1, tags);
        if (GatewayEndpoints.Info(record.Endpoint).IsRealtime)
        {
            _sessionDuration.Record(record.LatencyMs / 1000d, tags); // a session's length is not request latency
        }
        else
        {
            _latency.Record(record.LatencyMs, tags);
        }

        if (record.AudioSeconds > 0)
        {
            _audioSeconds.Add((double)record.AudioSeconds, new TagList { { "provider", record.ProviderName ?? "none" }, { "endpoint", record.Endpoint.ToString() } });
        }

        _tokens.Add(record.InputTokens, new TagList { { "provider", record.ProviderName ?? "none" }, { "direction", "input" } });
        _tokens.Add(record.OutputTokens, new TagList { { "provider", record.ProviderName ?? "none" }, { "direction", "output" } });
        _costSek.Add((double)record.CostSek, new TagList { { "provider", record.ProviderName ?? "none" } });
        if (record.RoutingRuleId is not null)
        {
            _ruleRouted.Add(1, new TagList { { "endpoint", record.Endpoint.ToString() } });
        }

        if (record.FallbackCount > 0)
        {
            _fallbacks.Add(record.FallbackCount, new TagList { { "endpoint", record.Endpoint.ToString() } });
        }
    }
}
