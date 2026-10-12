using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Gateway.Pipeline;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.TestKit;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>Key address allow-lists and trusted proxies. TestServer has no connection address, so a test header sets it.</summary>
public sealed class ClientAddressTests(GatewayFixture fixture)
{
    private const string PeerHeader = "x-test-peer";
    private const string Proxy = "192.0.2.250";

    [Fact]
    public async Task Key_is_refused_outside_its_allowed_addresses()
    {
        var key = await fixture.CreateKeyAsync(k => k.AllowedIpRanges = ["203.0.113.0/24", "2001:db8::1"]);
        await using var gateway = DeriveWithPeerHeader();
        using var client = gateway.CreateHttpsClient(allowAutoRedirect: true);

        (await SendAsync(client, key, peer: "203.0.113.9")).ShouldBe(HttpStatusCode.OK);
        (await SendAsync(client, key, peer: "::ffff:203.0.113.10")).ShouldBe(HttpStatusCode.OK); // dual-stack socket
        (await SendAsync(client, key, peer: "2001:db8::1")).ShouldBe(HttpStatusCode.OK);
        (await SendAsync(client, key, peer: "198.51.100.1")).ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(client, key, peer: null)).ShouldBe(HttpStatusCode.Forbidden);

        using var refused = await SendRawAsync(client, key, "198.51.100.1", null);
        var error = (await refused.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!["error"]!;
        error["code"]!.GetValue<string>().ShouldBe("address_not_allowed");
    }

    [Fact]
    public async Task Unrestricted_key_works_from_anywhere()
    {
        var key = await fixture.CreateKeyAsync();
        await using var gateway = DeriveWithPeerHeader();
        using var client = gateway.CreateHttpsClient(allowAutoRedirect: true);

        (await SendAsync(client, key, peer: "198.51.100.1")).ShouldBe(HttpStatusCode.OK);
        (await SendAsync(client, key, peer: null)).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Forwarded_address_is_used_only_through_a_trusted_proxy()
    {
        var key = await fixture.CreateKeyAsync(k => k.AllowedIpRanges = ["203.0.113.9"]);
        await using var gateway = DeriveWithPeerHeader();
        await SetTrustedProxiesAsync(gateway, Proxy);
        try
        {
            using var client = gateway.CreateHttpsClient(allowAutoRedirect: true);

            // Through the proxy: the client is the address the proxy appended.
            (await SendAsync(client, key, Proxy, "203.0.113.9")).ShouldBe(HttpStatusCode.OK);
            // A client prepending an allowed address is not believed: the proxy appended the real one last.
            (await SendAsync(client, key, Proxy, "203.0.113.9, 198.51.100.1")).ShouldBe(HttpStatusCode.Forbidden);
            // Not from the proxy: the header is ignored and the connection address counts.
            (await SendAsync(client, key, "198.51.100.1", "203.0.113.9")).ShouldBe(HttpStatusCode.Forbidden);
            // From the proxy without a header: the proxy itself is the client.
            (await SendAsync(client, key, Proxy, null)).ShouldBe(HttpStatusCode.Forbidden);
        }
        finally
        {
            await SetTrustedProxiesAsync(gateway);
        }
    }

    [Fact]
    public async Task Aspnet_forwarded_headers_switch_cannot_bypass_the_trusted_proxy_list()
    {
        var key = await fixture.CreateKeyAsync(k => k.AllowedIpRanges = ["203.0.113.9"]);
        // ASPNETCORE_FORWARDEDHEADERS_ENABLED=true would make ASP.NET believe X-Forwarded-For from anyone.
        // First, so the connection address is set before ASP.NET's forwarded-headers middleware runs, as with Kestrel.
        await using var gateway = fixture.Derive(services => services.Insert(0, ServiceDescriptor.Transient<IStartupFilter, PeerHeaderStartupFilter>()),
            host => host.UseSetting("FORWARDEDHEADERS_ENABLED", "true"));
        using var client = gateway.CreateHttpsClient(allowAutoRedirect: true);

        (await SendAsync(client, key, "198.51.100.1", "203.0.113.9")).ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Refusal_is_recorded_with_the_client_address()
    {
        var key = await fixture.CreateKeyAsync(k => k.AllowedIpRanges = ["203.0.113.0/24"]);
        await using var gateway = DeriveWithPeerHeader();
        using var client = gateway.CreateHttpsClient(allowAutoRedirect: true);
        (await SendAsync(client, key, peer: "198.51.100.77")).ShouldBe(HttpStatusCode.Forbidden);

        await gateway.Services.GetRequiredService<AuthFailureRecorder>().FlushAsync(TestContext.Current.CancellationToken);
        using var scope = gateway.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().AuthFailures
            .SingleAsync(f => f.VirtualKeyId == key.Id, TestContext.Current.CancellationToken);
        row.Reason.ShouldBe(AuthFailureReason.AddressNotAllowed);
        row.SourceAddress.ShouldNotBeNull();
        row.SourceAddress.ShouldStartWith("198.51.100.");
    }

    [Theory]
    [InlineData("192.0.2.250", "203.0.113.9", "203.0.113.9")]
    [InlineData("192.0.2.250", "198.51.100.1, 203.0.113.9", "203.0.113.9")]
    [InlineData("192.0.2.250", "203.0.113.9, 192.0.2.251", "203.0.113.9")] // two trusted hops
    [InlineData("192.0.2.250", "192.0.2.251", "192.0.2.251")]                // only trusted hops: the leftmost
    [InlineData("192.0.2.250", "unknown, 203.0.113.9", "203.0.113.9")]
    [InlineData("192.0.2.250", "203.0.113.9, unknown", "192.0.2.250")]       // unreadable nearest hop: stop at the proxy
    [InlineData("192.0.2.250", "[2001:db8::9]:4711", "2001:db8::9")]
    [InlineData("192.0.2.250", "203.0.113.9:4711", "203.0.113.9")]
    [InlineData("::ffff:192.0.2.250", "203.0.113.9", "203.0.113.9")]
    [InlineData("198.51.100.1", "203.0.113.9", "198.51.100.1")]             // not a trusted proxy
    public void Resolve_walks_forwarded_for_from_the_nearest_hop(string remote, string forwardedFor, string expected)
    {
        IPNetwork[] trusted = [IPNetwork.Parse("192.0.2.250/31")];
        ClientAddressMiddleware.Resolve(IPAddress.Parse(remote), forwardedFor, trusted)!.ToString().ShouldBe(expected);
    }

    [Fact]
    public void Resolve_reads_several_header_lines_as_one_list()
    {
        IPNetwork[] trusted = [IPNetwork.Parse("192.0.2.250/32")];
        var lines = new StringValues(["203.0.113.9", "198.51.100.1"]);
        ClientAddressMiddleware.Resolve(IPAddress.Parse(Proxy), lines, trusted)!.ToString().ShouldBe("198.51.100.1");
    }

    [Fact]
    public void Resolve_leaves_the_address_alone_without_trusted_proxies()
    {
        var remote = IPAddress.Parse(Proxy);
        ClientAddressMiddleware.Resolve(remote, "203.0.113.9", []).ShouldBeSameAs(remote);
    }

    private WebApplicationFactory<Program> DeriveWithPeerHeader() =>
        fixture.Derive(services => services.AddTransient<IStartupFilter, PeerHeaderStartupFilter>());

    private static async Task SetTrustedProxiesAsync(WebApplicationFactory<Program> gateway, params string[] proxies)
    {
        using (var scope = gateway.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var settings = await db.Settings.SingleOrDefaultAsync(TestContext.Current.CancellationToken);
            if (settings is null)
            {
                settings = new GatewaySettings();
                db.Settings.Add(settings);
            }

            settings.TrustedProxies = [.. proxies];
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        gateway.Services.GetRequiredService<GatewayCatalog>().Invalidate();
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, TestKey key, string? peer, string? forwardedFor = null)
    {
        using var response = await SendRawAsync(client, key, peer, forwardedFor);
        return response.StatusCode;
    }

    private static Task<HttpResponseMessage> SendRawAsync(HttpClient client, TestKey key, string? peer, string? forwardedFor)
    {
        var body = new JsonObject
        {
            ["model"] = "eu/ok", ["max_tokens"] = 20,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Test prompt" }),
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        if (peer is not null)
        {
            request.Headers.Add(PeerHeader, peer);
        }

        if (forwardedFor is not null)
        {
            request.Headers.Add(ClientAddressMiddleware.ForwardedForHeader, forwardedFor);
        }

        return SendAndDisposeRequestAsync(client, request);
    }

    private static async Task<HttpResponseMessage> SendAndDisposeRequestAsync(HttpClient client, HttpRequestMessage request)
    {
        using (request)
        {
            return await client.SendAsync(request, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Runs before the gateway's own middleware, like Kestrel setting the connection address.</summary>
    private sealed class PeerHeaderStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((http, nextMiddleware) =>
            {
                http.Connection.RemoteIpAddress = http.Request.Headers.TryGetValue(PeerHeader, out var peer) ? IPAddress.Parse(peer.ToString()) : null;
                return nextMiddleware(http);
            });
            next(app);
        };
    }
}
