using System.Text.Json;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.AdminApi.Common;

namespace Ume.LlmGateway.AdminApi.Catalog.Pricing;

/// <summary>
/// Downloads the models.dev list (<see cref="AdminOptions.PriceListUrl"/>, about 6 MB) at most once a day. A failed download
/// keeps the last list and is tried again after 30 minutes.
/// </summary>
public sealed partial class ModelListService(IHttpClientFactory clients, IOptions<AdminOptions> options, TimeProvider time, ILogger<ModelListService> logger)
    : IDisposable
{
    public const string HttpClientName = "model-list";
    private const long MaxBytes = 64 * 1024 * 1024;
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ModelList _list = ModelList.Empty;
    private DateTimeOffset _expires = DateTimeOffset.MinValue;

    public bool Enabled => !string.IsNullOrWhiteSpace(options.Value.PriceListUrl);

    public string? Url => options.Value.PriceListUrl;

    /// <summary>The daily price update runs: <c>Admin:AutoUpdatePrices</c> is on and a list is set.</summary>
    public bool UpdatesPrices => Enabled && options.Value.AutoUpdatePrices;

    /// <summary>When the list was last downloaded.</summary>
    public DateTimeOffset? LoadedAt { get; private set; }

    /// <summary>Why the last download failed, in words for the admin UI; null after a successful one.</summary>
    public string? LastError { get; private set; }

    /// <summary>The cached list; downloaded first when it is older than a day, or always with <paramref name="reload"/>.</summary>
    public async Task<ModelList> GetAsync(CancellationToken ct, bool reload = false)
    {
        if (!Enabled)
        {
            return ModelList.Empty;
        }
        if (!reload && time.GetUtcNow() < _expires)
        {
            return _list;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (!reload && time.GetUtcNow() < _expires)
            {
                return _list;
            }
            try
            {
                _list = await LoadAsync(ct);
                LoadedAt = time.GetUtcNow();
                LastError = null;
                _expires = time.GetUtcNow() + Lifetime;
                LogLoaded(logger, _list.Count);
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException ||
                                      (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                LogUnavailable(logger, e.Message);
                // Shown in the admin UI: the kind of failure, never a message that could hold an address or a response.
                LastError = e switch
                {
                    HttpRequestException { StatusCode: { } status } => $"The price list answered {(int)status}.",
                    HttpRequestException => "The price list could not be reached.",
                    OperationCanceledException => "The price list did not answer in time.",
                    _ => "The price list could not be read.",
                };
                _expires = time.GetUtcNow() + RetryAfterFailure;
            }
            return _list;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ModelList> LoadAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        using var client = clients.CreateClient(HttpClientName);
        client.MaxResponseContentBufferSize = MaxBytes;
        var body = await client.GetByteArrayAsync(new Uri(options.Value.PriceListUrl!), deadline.Token);
        using var document = JsonDocument.Parse(body);
        return ModelList.Parse(document);
    }

    public void Dispose() => _gate.Dispose();

    [LoggerMessage(Level = LogLevel.Information, Message = "Model list loaded: {Count} models")]
    private static partial void LogLoaded(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Model list unavailable, prices and details are not updated: {Reason}")]
    private static partial void LogUnavailable(ILogger logger, string reason);
}
