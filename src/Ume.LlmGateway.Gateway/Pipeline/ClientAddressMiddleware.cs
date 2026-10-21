using System.Collections.Immutable;
using System.Net;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>
/// Replaces the connection address with the client address from <c>X-Forwarded-For</c> when, and only when, the request
/// came through a trusted proxy (admin UI, Settings). Everything downstream (key address allow-lists, refused-credential
/// records, logs) then sees the real client. With no trusted proxies the header is ignored and nothing is parsed.
/// </summary>
public sealed partial class ClientAddressMiddleware(RequestDelegate next, GatewayCatalog catalog, ILogger<ClientAddressMiddleware> logger)
{
    public const string ForwardedForHeader = ClientAddresses.ForwardedForHeader;

    public Task InvokeAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (catalog.Current is { } snapshot)
        {
            Apply(http, snapshot.TrustedProxies);
            return next(http);
        }

        return LoadThenInvokeAsync(http);
    }

    // Only right after start or a config change; the request handler would load the catalogue next anyway.
    private async Task LoadThenInvokeAsync(HttpContext http)
    {
        try
        {
            Apply(http, (await catalog.GetAsync(http.RequestAborted)).TrustedProxies);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The handler meets the same failure and answers it in the client API's error format.
            LogCatalogUnavailable(logger, ex);
        }

        await next(http);
    }

    private static void Apply(HttpContext http, ImmutableArray<IPNetwork> trusted)
    {
        if (trusted.IsEmpty)
        {
            return;
        }

        var remote = http.Connection.RemoteIpAddress;
        var client = ClientAddresses.Resolve(remote, http.Request.Headers[ForwardedForHeader], trusted.AsSpan());
        if (!ReferenceEquals(client, remote))
        {
            http.Connection.RemoteIpAddress = client;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Trusted proxies could not be read with the catalogue; the connection address is used for this request")]
    private static partial void LogCatalogUnavailable(ILogger logger, Exception ex);
}
