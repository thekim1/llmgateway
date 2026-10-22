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

    /// <summary>Most records written at once. COPY makes a large batch cheap; under load the queue drains in a few writes.</summary>
    private const int MaxBatch = 5_000;
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
    private DateTimeOffset _lastPruned;

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
        var batch = new List<UsageWork>(MaxBatch);
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
        foreach (var (alert, sekPerUsd) in newAlerts)
        {
            LogBudgetAlert(_logger, alert.Scope.ToString(), alert.ScopeId, alert.ThresholdPercent);
            _alerts.Enqueue(alert, sekPerUsd);
        }
    }

    /// <summary>Stores the batch's alerts not raised before: unique per (budget, period, threshold), one query for the whole batch.</summary>
    private async Task<List<(AlertEvent Alert, decimal SekPerUsd)>> AddNewAlertsAsync(GatewayDbContext db, List<UsageWork> batch, CancellationToken cancellationToken)
    {
        var newAlerts = new List<(AlertEvent Alert, decimal SekPerUsd)>();
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
            newAlerts.Add((new AlertEvent
            {
                BudgetId = budgetId,
                Scope = a.Budget.Scope,
                ScopeId = a.Budget.ScopeId,
                PeriodStart = start,
                ThresholdPercent = threshold,
                SpentUsd = decimal.Round(a.SpentUsd, 2),
                LimitUsd = a.Budget.LimitUsd,
                Timestamp = _time.GetUtcNow(),
            }, a.SekPerUsd));
        }

        db.AlertEvents.AddRange(newAlerts.Select(n => n.Alert));
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
    private readonly Histogram<double> _overhead;
    private readonly Histogram<double> _timeToFirstToken;
    private readonly Histogram<double> _sessionDuration;
    private readonly UpDownCounter<long> _openSessions;
    private readonly Counter<double> _audioSeconds;
    private readonly Histogram<double> _streamMaxGap;
    private readonly Histogram<double> _streamMaxWrite;
    private readonly Counter<long> _streamStalls;
    private readonly Counter<double> _costUsd;
    private readonly Histogram<long>? _genAiTokens;
    private readonly Histogram<double>? _genAiClientDuration;
    private readonly Histogram<double>? _genAiServerDuration;
    private readonly Histogram<double>? _genAiTimeToFirstToken;
    private readonly Meter _meter;

    // Bucket boundaries recommended by the OpenTelemetry GenAI semantic conventions.
    private static readonly IReadOnlyList<long> TokenBuckets = [1, 4, 16, 64, 256, 1024, 4096, 16384, 65536, 262144, 1048576, 4194304, 16777216, 67108864];
    private static readonly IReadOnlyList<double> DurationBuckets = [0.01, 0.02, 0.04, 0.08, 0.16, 0.32, 0.64, 1.28, 2.56, 5.12, 10.24, 20.48, 40.96, 81.92];
    private static readonly IReadOnlyList<double> FirstTokenBuckets = [0.001, 0.005, 0.01, 0.02, 0.04, 0.06, 0.08, 0.1, 0.25, 0.5, 0.75, 1.0, 2.5, 5.0, 7.5, 10.0];

    public GatewayMetrics(IMeterFactory meterFactory, Microsoft.Extensions.Options.IOptions<GatewayOptions> options)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        ArgumentNullException.ThrowIfNull(options);
        _meter = meterFactory.Create(Extensions.TelemetryName);
        _requests = _meter.CreateCounter<long>("ume.gateway.requests", description: "Gateway requests by outcome");
        _tokens = _meter.CreateCounter<long>("ume.gateway.tokens", description: "Tokens processed");
        _costSek = _meter.CreateCounter<double>("ume.gateway.cost", unit: "SEK", description: "Cost in SEK at the current exchange rate (to be removed with SEK; use ume.gateway.cost.usd)");
        _costUsd = _meter.CreateCounter<double>("ume.gateway.cost.usd", unit: "USD", description: "Cost in USD, the currency prices and budgets are in");

        _fallbacks = _meter.CreateCounter<long>("ume.gateway.fallbacks", description: "Fallback attempts");
        _ruleRouted = _meter.CreateCounter<long>("ume.gateway.routing.rule_routed", description: "Requests routed by a routing rule (no per-rule label: rule names are unbounded)");
        _latency = _meter.CreateHistogram<double>("ume.gateway.request.duration", unit: "ms", description: "End-to-end latency");
        _overhead = _meter.CreateHistogram<double>("ume.gateway.overhead", unit: "ms", description: "The gateway's own time: end-to-end latency minus time spent waiting on providers");
        _timeToFirstToken = _meter.CreateHistogram<double>("ume.gateway.time_to_first_token", unit: "ms", description: "Streamed answers: time until the first event reached the client");
        _sessionDuration = _meter.CreateHistogram<double>("ume.gateway.realtime.session.duration", unit: "s", description: "Length of live audio sessions");
        _openSessions = _meter.CreateUpDownCounter<long>("ume.gateway.realtime.sessions", description: "Live audio sessions open on this instance");
        _audioSeconds = _meter.CreateCounter<double>("ume.gateway.audio", unit: "s", description: "Seconds of audio processed");
        _streamMaxGap = _meter.CreateHistogram<double>("ume.gateway.stream.max_upstream_gap", unit: "ms", description: "Streamed answers: longest wait for the provider's next event after the first");
        _streamMaxWrite = _meter.CreateHistogram<double>("ume.gateway.stream.max_write", unit: "ms", description: "Streamed answers: longest time the gateway spent writing one event to the client");
        _streamStalls = _meter.CreateCounter<long>("ume.gateway.stream.stalls", description: "Waits of a second or more for a provider's next event, mid-stream");

        // OpenTelemetry GenAI semantic conventions, so standard GenAI dashboards work (Gateway:GenAiMetrics, off by default:
        // four more histogram recordings per request). Model attributes are the catalogue's upstream model names
        // (bounded), never the name a client sent.
        if (!options.Value.GenAiMetrics)
        {
            return;
        }

        _genAiTokens = _meter.CreateHistogram<long>("gen_ai.client.token.usage", unit: "{token}", description: "Tokens used per request to a provider",
            advice: new InstrumentAdvice<long> { HistogramBucketBoundaries = TokenBuckets });
        _genAiClientDuration = _meter.CreateHistogram<double>("gen_ai.client.operation.duration", unit: "s", description: "Time spent waiting on providers per request",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = DurationBuckets });
        _genAiServerDuration = _meter.CreateHistogram<double>("gen_ai.server.request.duration", unit: "s", description: "Gateway request duration, end to end",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = DurationBuckets });
        _genAiTimeToFirstToken = _meter.CreateHistogram<double>("gen_ai.server.time_to_first_token", unit: "s", description: "Streamed answers: time until the first event reached the client",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = FirstTokenBuckets });
    }

    /// <summary>Once per stream: its longest provider gap and client write, and its stalls.</summary>
    public void StreamGaps(string provider, GatewayEndpoint endpoint, double maxUpstreamGapMs, double maxWriteMs, int stalls)
    {
        var tags = new TagList { { "provider", provider }, { "endpoint", endpoint.ToString() } };
        _streamMaxGap.Record(maxUpstreamGapMs, tags);
        _streamMaxWrite.Record(maxWriteMs, tags);
        if (stalls > 0)
        {
            _streamStalls.Add(stalls, tags);
        }
    }

    public void SessionOpened(GatewayEndpoint endpoint) => _openSessions.Add(1, new TagList { { "endpoint", endpoint.ToString() } });

    public void SessionClosed(GatewayEndpoint endpoint) => _openSessions.Add(-1, new TagList { { "endpoint", endpoint.ToString() } });

    public void ObserveQueueDepth(Func<int> depth) =>
        _meter.CreateObservableGauge("ume.gateway.usage_queue.depth", depth, description: "Usage records waiting to be written");

    /// <summary>The <c>gen_ai.provider.name</c> of a provider type (the semantic conventions' names where there is one).</summary>
    public static string GenAiProviderName(ProviderType type) => type switch
    {
        ProviderType.OpenAI => "openai",
        ProviderType.AzureOpenAI => "azure.ai.openai",
        ProviderType.AzureAIFoundry => "azure.ai.inference",
        ProviderType.Anthropic => "anthropic",
        ProviderType.Ollama or ProviderType.OllamaCloud => "ollama",
        _ => "openai_compatible",
    };

    /// <summary>The <c>gen_ai.operation.name</c> of an endpoint.</summary>
    public static string GenAiOperationName(GatewayEndpoint endpoint) => endpoint switch
    {
        GatewayEndpoint.ChatCompletions or GatewayEndpoint.Responses or GatewayEndpoint.AnthropicMessages => "chat",
        GatewayEndpoint.Embeddings => "embeddings",
        _ => GatewayEndpoints.Info(endpoint).RoutingName,
    };

    /// <param name="record">The request's usage record.</param>
    /// <param name="providerType">The type of the provider the request went to; with it the GenAI metrics are recorded too.</param>
    public void Record(UsageRecord record, ProviderType? providerType = null)
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
            if (record.UpstreamMs is { } upstream)
            {
                _overhead.Record(Math.Max(0, record.LatencyMs - upstream), tags);
            }

            if (record.TimeToFirstTokenMs is { } firstToken)
            {
                _timeToFirstToken.Record(firstToken, tags);
            }
        }

        if (record.AudioSeconds > 0)
        {
            _audioSeconds.Add((double)record.AudioSeconds, new TagList { { "provider", record.ProviderName ?? "none" }, { "endpoint", record.Endpoint.ToString() } });
        }

        _tokens.Add(record.InputTokens, new TagList { { "provider", record.ProviderName ?? "none" }, { "direction", "input" } });
        _tokens.Add(record.OutputTokens, new TagList { { "provider", record.ProviderName ?? "none" }, { "direction", "output" } });
        _costSek.Add((double)record.CostSek, new TagList { { "provider", record.ProviderName ?? "none" } });
        _costUsd.Add((double)record.CostUsd, new TagList { { "provider", record.ProviderName ?? "none" } });
        if (_genAiTokens is not null && providerType is { } type && record.UpstreamModel is { } model && !GatewayEndpoints.Info(record.Endpoint).IsRealtime)
        {
            RecordGenAi(record, type, model);
        }
        if (record.RoutingRuleId is not null)
        {
            _ruleRouted.Add(1, new TagList { { "endpoint", record.Endpoint.ToString() } });
        }

        if (record.FallbackCount > 0)
        {
            _fallbacks.Add(record.FallbackCount, new TagList { { "endpoint", record.Endpoint.ToString() } });
        }
    }

    private void RecordGenAi(UsageRecord record, ProviderType type, string model)
    {
        var tags = new TagList
        {
            { "gen_ai.operation.name", GenAiOperationName(record.Endpoint) },
            { "gen_ai.provider.name", GenAiProviderName(type) },
            { "gen_ai.request.model", model },
        };
        if (record.Outcome != RequestOutcome.Success)
        {
            tags.Add("error.type", record.ErrorCode ?? record.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        _genAiServerDuration!.Record(record.LatencyMs / 1000d, tags);
        if (record.UpstreamMs is { } upstream)
        {
            _genAiClientDuration!.Record(upstream / 1000d, tags);
        }

        if (record.TimeToFirstTokenMs is { } firstToken)
        {
            _genAiTimeToFirstToken!.Record(firstToken / 1000d, tags);
        }

        if (record.Outcome == RequestOutcome.Success)
        {
            var input = tags;
            input.Add("gen_ai.token.type", "input");
            _genAiTokens!.Record(record.InputTokens, input);
            if (GatewayEndpoints.Info(record.Endpoint).HasOutputTokens)
            {
                var output = tags;
                output.Add("gen_ai.token.type", "output");
                _genAiTokens!.Record(record.OutputTokens, output);
            }
        }
    }
}
