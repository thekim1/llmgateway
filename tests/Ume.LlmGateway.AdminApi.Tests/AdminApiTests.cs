using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi.Tests;

public sealed class AdminApiTests(AdminFixture fixture)
{
    [Theory]
    [InlineData(null, 401)]
    [InlineData("viewer", 403)]
    [InlineData("department-admin", 403)]
    public async Task Privileged_endpoints_require_admin_role(string? role, int status)
    {
        using var client = await fixture.ClientAsync(role);
        using var response = await client.GetAsync("/api/providers", TestContext.Current.CancellationToken);
        ((int)response.StatusCode).ShouldBe(status);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Bff_user_sets_secure_csrf_cookies_and_does_not_expose_tokens()
    {
        using var client = await fixture.ClientAsync();
        using var response = await client.GetAsync("/bff/user", TestContext.Current.CancellationToken);
        var user = await AdminFixture.ReadAsync(response);
        user["isAuthenticated"]!.GetValue<bool>().ShouldBeTrue();
        user["roles"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldContain("gateway-admin");
        var cookies = response.Headers.GetValues("Set-Cookie");
        cookies.ShouldContain(c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal) && c.Contains("secure", StringComparison.OrdinalIgnoreCase));
        user.ToJsonString().ShouldNotContain("access_token");
        user.ToJsonString().ShouldNotContain("id_token");
    }

    [Fact]
    public async Task Logout_returns_provider_redirect_with_client_and_protected_local_callback_state()
    {
        using var client = await fixture.ClientAsync();
        using var logout = await client.PostAsync("/bff/logout", null, TestContext.Current.CancellationToken);
        var result = await AdminFixture.ReadAsync(logout);
        var redirect = new Uri(result["redirectUrl"]!.GetValue<string>());
        redirect.Host.ShouldBe("identity.invalid");
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(redirect.Query);
        query["client_id"].ToString().ShouldBe("ume-admin");
        query.ContainsKey("id_token_hint").ShouldBeFalse();
        query["state"].ToString().ShouldNotBeNullOrWhiteSpace();
        using var callback = await client.GetAsync("/signout-callback-oidc?state=" + Uri.EscapeDataString(query["state"].ToString()), TestContext.Current.CancellationToken);
        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        callback.Headers.Location!.ToString().ShouldBe("/");
    }

    [Theory]
    [InlineData("X-XSRF-TOKEN")]
    [InlineData("X-Requested-With")]
    public async Task Mutation_requires_antiforgery_and_requested_with(string missing)
    {
        using var client = await fixture.ClientAsync();
        client.DefaultRequestHeaders.Remove(missing);
        using var response = await client.PostAsJsonAsync("/api/departments", new { name = "Blocked", costCenterCode = Guid.NewGuid().ToString() }, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Session_extension_renews_secure_cookie_without_returning_tokens()
    {
        using var client = await fixture.ClientAsync("viewer");
        var before = DateTimeOffset.UtcNow;
        using var response = await client.PostAsync("/bff/session/extend", null, TestContext.Current.CancellationToken);
        var body = await AdminFixture.ReadAsync(response);
        var expiry = body["sessionExpiresAt"]!.GetValue<DateTimeOffset>();
        expiry.ShouldBeGreaterThanOrEqualTo(before.AddHours(8));
        expiry.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow.AddHours(8));
        response.Headers.GetValues("Set-Cookie").ShouldContain(c =>
            c.StartsWith("__Host-ume-admin=", StringComparison.Ordinal) &&
            c.Contains("secure", StringComparison.OrdinalIgnoreCase) &&
            c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        body.ToJsonString().ShouldNotContain("token");
    }

    [Theory]
    [InlineData("X-XSRF-TOKEN")]
    [InlineData("X-Requested-With")]
    public async Task Session_extension_requires_csrf(string missing)
    {
        using var client = await fixture.ClientAsync();
        client.DefaultRequestHeaders.Remove(missing);
        using var response = await client.PostAsync("/bff/session/extend", null, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Headers.TryGetValues("Set-Cookie", out var cookies).ShouldBeFalse();
    }

    [Fact]
    public async Task Session_extension_requires_authentication()
    {
        using var client = await fixture.ClientAsync(null);
        using var response = await client.PostAsync("/bff/session/extend", null, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("https://outside.example")]
    [InlineData("//outside.example")]
    [InlineData("/\\outside.example")]
    public async Task Login_rejects_nonlocal_return_url(string returnUrl)
    {
        using var client = await fixture.ClientAsync(null);
        using var response = await client.GetAsync("/bff/login?returnUrl=" + Uri.EscapeDataString(returnUrl), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Organisation_crud_validates_conflicts_and_audits()
    {
        using var client = await fixture.ClientAsync();
        var org = await fixture.OrganisationAsync(client);
        using var conflict = await client.DeleteAsync($"/api/departments/{org.Department}", TestContext.Current.CancellationToken);
        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var update = await client.PutAsJsonAsync($"/api/teams/{org.Team}", new { name = "Changed team", isActive = false }, TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(update))["isActive"]!.GetValue<bool>().ShouldBeFalse();
        using var bad = await client.PostAsJsonAsync("/api/departments", new { name = "", costCenterCode = "" }, TestContext.Current.CancellationToken);
        var problem = await AdminFixture.ReadAsync(bad, HttpStatusCode.BadRequest);
        problem["errors"]!["name"].ShouldNotBeNull();
        using var duplicate = await client.PostAsJsonAsync("/api/departments", new { name = "Duplicate", costCenterCode = org.Code }, TestContext.Current.CancellationToken);
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var audit = await client.GetAsync("/api/audit?entityType=Team", TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(audit))["items"]!.AsArray().ShouldContain(a => a!["entityId"]!.GetValue<string>() == org.Team.ToString());
        using var deleteTeam = await client.DeleteAsync($"/api/teams/{org.Team}", TestContext.Current.CancellationToken);
        deleteTeam.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var deleteDepartment = await client.DeleteAsync($"/api/departments/{org.Department}", TestContext.Current.CancellationToken);
        deleteDepartment.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Department_admin_cannot_read_or_mutate_other_departments()
    {
        using var admin = await fixture.ClientAsync();
        var own = await fixture.OrganisationAsync(admin);
        var other = await fixture.OrganisationAsync(admin);
        using var client = await fixture.ClientAsync("department-admin", own.Code);
        using var departments = await client.GetAsync("/api/departments", TestContext.Current.CancellationToken);
        var list = (await AdminFixture.ReadAsync(departments)).AsArray();
        list.Count.ShouldBe(1);
        list[0]!["id"]!.GetValue<Guid>().ShouldBe(own.Department);
        using var denied = await client.PutAsJsonAsync($"/api/teams/{other.Team}", new { name = "Forbidden", isActive = true }, TestContext.Current.CancellationToken);
        denied.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var create = await client.PostAsJsonAsync("/api/teams", new { departmentId = other.Department, name = "Forbidden" }, TestContext.Current.CancellationToken);
        create.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var budget = await client.PostAsJsonAsync("/api/budgets", new { scope = "Team", scopeId = other.Team, limitSek = 100, period = "Monthly", alertThresholds = new[] { 80 }, isActive = true }, TestContext.Current.CancellationToken);
        budget.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("RevokeImmediately", "Revoked")]
    [InlineData("Grace24Hours", "InGracePeriod")]
    public async Task Keys_show_secret_once_rotate_and_revoke(string mode, string oldStatus)
    {
        using var client = await fixture.ClientAsync();
        var org = await fixture.OrganisationAsync(client);
        var changes = new List<InvalidationKind>();
        using var subscription = fixture.Services.GetRequiredService<IInvalidationBus>().Subscribe(changes.Add);
        using var create = await client.PostAsJsonAsync("/api/keys", KeyBody(org.Team), TestContext.Current.CancellationToken);
        var created = await AdminFixture.ReadAsync(create, HttpStatusCode.Created);
        var keyId = created["key"]!["id"]!.GetValue<Guid>();
        var secret = created["secret"]!.GetValue<string>();
        secret.ShouldStartWith("ume-sk-");
        using var get = await client.GetAsync($"/api/keys/{keyId}", TestContext.Current.CancellationToken);
        var retrieved = await AdminFixture.ReadAsync(get);
        retrieved["secret"].ShouldBeNull();
        retrieved["keyHash"].ShouldBeNull();
        using var rotate = await client.PostAsJsonAsync($"/api/keys/{keyId}/rotate", new { mode }, TestContext.Current.CancellationToken);
        var rotated = await AdminFixture.ReadAsync(rotate);
        rotated["secret"]!.GetValue<string>().ShouldNotBe(secret);
        rotated["previousKey"]!["status"]!.GetValue<string>().ShouldBe(oldStatus);
        using var revoke = await client.PostAsync($"/api/keys/{keyId}/revoke", null, TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(revoke))["status"]!.GetValue<string>().ShouldBe("Revoked");
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var stored = await db.VirtualKeys.SingleAsync(k => k.Id == keyId, TestContext.Current.CancellationToken);
        stored.KeyHash.ShouldNotContain(secret);
        var audits = await db.AuditLog.Where(a => a.EntityId == keyId.ToString()).Select(a => a.Details).ToListAsync(TestContext.Current.CancellationToken);
        string.Join("", audits).ShouldNotContain(secret);
        string.Join("", audits).ShouldNotContain(rotated["secret"]!.GetValue<string>());
        changes.ShouldContain(InvalidationKind.Keys);
    }

    [Fact]
    public async Task Provider_credential_is_write_only_and_export_import_is_secret_free()
    {
        using var client = await fixture.ClientAsync();
        var secret = Guid.NewGuid().ToString("N");
        var name = "provider-" + Guid.NewGuid().ToString("N");
        var body = new { name, baseUrl = "https://provider.invalid/v1", type = "OpenAICompatible", authMode = "Bearer",
            credential = secret, residency = "Eu", capabilities = new[] { "ChatCompletions", "Streaming" }, timeoutSeconds = 10, isEnabled = true };
        using var create = await client.PostAsJsonAsync("/api/providers", body, TestContext.Current.CancellationToken);
        var provider = await AdminFixture.ReadAsync(create, HttpStatusCode.Created);
        var id = provider["id"]!.GetValue<Guid>();
        provider["hasCredential"]!.GetValue<bool>().ShouldBeTrue();
        provider.ToJsonString().ShouldNotContain(secret);
        provider["credential"].ShouldBeNull();
        using var export = await client.GetAsync("/api/ops/config/export", TestContext.Current.CancellationToken);
        var document = await AdminFixture.ReadAsync(export);
        document.ToJsonString().ShouldNotContain(secret);
        document.ToJsonString().ShouldNotContain("encryptedCredential");
        using var import = await client.PostAsJsonAsync("/api/ops/config/import", document, TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(import))["updated"]!.GetValue<int>().ShouldBeGreaterThan(0);
        using var drain = await client.PostAsJsonAsync($"/api/providers/{id}/drain", new { drained = true }, TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(drain))["isDrained"]!.GetValue<bool>().ShouldBeTrue();
        using var circuit = await client.PostAsJsonAsync($"/api/ops/providers/{id}/circuit", new { state = "Open" }, TestContext.Current.CancellationToken);
        circuit.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var open = await fixture.Services.GetRequiredService<ICircuitBreakerStore>().GetOpenAsync([id], TestContext.Current.CancellationToken);
        open.ShouldContain(id);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var stored = await db.ProviderAccounts.SingleAsync(p => p.Id == id, TestContext.Current.CancellationToken);
        stored.EncryptedCredential.ShouldNotBeNull().ShouldNotContain(secret);
        string.Join("", await db.AuditLog.Where(a => a.EntityId == id.ToString()).Select(a => a.Details).ToListAsync(TestContext.Current.CancellationToken)).ShouldNotContain(secret);
        using var removeCredential = await client.PutAsJsonAsync($"/api/providers/{id}", new
        {
            name, baseUrl = body.baseUrl, type = body.type, authMode = body.authMode, credential = "",
            residency = body.residency, capabilities = body.capabilities, timeoutSeconds = 10, isEnabled = true,
        }, TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(removeCredential))["hasCredential"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task Usage_reporting_and_lookup_are_department_scoped()
    {
        using var admin = await fixture.ClientAsync();
        var own = await fixture.OrganisationAsync(admin);
        var other = await fixture.OrganisationAsync(admin);
        using var keyResponse = await admin.PostAsJsonAsync("/api/keys", KeyBody(own.Team), TestContext.Current.CancellationToken);
        var key = (await AdminFixture.ReadAsync(keyResponse, HttpStatusCode.Created))["key"]!["id"]!.GetValue<Guid>();
        var requestId = "req_" + Guid.NewGuid().ToString("N");
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            db.UsageRecords.Add(new UsageRecord { RequestId = requestId, Timestamp = DateTimeOffset.UtcNow, VirtualKeyId = key,
                TeamId = own.Team, DepartmentId = own.Department, Endpoint = GatewayEndpoint.ChatCompletions,
                RequestedModel = "test/model", InputTokens = 10, OutputTokens = 20, CostSek = 1.25m, Outcome = RequestOutcome.Success, StatusCode = 200 });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var client = await fixture.ClientAsync("viewer", own.Code);
        using var lookup = await client.GetAsync($"/api/usage/requests/{requestId}", TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(lookup))["costSek"]!.GetValue<decimal>().ShouldBe(1.25m);
        foreach (var group in new[] { "department", "team", "key", "model", "provider", "day" })
        {
            using var summary = await client.GetAsync("/api/usage/summary?groupBy=" + group, TestContext.Current.CancellationToken);
            (await AdminFixture.ReadAsync(summary))["totalCostSek"]!.GetValue<decimal>().ShouldBe(1.25m);
        }
        using var requests = await client.GetAsync("/api/usage/requests", TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(requests))["total"]!.GetValue<int>().ShouldBe(1);
        using var csv = await client.GetAsync("/api/usage/export.csv", TestContext.Current.CancellationToken);
        csv.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await csv.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain(own.Code);
        using var outsider = await fixture.ClientAsync("viewer", other.Code);
        using var forbidden = await outsider.GetAsync($"/api/usage/requests/{requestId}", TestContext.Current.CancellationToken);
        forbidden.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Models_prices_routes_budgets_and_ops_follow_the_contract()
    {
        using var client = await fixture.ClientAsync();
        var suffix = Guid.NewGuid().ToString("N");
        using var providerResponse = await client.PostAsJsonAsync("/api/providers", new
        {
            name = "local-" + suffix, baseUrl = "http://localhost:12345/v1", type = "Ollama", authMode = "None", residency = "OnPrem",
            capabilities = new[] { "ChatCompletions", "Streaming" }, timeoutSeconds = 10, isEnabled = true,
        }, TestContext.Current.CancellationToken);
        var providerId = (await AdminFixture.ReadAsync(providerResponse, HttpStatusCode.Created))["id"]!.GetValue<Guid>();
        using var modelResponse = await client.PostAsJsonAsync("/api/models", new
        {
            providerId, name = "local/model-" + suffix, upstreamModel = "test-model", kind = "Chat", parameterProfile = "Standard",
            isEnabled = true, price = new { inputPerMillionUsd = 1, cachedInputPerMillionUsd = 0.5m, outputPerMillionUsd = 2 },
        }, TestContext.Current.CancellationToken);
        var model = await AdminFixture.ReadAsync(modelResponse, HttpStatusCode.Created);
        var modelId = model["id"]!.GetValue<Guid>();
        model["currentPrice"]!["outputPerMillionUsd"]!.GetValue<decimal>().ShouldBe(2);
        using var price = await client.PostAsJsonAsync($"/api/models/{modelId}/prices", new { inputPerMillionUsd = 2, cachedInputPerMillionUsd = 1, outputPerMillionUsd = 3 }, TestContext.Current.CancellationToken);
        await AdminFixture.ReadAsync(price, HttpStatusCode.Created);
        using var routeResponse = await client.PostAsJsonAsync("/api/routes", new
        {
            name = "test/route-" + suffix, kind = "Chat", targets = new[] { new { modelId, priority = 0, weight = 1 } }, isEnabled = true,
        }, TestContext.Current.CancellationToken);
        var route = await AdminFixture.ReadAsync(routeResponse, HttpStatusCode.Created);
        var routeId = route["id"]!.GetValue<Guid>();
        route["targets"]![0]!["modelName"]!.GetValue<string>().ShouldBe(model["name"]!.GetValue<string>());
        using var cannotDeleteModel = await client.DeleteAsync($"/api/models/{modelId}", TestContext.Current.CancellationToken);
        cannotDeleteModel.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var cannotDeleteProvider = await client.DeleteAsync($"/api/providers/{providerId}", TestContext.Current.CancellationToken);
        cannotDeleteProvider.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var rate = await client.PutAsJsonAsync("/api/settings/exchange-rate", new { sekPerUnit = 10 }, TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(rate))["sekPerUnit"]!.GetValue<decimal>().ShouldBe(10);
        using var catalog = await client.GetAsync("/api/catalog", TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(catalog))["routes"]!.AsArray().ShouldContain(r => r!["name"]!.GetValue<string>() == route["name"]!.GetValue<string>());
        var org = await fixture.OrganisationAsync(client);
        using var budgetResponse = await client.PostAsJsonAsync("/api/budgets", new
        {
            scope = "Team", scopeId = org.Team, limitSek = 100, period = "Monthly", alertThresholds = new[] { 50, 80, 100 }, isActive = true,
        }, TestContext.Current.CancellationToken);
        var budget = await AdminFixture.ReadAsync(budgetResponse, HttpStatusCode.Created);
        budget["scopeName"]!.GetValue<string>().ShouldBe("Team");
        budget["spentSek"]!.GetValue<decimal>().ShouldBe(0);
        using var export = await client.GetAsync("/api/ops/config/export", TestContext.Current.CancellationToken);
        var config = await AdminFixture.ReadAsync(export);
        using var import = await client.PostAsJsonAsync("/api/ops/config/import", config, TestContext.Current.CancellationToken);
        await AdminFixture.ReadAsync(import);
        using var health = await client.GetAsync("/api/ops/health", TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(health))["components"]!.AsArray().ShouldContain(c => c!["status"]!.GetValue<string>() == "Healthy");
        using var deleteRoute = await client.DeleteAsync($"/api/routes/{routeId}", TestContext.Current.CancellationToken);
        deleteRoute.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var deleteModel = await client.DeleteAsync($"/api/models/{modelId}", TestContext.Current.CancellationToken);
        deleteModel.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var deleteProvider = await client.DeleteAsync($"/api/providers/{providerId}", TestContext.Current.CancellationToken);
        deleteProvider.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var deleteBudget = await client.DeleteAsync($"/api/budgets/{budget["id"]!.GetValue<Guid>()}", TestContext.Current.CancellationToken);
        deleteBudget.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Import_rejects_plaintext_credentials_and_rolls_back_invalid_references()
    {
        using var client = await fixture.ClientAsync();
        using var export = await client.GetAsync("/api/ops/config/export", TestContext.Current.CancellationToken);
        var config = await AdminFixture.ReadAsync(export);
        config["providers"] = new JsonArray(JsonNode.Parse("""
            {"configuration":{"name":"invalid-import","baseUrl":"https://provider.invalid/v1","type":"OpenAICompatible","authMode":"Bearer","residency":"Eu","capabilities":["ChatCompletions"],"credential":"must-not-be-imported","timeoutSeconds":10,"isEnabled":true}}
            """));
        using var denied = await client.PostAsJsonAsync("/api/ops/config/import", config, TestContext.Current.CancellationToken);
        denied.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        config["providers"]![0]!["configuration"]!.AsObject().Remove("credential");
        config["models"] = new JsonArray();
        config["routes"] = new JsonArray(JsonNode.Parse("""
            {"name":"invalid-route","kind":"Chat","isEnabled":true,"targets":[{"modelName":"does-not-exist","priority":0,"weight":1}]}
            """));
        using var invalid = await client.PostAsJsonAsync("/api/ops/config/import", config, TestContext.Current.CancellationToken);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var scope = fixture.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().ProviderAccounts.AnyAsync(p => p.Name == "invalid-import", TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("=HYPERLINK(\"evil\")")]
    [InlineData(" @SUM(1)")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("\tvalue")]
    public void Csv_escapes_spreadsheet_formula_cells(string input) =>
        Ume.LlmGateway.AdminApi.ReportEndpoints.Csv(input).ShouldStartWith("\"'");

    private static object KeyBody(Guid team) => new { teamId = team, name = "Key", allowedModels = Array.Empty<string>(), allowedResidencies = Array.Empty<string>(), piiPolicy = "Block", requestsPerMinute = 60 };
}
