using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>Work item for the background writer: one usage record plus any budget alerts it triggered.</summary>
public sealed record UsageWork(UsageRecord Record, IReadOnlyList<AlertCandidate> Alerts);

/// <summary>
/// Decouples request latency from database writes: usage metadata is queued in a bounded channel and written
/// in batches. When the queue is full callers wait (back-pressure) rather than dropping billing data.
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
    private readonly IHttpClientFactory _httpClients;
    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<UsageWriter> _logger;

    public UsageWriter(IServiceScopeFactory scopes, IHttpClientFactory httpClients, IOptionsMonitor<GatewayOptions> options, TimeProvider time, ILogger<UsageWriter> logger, GatewayMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        _scopes = scopes;
        _httpClients = httpClients;
        _options = options;
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

    public ValueTask EnqueueAsync(UsageWork work, CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(work, cancellationToken);

    /// <summary>Waits until everything queued so far has been written (used by tests and graceful shutdown).</summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        while (_channel.Reader.Count > 0 || Interlocked.CompareExchange(ref _inFlight, 0, 0) > 0)
        {
            await Task.Delay(20, cancellationToken);
        }
    }

    private int _inFlight;

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
                while (true)
                {
                    try
                    {
                        await WriteBatchAsync(batch, stoppingToken);
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

    private async Task WriteBatchAsync(List<UsageWork> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var ids = batch.Select(b => b.Record.Id).ToArray();
        var persisted = (await db.UsageRecords.Where(r => ids.Contains(r.Id)).Select(r => r.Id).ToListAsync(cancellationToken)).ToHashSet();
        db.UsageRecords.AddRange(batch.Select(b => b.Record).Where(r => !persisted.Contains(r.Id)));

        // Alerts: unique per (budget, period, threshold) – skip ones already raised.
        var alerts = batch.SelectMany(b => b.Alerts).ToList();
        var newAlerts = new List<AlertEvent>();
        foreach (var group in alerts.GroupBy(a => (a.Budget.Id, a.Window.Start, a.ThresholdPercent)))
        {
            var (budgetId, start, threshold) = group.Key;
            var exists = await db.AlertEvents.AnyAsync(a => a.BudgetId == budgetId && a.PeriodStart == start && a.ThresholdPercent == threshold, cancellationToken);
            if (!exists)
            {
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

        // Last-used timestamps (one statement per key in the batch).
        foreach (var keyGroup in batch.GroupBy(b => b.Record.VirtualKeyId))
        {
            var last = keyGroup.Max(b => b.Record.Timestamp);
            await db.VirtualKeys.Where(k => k.Id == keyGroup.Key && (k.LastUsedAt == null || k.LastUsedAt < last))
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, last), cancellationToken);
        }

        foreach (var alert in newAlerts)
        {
            LogBudgetAlert(_logger, alert.Scope.ToString(), alert.ScopeId, alert.ThresholdPercent);
            await SendWebhookAsync(alert, cancellationToken);
        }
    }

    private async Task SendWebhookAsync(AlertEvent alert, CancellationToken cancellationToken)
    {
        var url = _options.CurrentValue.AlertWebhookUrl;
        if (url is null)
        {
            return;
        }

        try
        {
            using var client = _httpClients.CreateClient("alerts");
            using var response = await client.PostAsJsonAsync(url, new
            {
                type = "budget_threshold",
                scope = alert.Scope.ToString(),
                scopeId = alert.ScopeId,
                thresholdPercent = alert.ThresholdPercent,
                spentSek = alert.SpentSek,
                limitSek = alert.LimitSek,
                periodStart = alert.PeriodStart,
            }, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            LogWebhookFailed(_logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to write {Count} usage records ({ExceptionType}); retaining batch for retry")]
    private static partial void LogWriteFailed(ILogger logger, string exceptionType, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Budget alert: {Scope} {ScopeId} reached {Threshold} %")]
    private static partial void LogBudgetAlert(ILogger logger, string scope, Guid scopeId, int threshold);

    [LoggerMessage(Level = LogLevel.Error, Message = "Shutdown timed out with {Count} usage records unwritten")]
    private static partial void LogShutdownDropped(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Alert webhook failed")]
    private static partial void LogWebhookFailed(ILogger logger, Exception ex);
}

/// <summary>OpenTelemetry metrics (no content, no key secrets; key prefix is not used as a tag to bound cardinality).</summary>
public sealed class GatewayMetrics
{
    private readonly Counter<long> _requests;
    private readonly Counter<long> _tokens;
    private readonly Counter<double> _costSek;
    private readonly Counter<long> _fallbacks;
    private readonly Histogram<double> _latency;
    private readonly Meter _meter;

    public GatewayMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        _meter = meterFactory.Create(Extensions.TelemetryName);
        _requests = _meter.CreateCounter<long>("ume.gateway.requests", description: "Gateway requests by outcome");
        _tokens = _meter.CreateCounter<long>("ume.gateway.tokens", description: "Tokens processed");
        _costSek = _meter.CreateCounter<double>("ume.gateway.cost", unit: "SEK", description: "Cost in SEK");
        _fallbacks = _meter.CreateCounter<long>("ume.gateway.fallbacks", description: "Fallback attempts");
        _latency = _meter.CreateHistogram<double>("ume.gateway.request.duration", unit: "ms", description: "End-to-end latency");
    }

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
        _latency.Record(record.LatencyMs, tags);
        _tokens.Add(record.InputTokens, new TagList { { "provider", record.ProviderName ?? "none" }, { "direction", "input" } });
        _tokens.Add(record.OutputTokens, new TagList { { "provider", record.ProviderName ?? "none" }, { "direction", "output" } });
        _costSek.Add((double)record.CostSek, new TagList { { "provider", record.ProviderName ?? "none" } });
        if (record.FallbackCount > 0)
        {
            _fallbacks.Add(record.FallbackCount, new TagList { { "endpoint", record.Endpoint.ToString() } });
        }
    }
}
