using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi.Tests;

public sealed class NetworkSettingsTests(AdminFixture fixture)
{
    [Theory]
    [InlineData(null, 401)]
    [InlineData("viewer", 403)]
    [InlineData("department-admin", 403)]
    public async Task Only_gateway_admins_see_or_change_network_settings(string? role, int status)
    {
        using var client = await fixture.ClientAsync(role);
        using var read = await client.GetAsync("/api/settings/network", TestContext.Current.CancellationToken);
        ((int)read.StatusCode).ShouldBe(status);
        using var write = await client.PutAsJsonAsync("/api/settings/network", new { trustedProxies = new[] { "192.0.2.1" } }, TestContext.Current.CancellationToken);
        ((int)write.StatusCode).ShouldBe(status);
    }

    [Fact]
    public async Task Trusted_proxies_round_trip_canonicalised_audited_and_published()
    {
        using var client = await fixture.ClientAsync();
        var changes = new List<InvalidationKind>();
        using var subscription = fixture.Services.GetRequiredService<IInvalidationBus>().Subscribe(changes.Add);

        using var save = await client.PutAsJsonAsync("/api/settings/network",
            new { trustedProxies = new[] { " 10.0.0.0/24 ", "2001:DB8::1", "10.0.0.0/24" } }, TestContext.Current.CancellationToken);
        var saved = await AdminFixture.ReadAsync(save);
        saved["trustedProxies"]!.AsArray().Select(p => p!.GetValue<string>()).ShouldBe(["10.0.0.0/24", "2001:db8::1"]);
        saved["updatedAt"].ShouldNotBeNull();
        saved["observed"].ShouldNotBeNull();
        changes.ShouldContain(InvalidationKind.Config);

        using var read = await client.GetAsync("/api/settings/network", TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(read))["trustedProxies"]!.AsArray().Count.ShouldBe(2);

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var audit = await db.AuditLog.Where(a => a.EntityType == AuditEntities.Settings).OrderByDescending(a => a.Id).FirstAsync(TestContext.Current.CancellationToken);
            audit.Details.ShouldNotBeNull();
            audit.Details.ShouldContain("2001:db8::1");
        }

        using var clear = await client.PutAsJsonAsync("/api/settings/network", new { trustedProxies = Array.Empty<string>() }, TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(clear))["trustedProxies"]!.AsArray().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("10.0.0.5/24")]
    [InlineData("proxy.example.se")]
    public async Task Unsafe_or_invalid_proxies_are_refused(string entry)
    {
        using var client = await fixture.ClientAsync();
        using var response = await client.PutAsJsonAsync("/api/settings/network", new { trustedProxies = new[] { entry } }, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Key_address_allowlist_round_trips_and_is_validated()
    {
        using var client = await fixture.ClientAsync();
        var org = await fixture.OrganisationAsync(client);
        using var create = await client.PostAsJsonAsync("/api/keys", new
        {
            teamId = org.Team, name = "Office only", allowedModels = Array.Empty<string>(), allowedResidencies = Array.Empty<string>(), piiPolicy = "Off",
            allowedIpRanges = new[] { "203.0.113.0/24", " 2001:DB8::10 ", "203.0.113.0/24" },
        }, TestContext.Current.CancellationToken);
        var key = (await AdminFixture.ReadAsync(create, HttpStatusCode.Created))["key"]!;
        key["allowedIpRanges"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldBe(["203.0.113.0/24", "2001:db8::10"]);
        var keyId = key["id"]!.GetValue<Guid>();

        // Left out: unchanged (older clients do not know the field).
        using var keep = await client.PutAsJsonAsync($"/api/keys/{keyId}", new
        {
            teamId = org.Team, name = "Office only", allowedModels = Array.Empty<string>(), allowedResidencies = Array.Empty<string>(), piiPolicy = "Off",
        }, TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(keep))["allowedIpRanges"]!.AsArray().Count.ShouldBe(2);

        using var clear = await client.PutAsJsonAsync($"/api/keys/{keyId}", new
        {
            teamId = org.Team, name = "Office only", allowedModels = Array.Empty<string>(), allowedResidencies = Array.Empty<string>(), piiPolicy = "Off",
            allowedIpRanges = Array.Empty<string>(),
        }, TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(clear))["allowedIpRanges"]!.AsArray().ShouldBeEmpty();

        using var invalid = await client.PutAsJsonAsync($"/api/keys/{keyId}", new
        {
            teamId = org.Team, name = "Office only", allowedModels = Array.Empty<string>(), allowedResidencies = Array.Empty<string>(), piiPolicy = "Off",
            allowedIpRanges = new[] { "203.0.113.7/24" },
        }, TestContext.Current.CancellationToken);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await invalid.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("203.0.113.0/24");
    }
}
