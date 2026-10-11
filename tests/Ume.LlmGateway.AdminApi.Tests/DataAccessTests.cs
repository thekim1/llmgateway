using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Persistence;

namespace Ume.LlmGateway.AdminApi.Tests;

public sealed class DataAccessTests(AdminFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null, 401)]
    [InlineData("viewer", 403)]
    [InlineData("department-admin", 403)]
    public async Task Only_gateway_admins_manage_data_api_keys(string? role, int status)
    {
        using var client = await fixture.ClientAsync(role, role is null ? null : "ANY");
        ((int)(await client.GetAsync("/api/data-access/keys", Ct)).StatusCode).ShouldBe(status);
        ((int)(await client.GetAsync("/api/data-access", Ct)).StatusCode).ShouldBe(status);
        ((int)(await client.PostAsJsonAsync("/api/data-access/keys", new { name = "Denied", permissions = new[] { "usage.aggregate" } }, Ct)).StatusCode).ShouldBe(status);
    }

    [Fact]
    public async Task A_key_is_shown_once_stored_as_a_hash_changed_revoked_deleted_and_audited()
    {
        using var client = await fixture.ClientAsync();
        var name = "Power BI " + Guid.NewGuid().ToString("N")[..8];
        using var create = await client.PostAsJsonAsync("/api/data-access/keys",
            new { name, description = "Monthly report", permissions = new[] { "usage.detail", "usage.aggregate" } }, Ct);
        var created = await AdminFixture.ReadAsync(create, HttpStatusCode.Created);
        var id = created["key"]!["id"]!.GetValue<Guid>();
        var secret = created["secret"]!.GetValue<string>();
        DataApiKeyHasher.LooksLikeKey(secret).ShouldBeTrue();
        created["key"]!["prefix"]!.GetValue<string>().ShouldBe(secret[..11]);
        // Stored in the canonical order, whatever order they were sent in.
        created["key"]!["permissions"]!.AsArray().Select(p => p!.GetValue<string>()).ShouldBe(["usage.aggregate", "usage.detail"]);
        created["key"]!["status"]!.GetValue<string>().ShouldBe("Active");

        using var list = await client.GetAsync("/api/data-access/keys", Ct);
        var listed = (await AdminFixture.ReadAsync(list)).AsArray().Single(k => k!["id"]!.GetValue<Guid>() == id)!;
        listed["secret"].ShouldBeNull();
        listed["keyHash"].ShouldBeNull();

        var expires = DateTimeOffset.UtcNow.AddDays(30);
        using var update = await client.PutAsJsonAsync($"/api/data-access/keys/{id}",
            new { name, permissions = new[] { "security.read" }, expiresAt = expires }, Ct);
        var updated = await AdminFixture.ReadAsync(update);
        updated["permissions"]!.AsArray().Select(p => p!.GetValue<string>()).ShouldBe(["security.read"]);
        updated["expiresAt"].ShouldNotBeNull();
        updated["description"].ShouldBeNull();

        // An active key cannot be deleted: revoke it first.
        (await client.DeleteAsync($"/api/data-access/keys/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var revoke = await client.PostAsync($"/api/data-access/keys/{id}/revoke", null, Ct);
        (await AdminFixture.ReadAsync(revoke))["status"]!.GetValue<string>().ShouldBe("Revoked");
        (await client.PostAsync($"/api/data-access/keys/{id}/revoke", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await client.PutAsJsonAsync($"/api/data-access/keys/{id}", new { name, permissions = new[] { "usage.detail" } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var stored = await db.DataApiKeys.SingleAsync(k => k.Id == id, Ct);
            stored.KeyHash.ShouldBe(DataApiKeyHasher.Hash(secret));
            stored.KeyHash.ShouldNotContain(secret);
        }

        (await client.DeleteAsync($"/api/data-access/keys/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync($"/api/data-access/keys/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var audits = await db.AuditLog.Where(a => a.EntityType == "DataApiKey" && a.EntityId == id.ToString()).OrderBy(a => a.Id).ToListAsync(Ct);
            audits.Select(a => a.Action).ShouldBe(["create", "update", "revoke", "delete"]);
            var details = string.Join("", audits.Select(a => a.Details));
            details.ShouldNotContain(secret);
            details.ShouldNotContain(DataApiKeyHasher.Hash(secret));
            details.ShouldContain("security.read");
        }
    }

    [Fact]
    public async Task Requests_are_validated()
    {
        using var client = await fixture.ClientAsync();
        var name = "Validated " + Guid.NewGuid().ToString("N")[..8];
        (await client.PostAsJsonAsync("/api/data-access/keys", new { name, permissions = Array.Empty<string>() }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/data-access/keys", new { name, permissions = new[] { "admin.everything" } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/data-access/keys", new { name, permissions = new[] { "catalog.read" }, expiresAt = DateTimeOffset.UtcNow.AddDays(-1) }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var first = await client.PostAsJsonAsync("/api/data-access/keys", new { name, permissions = new[] { "catalog.read" } }, Ct);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var duplicate = await client.PostAsJsonAsync("/api/data-access/keys", new { name, permissions = new[] { "catalog.read" } }, Ct);
        (await AdminFixture.ReadAsync(duplicate, HttpStatusCode.BadRequest))["errors"]!["name"].ShouldNotBeNull();

        using var settings = await client.GetAsync("/api/data-access", Ct);
        (await AdminFixture.ReadAsync(settings))["permissions"]!.AsArray().Count.ShouldBe(4);
    }
}
