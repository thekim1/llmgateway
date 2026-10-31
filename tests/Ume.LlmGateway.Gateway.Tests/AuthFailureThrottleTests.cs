using System.Net;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Ume.LlmGateway.Gateway.Authentication;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>Failed authentications per client address: the gateway stops looking keys up for an address that floods it.</summary>
public sealed class AuthFailureThrottleTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 21, 9, 0, 10, TimeSpan.Zero);

    private static (AuthFailureThrottle Throttle, FakeTimeProvider Time) Create(int perMinute = 3)
    {
        var time = new FakeTimeProvider(Start);
        return (new AuthFailureThrottle(new StaticOptions(new GatewayOptions { AuthFailuresPerMinute = perMinute }), time), time);
    }

    [Fact]
    public void An_address_is_limited_after_its_failures_until_the_minute_ends()
    {
        var (throttle, time) = Create();
        var client = IPAddress.Parse("192.0.2.10");

        throttle.RecordFailure(client);
        throttle.RecordFailure(client);
        throttle.IsLimited(client, out _).ShouldBeFalse();
        throttle.RecordFailure(client);

        throttle.IsLimited(client, out var retryAfter).ShouldBeTrue();
        retryAfter.ShouldBe(50); // 09:00:10, window ends 09:01:00
        throttle.IsLimited(IPAddress.Parse("192.0.2.11"), out _).ShouldBeFalse();

        time.Advance(TimeSpan.FromSeconds(50));
        throttle.IsLimited(client, out _).ShouldBeFalse();
    }

    [Fact]
    public void Ipv6_addresses_in_one_64_share_a_count_and_mapped_ipv4_is_the_ipv4_address()
    {
        var (throttle, _) = Create(perMinute: 2);
        throttle.RecordFailure(IPAddress.Parse("2001:db8:1:2::1"));
        throttle.RecordFailure(IPAddress.Parse("2001:db8:1:2:ffff::9"));

        throttle.IsLimited(IPAddress.Parse("2001:db8:1:2::abcd"), out _).ShouldBeTrue();
        throttle.IsLimited(IPAddress.Parse("2001:db8:1:3::1"), out _).ShouldBeFalse();

        throttle.RecordFailure(IPAddress.Parse("198.51.100.7"));
        throttle.RecordFailure(IPAddress.Parse("::ffff:198.51.100.7"));
        throttle.IsLimited(IPAddress.Parse("198.51.100.7"), out _).ShouldBeTrue();
    }

    [Fact]
    public void Zero_turns_it_off_and_unknown_addresses_are_never_limited()
    {
        var (off, _) = Create(perMinute: 0);
        var client = IPAddress.Parse("192.0.2.10");
        for (var i = 0; i < 100; i++)
        {
            off.RecordFailure(client);
        }

        off.IsLimited(client, out _).ShouldBeFalse();

        var (on, _) = Create(perMinute: 1);
        on.RecordFailure(null);
        on.IsLimited(null, out _).ShouldBeFalse();
    }

    private sealed class StaticOptions(GatewayOptions value) : IOptionsMonitor<GatewayOptions>
    {
        public GatewayOptions CurrentValue => value;
        public GatewayOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<GatewayOptions, string?> listener) => null;
    }
}
