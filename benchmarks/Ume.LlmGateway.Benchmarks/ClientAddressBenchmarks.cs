using System.Net;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Primitives;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Gateway.Pipeline;

namespace Ume.LlmGateway.Benchmarks;

/// <summary>
/// The per-request cost of client address checks: resolving the client behind trusted proxies and a key's address
/// allow-list. The "none" cases are what every request pays when the features are not used.
/// </summary>
[MemoryDiagnoser]
public class ClientAddressBenchmarks
{
    private readonly IPAddress _proxy = IPAddress.Parse("10.0.0.5");
    private readonly IPAddress _client = IPAddress.Parse("203.0.113.9");
    private readonly StringValues _forwardedFor = new("198.51.100.1, 203.0.113.9");
    private readonly IPNetwork[] _trusted = [IPNetwork.Parse("10.0.0.5/32"), IPNetwork.Parse("10.0.1.0/28")];
    private VirtualKey _unrestricted = null!;
    private VirtualKey _restricted = null!;

    [GlobalSetup]
    public void Setup()
    {
        _unrestricted = new VirtualKey { Name = "open", Prefix = "p", KeyHash = "h" };
        _unrestricted.IndexAllowLists();
        _restricted = new VirtualKey
        {
            Name = "office", Prefix = "p", KeyHash = "h",
            // A realistic list: a few office networks and a couple of servers; the match is the last entry.
            AllowedIpRanges = ["192.0.2.0/24", "198.51.100.0/25", "2001:db8::/48", "10.20.0.0/16", "203.0.113.0/24"],
        };
        _restricted.IndexAllowLists();
    }

    [Benchmark(Baseline = true, Description = "Resolve: no trusted proxies")]
    public IPAddress? ResolveWithoutProxies() => ClientAddressMiddleware.Resolve(_proxy, _forwardedFor, []);

    [Benchmark(Description = "Resolve: through a trusted proxy (2 hops)")]
    public IPAddress? ResolveThroughProxy() => ClientAddressMiddleware.Resolve(_proxy, _forwardedFor, _trusted);

    [Benchmark(Description = "Key: no address restriction")]
    public bool UnrestrictedKey() => _unrestricted.IsAddressAllowed(_client);

    [Benchmark(Description = "Key: 5 allowed networks, last matches")]
    public bool RestrictedKey() => _restricted.IsAddressAllowed(_client);
}
