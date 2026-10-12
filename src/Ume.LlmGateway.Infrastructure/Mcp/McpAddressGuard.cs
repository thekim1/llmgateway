using System.Net;
using System.Net.Sockets;

namespace Ume.LlmGateway.Infrastructure.Mcp;

/// <summary>Where an address points, for the SSRF rules of MCP server and token URLs.</summary>
public enum McpAddressClass
{
    Public = 0,

    /// <summary>Loopback, RFC 1918, carrier-grade NAT, unique or site-local IPv6: municipal servers live here, so only gateway admins may name it.</summary>
    Private = 1,

    /// <summary>Link-local (cloud metadata lives here), cloud metadata addresses, unspecified, multicast and reserved: never.</summary>
    Forbidden = 2,
}

/// <summary>
/// The address rules for connections to MCP servers and their token endpoints. Every MCP connection of the gateway and the
/// admin API goes through <see cref="ConnectAsync"/>, which checks the addresses a name resolves to at connect time, so a
/// name that later resolves to a forbidden address (DNS rebinding) is never reached. Private addresses are allowed there;
/// who may name them is checked when a URL is saved.
/// </summary>
public static class McpAddressGuard
{
    private static readonly IPAddress[] MetadataAddresses =
    [
        IPAddress.Parse("168.63.129.16"),
        IPAddress.Parse("100.100.100.200"),
        IPAddress.Parse("fd00:ec2::254"),
    ];

    /// <summary>IPv6 addresses that carry an IPv4 address are classified by that address (mapped, compatible, NAT64, 6to4).</summary>
    public static McpAddressClass Classify(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        address = EmbeddedIPv4(address) ?? address;
        if (MetadataAddresses.Contains(address))
        {
            return McpAddressClass.Forbidden;
        }

        if (IPAddress.IsLoopback(address))
        {
            return McpAddressClass.Private;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 0 || (b[0] == 169 && b[1] == 254) || b[0] >= 224 ? McpAddressClass.Forbidden
                : b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] is >= 64 and <= 127)
                    ? McpAddressClass.Private
                : McpAddressClass.Public;
        }

        return address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6Multicast ? McpAddressClass.Forbidden
            : address.IsIPv6UniqueLocal || address.IsIPv6SiteLocal ? McpAddressClass.Private
            : McpAddressClass.Public;
    }

    private static IPAddress? EmbeddedIPv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        var b = address.GetAddressBytes();
        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return new IPAddress(b.AsSpan(2, 4));
        }

        var nat64 = b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0;
        var compatible = b.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0 && !(b[12] == 0 && b[13] == 0 && b[14] == 0 && b[15] <= 1);
        return nat64 || compatible ? new IPAddress(b.AsSpan(12, 4)) : null;
    }

    /// <summary>A <see cref="SocketsHttpHandler.ConnectCallback"/> that refuses to connect when the host resolves to a forbidden address.</summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        if (addresses.Length == 0 || addresses.Any(a => Classify(a) == McpAddressClass.Forbidden))
        {
            throw new HttpRequestException("The MCP server's address is not allowed.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
