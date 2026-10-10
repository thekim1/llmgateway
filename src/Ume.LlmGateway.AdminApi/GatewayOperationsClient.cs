using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.AdminApi;

public sealed partial class GatewayOperationsClient(
    IHttpClientFactory clients, IOptions<GatewayLinkOptions> gateway, IOptions<GatewaySecurityOptions> security,
    TimeProvider time, ILogger<GatewayOperationsClient> logger)
{
    public async Task<GatewayOperationsStatus?> ReadAsync(CancellationToken ct)
    {
        var address = gateway.Value.OperationsAddress;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https")
        {
            LogUnavailable(logger, "Missing HTTPS operations address");
            return null;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, OperationsSignature.Path));
            var timestamp = time.GetUtcNow().ToUnixTimeSeconds();
            request.Headers.Add(OperationsSignature.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
            request.Headers.Add(OperationsSignature.SignatureHeader, OperationsSignature.Sign(security.Value.KeyPepper, timestamp));
            using var client = clients.CreateClient("gateway-operations");
            using var response = await client.SendAsync(request, deadline.Token);
            response.EnsureSuccessStatusCode();
            var status = await response.Content.ReadFromJsonAsync<GatewayOperationsStatus>(deadline.Token);
            if (status is null || status.Status is not ("Healthy" or "Degraded" or "Unhealthy") || string.IsNullOrWhiteSpace(status.Version) ||
                status.UsageWriter is null || status.UsageWriter.QueueDepth < 0 || status.UsageWriter.Capacity < 1 ||
                status.UsageWriter.QueueDepth > status.UsageWriter.Capacity || status.UsageWriter.InFlightRecords < 0 ||
                status.UsageWriter.ConsecutiveFailures < 0)
            {
                LogUnavailable(logger, "Invalid operations response");
                return null;
            }
            return status;
        }
        catch (HttpRequestException)
        {
            LogUnavailable(logger, "HTTP or TLS failure");
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LogUnavailable(logger, "Probe timed out");
            return null;
        }
        catch (JsonException)
        {
            LogUnavailable(logger, "Invalid operations JSON");
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gateway operations unavailable: {Reason}")]
    private static partial void LogUnavailable(ILogger logger, string reason);
}
