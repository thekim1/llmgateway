using System.Collections.Immutable;
using System.Net;
using Microsoft.Extensions.Primitives;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>
/// Replaces the connection address with the client address from <c>X-Forwarded-For</c> when, and only when, the request
/// came through a trusted proxy (admin UI, Settings). Everything downstream (key address allow-lists, refused-credential
/// records, logs) then sees the real client. With no trusted proxies the header is ignored and nothing is parsed.
/// </summary>
public sealed partial class ClientAddressMiddleware(RequestDelegate next, GatewayCatalog catalog, ILogger<ClientAddressMiddleware> logger)
{
    public const string ForwardedForHeader = "X-Forwarded-For";

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
        var client = Resolve(remote, http.Request.Headers[ForwardedForHeader], trusted.AsSpan());
        if (!ReferenceEquals(client, remote))
        {
            http.Connection.RemoteIpAddress = client;
        }
    }

    /// <summary>
    /// Walks <c>X-Forwarded-For</c> from the right (the hop nearest the gateway) while each address is a trusted proxy, and
    /// returns the first one that is not: the client. A client can prepend anything to the header, so entries left of the
    /// first untrusted address are never believed. An unreadable entry stops the walk at the last trusted hop.
    /// Returns <paramref name="remote"/> itself when it is not a trusted proxy or there is no header.
    /// </summary>
    public static IPAddress? Resolve(IPAddress? remote, StringValues forwardedFor, ReadOnlySpan<IPNetwork> trusted)
    {
        if (remote is null || trusted.IsEmpty || StringValues.IsNullOrEmpty(forwardedFor) || !NetworkRanges.Contains(trusted, remote))
        {
            return remote;
        }

        var current = NetworkRanges.Normalize(remote);
        var hops = 0;
        // Several header lines are one list in order, so the last line holds the nearest hops.
        for (var line = forwardedFor.Count - 1; line >= 0; line--)
        {
            var rest = (forwardedFor[line] ?? string.Empty).AsSpan();
            while (!rest.IsEmpty)
            {
                var comma = rest.LastIndexOf(',');
                var entry = rest[(comma + 1)..].Trim();
                rest = comma < 0 ? [] : rest[..comma];
                if (entry.IsEmpty)
                {
                    continue;
                }

                if (++hops > MaxHops || !TryParseHop(entry, out var hop))
                {
                    return current;
                }

                current = hop;
                if (!NetworkRanges.Contains(trusted, hop))
                {
                    return current;
                }
            }
        }

        return current;
    }

    /// <summary>More hops than any real proxy chain; the rest of a longer header is not read.</summary>
    private const int MaxHops = 20;

    // "192.0.2.1", "2001:db8::1", "[2001:db8::1]:443" and "192.0.2.1:1234" (some proxies keep the port).
    private static bool TryParseHop(ReadOnlySpan<char> entry, out IPAddress address)
    {
        if (IPEndPoint.TryParse(entry, out var endpoint))
        {
            address = NetworkRanges.Normalize(endpoint.Address);
            return true;
        }

        address = IPAddress.None;
        return false;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Trusted proxies could not be read with the catalogue; the connection address is used for this request")]
    private static partial void LogCatalogUnavailable(ILogger logger, Exception ex);
}
