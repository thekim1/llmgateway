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
    private readonly Channel<AlertEvent> _channel = Channel.CreateBounded<AlertEvent>(new BoundedChannelOptions(Capacity)
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

    /// <summary>Queues the alert for the webhook, if one is configured. Never waits.</summary>
    public void Enqueue(AlertEvent alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        if (_options.CurrentValue.AlertWebhookUrl is not null && !_channel.Writer.TryWrite(alert))
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
        await foreach (var alert in _channel.Reader.ReadAllAsync(CancellationToken.None))
        {
            await SendAsync(alert);
        }
    }

    private async Task SendAsync(AlertEvent alert)
    {
        if (_options.CurrentValue.AlertWebhookUrl is not { } url)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(SendTimeout);
        try
        {
            using var client = _httpClients.CreateClient(HttpClientName);
            using var response = await client.PostAsJsonAsync(url, new
            {
                type = "budget_threshold",
                scope = alert.Scope.ToString(),
                scopeId = alert.ScopeId,
                thresholdPercent = alert.ThresholdPercent,
                spentSek = alert.SpentSek,
                limitSek = alert.LimitSek,
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
