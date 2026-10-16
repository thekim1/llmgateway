using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Domain.Entities;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>
/// Sends budget alerts to the optional webhook (<c>Gateway:AlertWebhookUrl</c>) from its own bounded queue, so a slow or
/// unreachable receiver never holds up the usage writer. Each alert is posted once with a short timeout and no retries
/// (a retried POST could notify twice); alerts that do not fit in the queue are dropped and logged. The alert itself is
/// already stored as an <see cref="AlertEvent"/> before it is queued here.
/// </summary>
public sealed partial class AlertNotifier : BackgroundService
{
    /// <summary>The named HTTP client; registered without the standard resilience handler.</summary>
    public const string HttpClientName = "alerts";

    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    private const int Capacity = 1_000;
    private readonly Channel<(AlertEvent Alert, decimal SekPerUsd)> _channel = Channel.CreateBounded<(AlertEvent, decimal)>(new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
    });

    private readonly IHttpClientFactory _httpClients;
    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly ILogger<AlertNotifier> _logger;

    public AlertNotifier(IHttpClientFactory httpClients, IOptionsMonitor<GatewayOptions> options, ILogger<AlertNotifier> logger)
    {
        _httpClients = httpClients;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Queues the alert for the webhook, if one is configured. Never waits. <paramref name="sekPerUsd"/> is the rate when it
    /// was raised, for the SEK amounts kept in the payload (0: the configured fallback rate).
    /// </summary>
    public void Enqueue(AlertEvent alert, decimal sekPerUsd)
    {
        ArgumentNullException.ThrowIfNull(alert);
        if (_options.CurrentValue.AlertWebhookUrl is not null && !_channel.Writer.TryWrite((alert, sekPerUsd)))
        {
            LogDropped(_logger, alert.Scope.ToString(), alert.ScopeId, alert.ThresholdPercent);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Alerts already queued are still sent (each within SendTimeout) while the host's shutdown time allows.
        _channel.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (alert, sekPerUsd) in _channel.Reader.ReadAllAsync(CancellationToken.None))
        {
            await SendAsync(alert, sekPerUsd > 0 ? sekPerUsd : _options.CurrentValue.FallbackSekPerUsd);
        }
    }

    private async Task SendAsync(AlertEvent alert, decimal sekPerUsd)
    {
        if (_options.CurrentValue.AlertWebhookUrl is not { } url)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(SendTimeout);
        try
        {
            // Amounts are in USD; the SEK fields are the same amounts at the rate then, kept for existing receivers.
            using var client = _httpClients.CreateClient(HttpClientName);
            using var response = await client.PostAsJsonAsync(url, new
            {
                type = "budget_threshold",
                scope = alert.Scope.ToString(),
                scopeId = alert.ScopeId,
                thresholdPercent = alert.ThresholdPercent,
                spentUsd = alert.SpentUsd,
                limitUsd = alert.LimitUsd,
                spentSek = decimal.Round(alert.SpentUsd * sekPerUsd, 2),
                limitSek = decimal.Round(alert.LimitUsd * sekPerUsd, 2),
                periodStart = alert.PeriodStart,
            }, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            LogWebhookFailed(_logger, ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Alert webhook failed ({ExceptionType}); the alert is not resent")]
    private static partial void LogWebhookFailed(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Alert webhook queue full: alert for {Scope} {ScopeId} at {Threshold} % not sent (it is still stored)")]
    private static partial void LogDropped(ILogger logger, string scope, Guid scopeId, int threshold);
}
