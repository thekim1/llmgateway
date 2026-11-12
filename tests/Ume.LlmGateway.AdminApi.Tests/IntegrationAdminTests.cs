using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.Infrastructure.Persistence;

namespace Ume.LlmGateway.AdminApi.Tests;

/// <summary>Integrations and their keys on the admin API (docs/integrations.md).</summary>
public sealed class IntegrationAdminTests(AdminFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("viewer", 403)]
    [InlineData("department-admin", 403)]
    public async Task Only_gateway_admins_change_integrations(string role, int status)
    {
        using var admin = await fixture.ClientAsync();
        var org = await fixture.OrganisationAsync(admin);
        using var client = await fixture.ClientAsync(role, org.Code);
        ((int)(await client.PostAsJsonAsync("/api/integrations", new { name = "Denied", ownerTeamId = org.Team }, Ct)).StatusCode).ShouldBe(status);
        var id = await CreateIntegrationAsync(admin, org.Team);
        ((int)(await client.PutAsJsonAsync($"/api/integrations/{id}", new { name = "Denied", ownerTeamId = org.Team }, Ct)).StatusCode).ShouldBe(status);
        ((int)(await client.DeleteAsync($"/api/integrations/{id}", Ct)).StatusCode).ShouldBe(status);
    }

    [Fact]
    public async Task A_department_admin_lists_integrations_and_assigns_a_key_that_has_no_secret()
    {
        using var admin = await fixture.ClientAsync();
        var platform = await fixture.OrganisationAsync(admin);
        var integration = await CreateIntegrationAsync(admin, platform.Team);
        var department = await fixture.OrganisationAsync(admin);
        using var client = await fixture.ClientAsync("department-admin", department.Code);

        using var list = await client.GetAsync("/api/integrations", Ct);
        (await AdminFixture.ReadAsync(list)).AsArray().ShouldContain(i => i!["id"]!.GetValue<Guid>() == integration);

        using var create = await client.PostAsJsonAsync("/api/keys", Key(department.Team, integration), Ct);
        var created = await AdminFixture.ReadAsync(create, HttpStatusCode.Created);
        created["secret"].ShouldBeNull();
        var key = created["key"]!;
        key["integrationId"]!.GetValue<Guid>().ShouldBe(integration);
        key["integrationRole"]!.GetValue<string>().ShouldBe("Assigned");
        key["canReveal"]!.GetValue<bool>().ShouldBeFalse();
        var id = key["id"]!.GetValue<Guid>();

        using (var scope = fixture.Services.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().VirtualKeys.AsNoTracking().SingleAsync(k => k.Id == id, Ct);
            stored.EncryptedSecret.ShouldBeNull(); // nothing to reveal: the key is only used by reference
        }

        // The key's own settings stay with its managers; it cannot be rotated or revealed, only revoked.
        using (var update = await client.PutAsJsonAsync($"/api/keys/{id}", Key(department.Team, integration, requestsPerMinute: 30), Ct))
        {
            (await AdminFixture.ReadAsync(update))["requestsPerMinute"]!.GetValue<int>().ShouldBe(30);
        }

        (await client.PostAsJsonAsync($"/api/keys/{id}/rotate", new { mode = "RevokeImmediately" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await admin.PostAsJsonAsync($"/api/keys/{id}/reveal", new { purpose = "Reveal" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await client.PostAsync($"/api/keys/{id}/revoke", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var byIntegration = await admin.GetAsync($"/api/keys?integrationId={integration}", Ct);
        (await AdminFixture.ReadAsync(byIntegration)).AsArray().Select(k => k!["id"]!.GetValue<Guid>()).ShouldBe([id]);
    }

    [Fact]
    public async Task Credential_keys_are_made_and_changed_by_gateway_admins_in_the_owner_team()
    {
        using var admin = await fixture.ClientAsync();
        var platform = await fixture.OrganisationAsync(admin);
        var integration = await CreateIntegrationAsync(admin, platform.Team);
        var other = await fixture.OrganisationAsync(admin);

        // Not in the owner team.
        using (var wrongTeam = await admin.PostAsJsonAsync("/api/keys", Key(other.Team, integration, role: "Credential"), Ct))
        {
            wrongTeam.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        // Not by the platform team's department admin.
        using var departmentAdmin = await fixture.ClientAsync("department-admin", platform.Code);
        (await departmentAdmin.PostAsJsonAsync("/api/keys", Key(platform.Team, integration, role: "Credential"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var create = await admin.PostAsJsonAsync("/api/keys", Key(platform.Team, integration, role: "Credential", scopes: ["usage.read"]), Ct);
        var created = await AdminFixture.ReadAsync(create, HttpStatusCode.Created);
        created["secret"]!.GetValue<string>().ShouldStartWith("ume-sk-");
        var key = created["key"]!;
        key["integrationRole"]!.GetValue<string>().ShouldBe("Credential");
        key["integrationScopes"]!.AsArray().Select(s => s!.GetValue<string>()).ShouldBe(["usage.read"]);
        var id = key["id"]!.GetValue<Guid>();

        // The department admin may rotate and revoke it, not change it.
        (await departmentAdmin.PutAsJsonAsync($"/api/keys/{id}", Key(platform.Team, integration, requestsPerMinute: 1), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var rotate = await departmentAdmin.PostAsJsonAsync($"/api/keys/{id}/rotate", new { mode = "RevokeImmediately" }, Ct);
        var rotated = await AdminFixture.ReadAsync(rotate);
        rotated["key"]!["integrationId"]!.GetValue<Guid>().ShouldBe(integration);
        rotated["key"]!["integrationRole"]!.GetValue<string>().ShouldBe("Credential");
        rotated["key"]!["integrationScopes"]!.AsArray().Select(s => s!.GetValue<string>()).ShouldBe(["usage.read"]);

        // An unknown scope, scopes on an assigned key, and moving a key to another integration are refused.
        (await admin.PostAsJsonAsync("/api/keys", Key(platform.Team, integration, role: "Credential", scopes: ["everything"]), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync("/api/keys", Key(other.Team, integration, scopes: ["usage.read"]), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var second = await CreateIntegrationAsync(admin, platform.Team);
        var newId = rotated["key"]!["id"]!.GetValue<Guid>();
        (await admin.PutAsJsonAsync($"/api/keys/{newId}", Key(platform.Team, second, role: "Credential"), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_integration_with_live_keys_cannot_be_deleted_or_change_owner_and_a_deactivated_one_takes_no_keys()
    {
        using var admin = await fixture.ClientAsync();
        var platform = await fixture.OrganisationAsync(admin);
        var integration = await CreateIntegrationAsync(admin, platform.Team);
        using var create = await admin.PostAsJsonAsync("/api/keys", Key(platform.Team, integration, role: "Credential"), Ct);
        var keyId = (await AdminFixture.ReadAsync(create, HttpStatusCode.Created))["key"]!["id"]!.GetValue<Guid>();

        using (var get = await admin.GetAsync($"/api/integrations/{integration}", Ct))
        {
            (await AdminFixture.ReadAsync(get))["credentialKeys"]!.GetValue<int>().ShouldBe(1);
        }

        var other = await fixture.OrganisationAsync(admin);
        (await admin.PutAsJsonAsync($"/api/integrations/{integration}", new { name = "Moved " + Guid.NewGuid(), ownerTeamId = other.Team }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await admin.DeleteAsync($"/api/integrations/{integration}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await admin.DeleteAsync($"/api/teams/{platform.Team}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var name = "Paused " + Guid.NewGuid();
        using (var deactivate = await admin.PutAsJsonAsync($"/api/integrations/{integration}", new { name, ownerTeamId = platform.Team, isActive = false }, Ct))
        {
            (await AdminFixture.ReadAsync(deactivate))["isActive"]!.GetValue<bool>().ShouldBeFalse();
        }

        (await admin.PostAsJsonAsync("/api/keys", Key(platform.Team, integration), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await admin.PostAsync($"/api/keys/{keyId}/revoke", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.DeleteAsync($"/api/integrations/{integration}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var audit = await admin.GetAsync("/api/audit?entityType=Integration&pageSize=200", Ct);
        (await AdminFixture.ReadAsync(audit))["items"]!.AsArray().Where(e => e!["entityId"]!.GetValue<string>() == integration.ToString())
            .Select(e => e!["action"]!.GetValue<string>()).ShouldBe(["delete", "update", "create"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_models_output_limit_is_kept_checked_against_the_context_window_and_exported()
    {
        using var client = await fixture.ClientAsync();
        var suffix = Guid.NewGuid().ToString("N");
        using var providerResponse = await client.PostAsJsonAsync("/api/providers", new
        {
            name = "limits-" + suffix, baseUrl = "https://limits.invalid/v1", type = "OpenAICompatible", authMode = "None", residency = "Eu",
            capabilities = new[] { "ChatCompletions" }, timeoutSeconds = 60, isEnabled = true,
        }, Ct);
        var provider = (await AdminFixture.ReadAsync(providerResponse, HttpStatusCode.Created))["id"]!.GetValue<Guid>();
        object Model(int? output) => new
        {
            providerId = provider, name = "limits-" + suffix + "/chat", upstreamModel = "chat", kind = "Chat", parameterProfile = "Standard",
            contextWindow = 8000, maxOutputTokens = output, isEnabled = true,
        };

        (await client.PostAsJsonAsync("/api/models", Model(9000), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var create = await client.PostAsJsonAsync("/api/models", Model(4000), Ct);
        (await AdminFixture.ReadAsync(create, HttpStatusCode.Created))["maxOutputTokens"]!.GetValue<int>().ShouldBe(4000);

        using var export = await client.GetAsync("/api/ops/config/export", Ct);
        var exported = (await AdminFixture.ReadAsync(export))["models"]!.AsArray()
            .Single(m => m!["configuration"]!["name"]!.GetValue<string>() == "limits-" + suffix + "/chat")!;
        exported["configuration"]!["maxOutputTokens"]!.GetValue<int>().ShouldBe(4000);
    }

    private static async Task<Guid> CreateIntegrationAsync(HttpClient admin, Guid ownerTeam)
    {
        using var response = await admin.PostAsJsonAsync("/api/integrations",
            new { name = "Platform " + Guid.NewGuid().ToString("N")[..8], ownerTeamId = ownerTeam, description = "RAG" }, Ct);
        var created = await AdminFixture.ReadAsync(response, HttpStatusCode.Created);
        created["ownerTeamId"]!.GetValue<Guid>().ShouldBe(ownerTeam);
        created["isActive"]!.GetValue<bool>().ShouldBeTrue();
        return created["id"]!.GetValue<Guid>();
    }

    private static object Key(Guid team, Guid integration, string? role = null, string[]? scopes = null, int requestsPerMinute = 60) => new
    {
        teamId = team, name = "Chatbot", allowedModels = Array.Empty<string>(), allowedResidencies = Array.Empty<string>(), piiPolicy = "Off",
        requestsPerMinute, integrationId = integration, integrationRole = role, integrationScopes = scopes,
    };
}
