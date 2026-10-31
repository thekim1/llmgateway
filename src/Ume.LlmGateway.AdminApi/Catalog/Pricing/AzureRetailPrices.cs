using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.AdminApi.Common;

namespace Ume.LlmGateway.AdminApi.Catalog.Pricing;

/// <summary>
/// Reads the Foundry Models meters of one Azure region (<see cref="AdminOptions.AzurePriceRegion"/>) from the public
/// Azure retail prices API, no credential needed, and keeps them for 12 hours. A failure only means no suggestions.
/// </summary>
public sealed partial class AzureRetailPrices(IHttpClientFactory clients, IOptions<AdminOptions> options, TimeProvider time, ILogger<AzureRetailPrices> logger)
    : IDisposable
{
    public const string HttpClientName = "azure-retail-prices";
    private const string Host = "prices.azure.com";
    private const int MaxPages = 20;
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<AzurePriceMeter> _meters = [];
    private DateTimeOffset _expires = DateTimeOffset.MinValue;

    public async Task<IReadOnlyList<AzurePriceMeter>> GetAsync(CancellationToken ct)
    {
        if (time.GetUtcNow() < _expires)
        {
            return _meters;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (time.GetUtcNow() < _expires)
            {
                return _meters;
            }
            try
            {
                _meters = await LoadAsync(ct);
                _expires = time.GetUtcNow() + Lifetime;
            }
            catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException ||
                                      (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                LogUnavailable(logger, e.Message);
                _expires = time.GetUtcNow() + RetryAfterFailure;
            }
            return _meters;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<AzurePriceMeter>> LoadAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var filter = $"serviceName eq 'Foundry Models' and armRegionName eq '{options.Value.AzurePriceRegion}' and type eq 'Consumption'";
        Uri? next = new($"https://{Host}/api/retail/prices?$filter={Uri.EscapeDataString(filter)}");
        var meters = new List<AzurePriceMeter>();
        using var client = clients.CreateClient(HttpClientName);
        for (var page = 0; next is not null && page < MaxPages; page++)
        {
            var body = JsonNode.Parse(await client.GetStringAsync(next, deadline.Token)) as JsonObject
                ?? throw new InvalidOperationException("The price list page is not a JSON object.");
            foreach (var item in (body["Items"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (Text(item, "meterName") is { } meter && item["unitPrice"] is JsonValue v && v.TryGetValue<decimal>(out var unitPrice) &&
                    AzurePriceMatcher.Parse(Text(item, "productName") ?? string.Empty, meter, Text(item, "unitOfMeasure") ?? string.Empty, unitPrice,
                        Text(item, "type") ?? string.Empty) is { } parsed)
                {
                    meters.Add(parsed);
                }
            }
            // Only the price API itself is followed.
            next = Text(body, "NextPageLink") is { } link && Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
                   string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase) ? uri : null;
        }
        return meters;
    }

    public void Dispose() => _gate.Dispose();

    private static string? Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Azure retail prices unavailable, Foundry models get no suggested price: {Reason}")]
    private static partial void LogUnavailable(ILogger logger, string reason);
}
