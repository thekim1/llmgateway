using System.Net;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.Domain.Tests;

public class NetworkRangesTests
{
    [Theory]
    [InlineData("10.20.0.0/16", "10.20.0.0/16")]
    [InlineData(" 192.0.2.10 ", "192.0.2.10")]
    [InlineData("192.0.2.10/32", "192.0.2.10")]
    [InlineData("2001:DB8:0:0::1", "2001:db8::1")]
    [InlineData("2001:db8::/32", "2001:db8::/32")]
    [InlineData("::ffff:192.0.2.10", "192.0.2.10")]
    [InlineData("0.0.0.0/0", "0.0.0.0/0")]
    public void Accepts_addresses_and_networks_in_canonical_form(string input, string canonical)
    {
        NetworkRanges.Normalize([input], out var result).ShouldBeNull();
        result.ShouldBe([canonical]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("10")]
    [InlineData("10.1")]
    [InlineData("example.se")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("10.0.0.0/ 8")]
    [InlineData("fe80::1%eth0")]
    [InlineData("::ffff:10.0.0.0/104")]
    public void Rejects_anything_else(string input)
    {
        NetworkRanges.TryParse(input, out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Network_with_host_bits_suggests_the_network_address()
    {
        NetworkRanges.TryParse("10.20.1.5/16", out _, out var error).ShouldBeFalse();
        error!.ShouldContain("10.20.0.0/16");
    }

    [Fact]
    public void Duplicates_are_removed_and_the_size_is_capped()
    {
        NetworkRanges.Normalize(["192.0.2.1", "192.0.2.1/32", "::ffff:192.0.2.1"], out var result).ShouldBeNull();
        result.ShouldBe(["192.0.2.1"]);

        NetworkRanges.Normalize(Enumerable.Range(0, NetworkRanges.MaxEntries + 1).Select(i => $"10.0.{i / 256}.{i % 256}"), out _).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("10.20.30.40", true)]
    [InlineData("::ffff:10.20.30.40", true)]
    [InlineData("10.21.0.1", false)]
    [InlineData("2001:db8::5", true)]
    [InlineData("2001:db9::5", false)]
    public void Contains_matches_either_family(string address, bool expected)
    {
        IPNetwork[] networks = [IPNetwork.Parse("10.20.0.0/16"), IPNetwork.Parse("2001:db8::/32")];
        NetworkRanges.Contains(networks, IPAddress.Parse(address)).ShouldBe(expected);
    }
}

public class KeyAddressAllowListTests
{
    private static VirtualKey Key(params string[] ranges) => new() { Name = "k", Prefix = "p", KeyHash = "h", AllowedIpRanges = [.. ranges] };

    [Fact]
    public void Empty_list_allows_every_address_including_none()
    {
        Key().IsAddressAllowed(IPAddress.Parse("198.51.100.1")).ShouldBeTrue();
        Key().IsAddressAllowed(null).ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restricted_key_allows_only_listed_addresses_with_or_without_the_index(bool indexed)
    {
        var key = Key("203.0.113.0/24", "2001:db8::1");
        if (indexed)
        {
            key.IndexAllowLists();
        }

        key.IsAddressAllowed(IPAddress.Parse("203.0.113.200")).ShouldBeTrue();
        key.IsAddressAllowed(IPAddress.Parse("2001:db8::1")).ShouldBeTrue();
        key.IsAddressAllowed(IPAddress.Parse("198.51.100.1")).ShouldBeFalse();
        key.IsAddressAllowed(null).ShouldBeFalse();
    }

    [Fact]
    public void A_list_with_no_valid_entry_refuses_everyone()
    {
        var key = Key("not-an-address");
        key.IndexAllowLists();
        key.IsAddressAllowed(IPAddress.Parse("198.51.100.1")).ShouldBeFalse();
    }

    [Fact]
    public void Index_is_ignored_once_the_list_is_replaced()
    {
        var key = Key("203.0.113.0/24");
        key.IndexAllowLists();
        key.AllowedIpRanges = ["198.51.100.0/24"];
        key.IsAddressAllowed(IPAddress.Parse("198.51.100.1")).ShouldBeTrue();
        key.IsAddressAllowed(IPAddress.Parse("203.0.113.1")).ShouldBeFalse();
    }

    [Fact]
    public void Rotation_keeps_the_address_restriction()
    {
        var old = Key("203.0.113.0/24");
        old.CreatedAt = DateTimeOffset.UtcNow.AddDays(-1);
        var replacement = KeyRotation.Rotate(old, new GeneratedKey("ume-sk-x", "ume-sk-x", "hash"), KeyRotationMode.RevokeImmediately, DateTimeOffset.UtcNow, "tester");
        replacement.AllowedIpRanges.ShouldBe(["203.0.113.0/24"]);
        replacement.AllowedIpRanges.ShouldNotBeSameAs(old.AllowedIpRanges);
    }
}
