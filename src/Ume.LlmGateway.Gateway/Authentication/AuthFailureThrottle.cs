using System.Net;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Domain.Networking;

namespace Ume.LlmGateway.Gateway.Authentication;

/// <summary>
/// Counts failed authentications per client address in fixed one-minute windows (<see cref="GatewayOptions.AuthFailuresPerMinute"/>).
/// An address over the limit is not looked up in the database until the window ends; the request handler then accepts only
/// keys it has cached, so clients behind the same proxy as the offender keep working. Per instance and in memory: it
/// protects this instance's database connections, not a global count. IPv6 addresses count per /64, the block a single
/// host usually controls. The check on the request path is one read of an empty set unless some address is over its limit.
/// </summary>
public sealed class AuthFailureThrottle(IOptionsMonitor<GatewayOptions> options, TimeProvider time)
{
    /// <summary>Addresses tracked per window at most; failures from further addresses are not counted (bounded memory).</summary>
    internal const int MaxTrackedAddresses = 100_000;

    private readonly Lock _lock = new();
    private Dictionary<UInt128, int> _failures = [];
    private HashSet<UInt128> _limited = [];
    private long _window = -1;
    private volatile bool _anyLimited;

    /// <summary>True when <paramref name="address"/> is over its limit in the current window; <paramref name="retryAfterSeconds"/> is when it resets.</summary>
    public bool IsLimited(IPAddress? address, out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        if (!_anyLimited || address is null)
        {
            return false;
        }

        var now = time.GetUtcNow();
        lock (_lock)
        {
            Roll(now);
            if (!_limited.Contains(Key(address)))
            {
                return false;
            }
        }

        retryAfterSeconds = Math.Max(1, 60 - now.Second);
        return true;
    }

    /// <summary>Counts one failed authentication from <paramref name="address"/>.</summary>
    public void RecordFailure(IPAddress? address)
    {
        var limit = options.CurrentValue.AuthFailuresPerMinute;
        if (limit <= 0 || address is null)
        {
            return;
        }

        var key = Key(address);
        lock (_lock)
        {
            Roll(time.GetUtcNow());
            if (!_failures.TryGetValue(key, out var count) && _failures.Count >= MaxTrackedAddresses)
            {
                return;
            }

            _failures[key] = ++count;
            if (count >= limit && _limited.Add(key))
            {
                _anyLimited = true;
            }
        }
    }

    private void Roll(DateTimeOffset now)
    {
        var window = now.ToUnixTimeSeconds() / 60;
        if (window == _window)
        {
            return;
        }

        _window = window;
        _failures = [];
        _limited = [];
        _anyLimited = false;
    }

    /// <summary>IPv4 as is; IPv6 by its /64 prefix.</summary>
    private static UInt128 Key(IPAddress address)
    {
        var normalized = NetworkRanges.Normalize(address);
        Span<byte> bytes = stackalloc byte[16];
        if (normalized.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            normalized.TryWriteBytes(bytes[12..], out _);
            bytes[10] = 0xFF; // v4-mapped, so it never equals a v6 prefix
            bytes[11] = 0xFF;
        }
        else
        {
            normalized.TryWriteBytes(bytes, out _);
            bytes[8..].Clear();
        }

        return System.Buffers.Binary.BinaryPrimitives.ReadUInt128BigEndian(bytes);
    }
}
