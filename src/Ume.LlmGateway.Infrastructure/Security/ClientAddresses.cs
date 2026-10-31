using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Primitives;
using Ume.LlmGateway.Domain.Networking;

namespace Ume.LlmGateway.Infrastructure.Security;

/// <summary>How much of a client address is kept where it is logged or stored.</summary>
public enum SourceAddressMode
{
    /// <summary>IPv4 /24 or IPv6 /48.</summary>
    Truncated = 0,
    Full = 1,
    None = 2,
}

/// <summary>
/// The client address behind trusted reverse proxies, and how it is written down. Shared by the gateway (key address
/// restrictions, refused credentials) and the admin API (who made a change, from where).
/// </summary>
public static class ClientAddresses
{
    public const string ForwardedForHeader = "X-Forwarded-For";

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

    /// <summary>Applies <see cref="SourceAddressMode"/>: truncated keeps IPv4 /24 or IPv6 /48.</summary>
    public static string? Format(IPAddress? address, SourceAddressMode mode)
    {
        if (address is null || mode == SourceAddressMode.None)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (mode == SourceAddressMode.Full)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        bytes.AsSpan(address.AddressFamily == AddressFamily.InterNetwork ? 3 : 6).Clear();
        return new IPAddress(bytes).ToString();
    }
}
