using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Ume.LlmGateway.Domain.Networking;

/// <summary>
/// IP address allow-lists: a key's permitted client addresses and the gateway's trusted proxies. Entries are single
/// addresses (<c>192.0.2.10</c>, <c>2001:db8::1</c>) or CIDR networks (<c>10.20.0.0/16</c>), stored in canonical form.
/// </summary>
public static class NetworkRanges
{
    /// <summary>Most entries a key or the trusted-proxy list may hold; the gateway scans them linearly.</summary>
    public const int MaxEntries = 100;

    /// <summary>
    /// Parses one entry. A single address becomes a /32 (IPv4) or /128 (IPv6) network; an IPv4-mapped IPv6 address is
    /// read as its IPv4 address. <paramref name="error"/> is a Swedish message for the admin API.
    /// </summary>
    public static bool TryParse(string? text, out IPNetwork network, out string? error)
    {
        network = default;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 64)
        {
            error = "Enter an IP address or a network in CIDR notation, for example 10.20.0.0/16.";
            return false;
        }

        var slash = value.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            if (!IPAddress.TryParse(value, out var single) || !IsPlainAddress(value, single))
            {
                error = $"'{value}' is not a valid IP address.";
                return false;
            }

            single = Normalize(single);
            network = new IPNetwork(single, single.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
            error = null;
            return true;
        }

        if (!IPAddress.TryParse(value.AsSpan(0, slash), out var address) || !IsPlainAddress(value[..slash], address)
            || !int.TryParse(value.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var prefix)
            || prefix > (address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128))
        {
            error = $"'{value}' is not a valid network in CIDR notation.";
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            error = $"Write '{value}' as an IPv4 network, for example {address.MapToIPv4()}/{Math.Max(0, prefix - 96)}.";
            return false;
        }

        // Host bits set (10.20.1.5/16): say which network was probably meant instead of silently widening the entry.
        var meant = MaskedNetwork(address, prefix);
        if (!IPNetwork.TryParse(meant, out network) || !network.BaseAddress.Equals(address))
        {
            network = default;
            error = $"'{value}' is not a network address. Did you mean {meant}?";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Parses entries the admin API has already validated; invalid ones are skipped rather than widening access.</summary>
    public static IPNetwork[] ParseValid(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var parsed = new List<IPNetwork>();
        foreach (var entry in entries)
        {
            if (TryParse(entry, out var network, out _))
            {
                parsed.Add(network);
            }
        }

        return [.. parsed];
    }

    /// <summary>
    /// Validates and canonicalises a list from the admin API: trimmed, IPv6 in compressed form, duplicates removed.
    /// Returns the first problem as a Swedish message, or null.
    /// </summary>
    public static string? Normalize(IEnumerable<string> entries, out List<string> canonical)
    {
        ArgumentNullException.ThrowIfNull(entries);
        canonical = [];
        var seen = new HashSet<IPNetwork>();
        foreach (var entry in entries)
        {
            if (!TryParse(entry, out var network, out var error))
            {
                return error;
            }

            if (seen.Add(network))
            {
                canonical.Add(Format(network));
            }
        }

        return canonical.Count > MaxEntries ? $"At most {MaxEntries} addresses or networks." : null;
    }

    /// <summary>A /32 or /128 network is written as the bare address.</summary>
    public static string Format(IPNetwork network) =>
        network.PrefixLength == (network.BaseAddress.AddressFamily == AddressFamily.InterNetwork ? 32 : 128)
            ? network.BaseAddress.ToString()
            : network.ToString();

    /// <summary>True when the network covers every address of its family (<c>0.0.0.0/0</c>, <c>::/0</c>).</summary>
    public static bool IsEverything(IPNetwork network) => network.PrefixLength == 0;

    /// <summary>Whether any of <paramref name="networks"/> contains <paramref name="address"/>. No allocation for a plain IPv4 or IPv6 address.</summary>
    public static bool Contains(ReadOnlySpan<IPNetwork> networks, IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        foreach (var network in networks)
        {
            if (network.Contains(address))
            {
                return true;
            }
        }

        return false;
    }

    public static IPAddress Normalize(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    // IPAddress.TryParse accepts "10", "10.1" and scoped IPv6 ("fe80::1%eth0"); an allow-list wants exactly what it says.
    private static bool IsPlainAddress(string text, IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork
            ? text.Count(c => c == '.') == 3
            : address.AddressFamily == AddressFamily.InterNetworkV6 && !text.Contains('%', StringComparison.Ordinal);

    private static string MaskedNetwork(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var keep = Math.Clamp(prefix - (i * 8), 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - keep));
        }

        return $"{new IPAddress(bytes)}/{prefix}";
    }
}
