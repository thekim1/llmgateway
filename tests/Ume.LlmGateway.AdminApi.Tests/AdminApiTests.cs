using System.Net;
using System.Net.Http.Json;
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
    public async Task Team_request_policy_is_validated_kept_and_cleared()
    {
        using var client = await fixture.ClientAsync();
        var org = await fixture.OrganisationAsync(client);
        using var set = await client.PutAsJsonAsync($"/api/teams/{org.Team}", new
        {
            name = "Policy team", isActive = true,
            requestPolicy = new { maxOutputTokens = 800, systemPrompt = "Svara på svenska.", defaults = new { temperature = 0.2 } },
        }, TestContext.Current.CancellationToken);
        var team = await AdminFixture.ReadAsync(set);
        team["requestPolicy"]!["maxOutputTokens"]!.GetValue<int>().ShouldBe(800);
        team["requestPolicy"]!["defaults"]!["temperature"]!.GetValue<double>().ShouldBe(0.2);

        // Omitted: unchanged.
        using var rename = await client.PutAsJsonAsync($"/api/teams/{org.Team}", new { name = "Renamed policy team", isActive = true }, TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(rename))["requestPolicy"]!["systemPrompt"]!.GetValue<string>().ShouldBe("Svara på svenska.");

        // A policy may not set the model or the conversation.
        using var bad = await client.PutAsJsonAsync($"/api/teams/{org.Team}", new
        {
            name = "Policy team", isActive = true, requestPolicy = new { overrides = new { model = "gpt-5" } },
        }, TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(bad, HttpStatusCode.BadRequest))["errors"]!["requestPolicy"].ShouldNotBeNull();

        // {} clears it.
        using var clear = await client.PutAsJsonAsync($"/api/teams/{org.Team}", new { name = "Policy team", isActive = true, requestPolicy = new { } }, TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(clear))["requestPolicy"].ShouldBeNull();
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
        using var budget = await client.PostAsJsonAsync("/api/budgets", new { scope = "Team", scopeId = other.Team, limitUsd = 100, period = "Monthly", alertThresholds = new[] { 80 }, isActive = true }, TestContext.Current.CancellationToken);
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
    public async Task Key_attachment_policy_defaults_to_allowed_and_is_kept_when_omitted()
    {
        using var client = await fixture.ClientAsync();
        var org = await fixture.OrganisationAsync(client);
        object Body(string? attachmentPolicy) => attachmentPolicy is null
            ? new { teamId = org.Team, name = "Files", allowedModels = Array.Empty<string>(), allowedResidencies = Array.Empty<string>(), piiPolicy = "Off" }
            : new { teamId = org.Team, name = "Files", allowedModels = Array.Empty<string>(), allowedResidencies = Array.Empty<string>(), piiPolicy = "Off", attachmentPolicy };

        using var create = await client.PostAsJsonAsync("/api/keys", Body(null), TestContext.Current.CancellationToken);
        var key = (await AdminFixture.ReadAsync(create, HttpStatusCode.Created))["key"]!;
        key["attachmentPolicy"]!.GetValue<string>().ShouldBe("Allowed");
        var keyId = key["id"]!.GetValue<Guid>();

        using var restrict = await client.PutAsJsonAsync($"/api/keys/{keyId}", Body("None"), TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(restrict))["attachmentPolicy"]!.GetValue<string>().ShouldBe("None");
        using var omit = await client.PutAsJsonAsync($"/api/keys/{keyId}", Body(null), TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(omit))["attachmentPolicy"]!.GetValue<string>().ShouldBe("None");

        using var rotate = await client.PostAsJsonAsync($"/api/keys/{keyId}/rotate", new { mode = "RevokeImmediately" }, TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(rotate))["key"]!["attachmentPolicy"]!.GetValue<string>().ShouldBe("None");

        using var invalid = await client.PostAsJsonAsync("/api/keys", Body("Sometimes"), TestContext.Current.CancellationToken);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Key_provider_features_default_to_off_and_are_kept_when_omitted()
    {
        using var client = await fixture.ClientAsync();
        var org = await fixture.OrganisationAsync(client);
        var ct = TestContext.Current.CancellationToken;
        var basic = new { teamId = org.Team, name = "Tools", allowedModels = Array.Empty<string>(), allowedResidencies = Array.Empty<string>(), piiPolicy = "Off" };

        using var create = await client.PostAsJsonAsync("/api/keys", basic, ct);
        var key = (await AdminFixture.ReadAsync(create, HttpStatusCode.Created))["key"]!;
        key["allowProviderTools"]!.GetValue<bool>().ShouldBeFalse();
        key["allowProviderStorage"]!.GetValue<bool>().ShouldBeFalse();
        var keyId = key["id"]!.GetValue<Guid>();

        using var allow = await client.PutAsJsonAsync($"/api/keys/{keyId}", new
        {
            basic.name, basic.allowedModels, basic.allowedResidencies, basic.piiPolicy, allowProviderTools = true, allowProviderStorage = true,
        }, ct);
        (await AdminFixture.ReadAsync(allow))["allowProviderTools"]!.GetValue<bool>().ShouldBeTrue();
        using var omit = await client.PutAsJsonAsync($"/api/keys/{keyId}", new { basic.name, basic.allowedModels, basic.allowedResidencies, basic.piiPolicy }, ct);
        (await AdminFixture.ReadAsync(omit))["allowProviderStorage"]!.GetValue<bool>().ShouldBeTrue();

        using var rotate = await client.PostAsJsonAsync($"/api/keys/{keyId}/rotate", new { mode = "RevokeImmediately" }, ct);
        var rotated = (await AdminFixture.ReadAsync(rotate))["key"]!;
        rotated["allowProviderTools"]!.GetValue<bool>().ShouldBeTrue();
        rotated["allowProviderStorage"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task Key_limits_provider_allowlist_and_budgets_round_trip()
    {
        using var client = await fixture.ClientAsync();
        var org = await fixture.OrganisationAsync(client);
        using var create = await client.PostAsJsonAsync("/api/keys", new
        {
            teamId = org.Team, name = "Limited", allowedModels = new[] { "ume/chat-standard" }, allowedResidencies = new[] { "Eu" },
            allowedProviders = new[] { " ollama-cloud ", "OLLAMA-CLOUD", "anthropic" }, piiPolicy = "Off", requestsPerMinute = 30, tokensPerMinute = 5000, maxConcurrentRequests = 4, contentGuardPolicy = "Record",
        }, TestContext.Current.CancellationToken);
        var key = (await AdminFixture.ReadAsync(create, HttpStatusCode.Created))["key"]!;
        key["allowedProviders"]!.AsArray().Select(p => p!.GetValue<string>()).ShouldBe(["ollama-cloud", "anthropic"]);
        key["requestsPerMinute"]!.GetValue<int>().ShouldBe(30);
        key["tokensPerMinute"]!.GetValue<int>().ShouldBe(5000);
        key["maxConcurrentRequests"]!.GetValue<int>().ShouldBe(4);
        key["contentGuardPolicy"]!.GetValue<string>().ShouldBe("Record");
        var keyId = key["id"]!.GetValue<Guid>();

        using var update = await client.PutAsJsonAsync($"/api/keys/{keyId}", new
        {
            teamId = org.Team, name = "Limited", allowedModels = Array.Empty<string>(), allowedResidencies = Array.Empty<string>(),
            allowedProviders = Array.Empty<string>(), piiPolicy = "Off",
        }, TestContext.Current.CancellationToken);
        var updated = await AdminFixture.ReadAsync(update);
        updated["allowedProviders"]!.AsArray().ShouldBeEmpty();

        using var invalid = await client.PostAsJsonAsync("/api/keys", new
        {
            teamId = org.Team, name = "Bad", allowedModels = Array.Empty<string>(), allowedResidencies = Array.Empty<string>(),
            allowedProviders = new[] { " " }, piiPolicy = "Off",
        }, TestContext.Current.CancellationToken);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Several budgets on one key, one per period; a second budget for the same period is refused.
        foreach (var period in new[] { "Hourly", "Daily", "Weekly", "Monthly", "Quarterly", "Yearly" })
        {
            using var budget = await client.PostAsJsonAsync("/api/budgets", new
            {
                scope = "VirtualKey", scopeId = keyId, limitUsd = 100, period, alertThresholds = new[] { 80, 100 }, isActive = true,
            }, TestContext.Current.CancellationToken);
            (await AdminFixture.ReadAsync(budget, HttpStatusCode.Created))["period"]!.GetValue<string>().ShouldBe(period);
        }
        using var duplicate = await client.PostAsJsonAsync("/api/budgets", new
        {
            scope = "VirtualKey", scopeId = keyId, limitUsd = 5, period = "Hourly", alertThresholds = new[] { 100 }, isActive = true,
        }, TestContext.Current.CancellationToken);
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var list = await client.GetAsync($"/api/budgets?scope=VirtualKey&scopeId={keyId}", TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(list)).AsArray().Count.ShouldBe(6);
    }

    [Fact]
    public async Task Token_quotas_sit_next_to_cost_budgets_once_per_residency()
    {
        using var client = await fixture.ClientAsync();
        var org = await fixture.OrganisationAsync(client);
        async Task<HttpResponseMessage> AddAsync(object body) => await client.PostAsJsonAsync("/api/budgets", body, TestContext.Current.CancellationToken);

        using (var cost = await AddAsync(new { scope = "Team", scopeId = org.Team, limitUsd = 100, period = "Monthly", alertThresholds = new[] { 80 }, isActive = true }))
        {
            (await AdminFixture.ReadAsync(cost, HttpStatusCode.Created))["unit"]!.GetValue<string>().ShouldBe("Usd");
        }

        using (var quota = await AddAsync(new
        {
            scope = "Team", scopeId = org.Team, limitUsd = 0, unit = "Tokens", limitTokens = 5_000_000, residency = "OnPrem", period = "Monthly",
            alertThresholds = new[] { 80 }, isActive = true,
        }))
        {
            var created = await AdminFixture.ReadAsync(quota, HttpStatusCode.Created);
            created["limitTokens"]!.GetValue<long>().ShouldBe(5_000_000);
            created["residency"]!.GetValue<string>().ShouldBe("OnPrem");
            created["spentTokens"]!.GetValue<long>().ShouldBe(0);
        }

        // One quota per owner, period and residency; another residency is a quota of its own.
        using (var again = await AddAsync(new { scope = "Team", scopeId = org.Team, limitUsd = 0, unit = "Tokens", limitTokens = 1, residency = "OnPrem", period = "Monthly", alertThresholds = new[] { 80 }, isActive = true }))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        using (var all = await AddAsync(new { scope = "Team", scopeId = org.Team, limitUsd = 0, unit = "Tokens", limitTokens = 9_000_000, period = "Monthly", alertThresholds = new[] { 80 }, isActive = true }))
        {
            all.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        // A token quota needs a token limit; only token quotas count one residency.
        using (var noLimit = await AddAsync(new { scope = "Team", scopeId = org.Team, limitUsd = 0, unit = "Tokens", period = "Weekly", alertThresholds = new[] { 80 }, isActive = true }))
        {
            (await AdminFixture.ReadAsync(noLimit, HttpStatusCode.BadRequest))["errors"]!["limitTokens"].ShouldNotBeNull();
        }

        using (var costWithResidency = await AddAsync(new { scope = "Team", scopeId = org.Team, limitUsd = 5, residency = "OnPrem", period = "Weekly", alertThresholds = new[] { 80 }, isActive = true }))
        {
            (await AdminFixture.ReadAsync(costWithResidency, HttpStatusCode.BadRequest))["errors"]!["residency"].ShouldNotBeNull();
        }

        using var list = await client.GetAsync($"/api/budgets?scope=Team&scopeId={org.Team}", TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(list)).AsArray().Select(b => b!["unit"]!.GetValue<string>()).Order().ShouldBe(["Tokens", "Tokens", "Usd"]);
    }

    [Fact]
    public async Task Budget_period_cannot_be_changed_to_one_that_already_exists()
    {
        using var client = await fixture.ClientAsync();
        var org = await fixture.OrganisationAsync(client);
        async Task<Guid> AddAsync(string period)
        {
            using var response = await client.PostAsJsonAsync("/api/budgets", new
            {
                scope = "Team", scopeId = org.Team, limitUsd = 10, period, alertThresholds = new[] { 100 }, isActive = true,
            }, TestContext.Current.CancellationToken);
            return (await AdminFixture.ReadAsync(response, HttpStatusCode.Created))["id"]!.GetValue<Guid>();
        }
        await AddAsync("Daily");
        var monthly = await AddAsync("Monthly");
        using var clash = await client.PutAsJsonAsync($"/api/budgets/{monthly}", new
        {
            scope = "Team", scopeId = org.Team, limitUsd = 20, period = "Daily", alertThresholds = new[] { 100 }, isActive = true,
        }, TestContext.Current.CancellationToken);
        clash.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var sameKeepsWorking = await client.PutAsJsonAsync($"/api/budgets/{monthly}", new
        {
            scope = "Team", scopeId = org.Team, limitUsd = 20, period = "Monthly", alertThresholds = new[] { 100 }, isActive = true,
        }, TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(sameKeepsWorking))["limitUsd"]!.GetValue<decimal>().ShouldBe(20);
    }

    [Fact]
    public async Task Gateway_admin_can_reveal_and_copy_a_key_and_every_access_is_audited()
    {
        using var client = await fixture.ClientAsync();
        var org = await fixture.OrganisationAsync(client);
        using var create = await client.PostAsJsonAsync("/api/keys", KeyBody(org.Team), TestContext.Current.CancellationToken);
        var created = await AdminFixture.ReadAsync(create, HttpStatusCode.Created);
        var keyId = created["key"]!["id"]!.GetValue<Guid>();
        var secret = created["secret"]!.GetValue<string>();
        created["key"]!["canReveal"]!.GetValue<bool>().ShouldBeTrue();

        using var reveal = await client.PostAsJsonAsync($"/api/keys/{keyId}/reveal", new { purpose = "Reveal" }, TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(reveal))["secret"]!.GetValue<string>().ShouldBe(secret);
        using var copy = await client.PostAsJsonAsync($"/api/keys/{keyId}/reveal", new { purpose = "Copy" }, TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(copy))["secret"]!.GetValue<string>().ShouldBe(secret);

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var audits = await db.AuditLog.Where(a => a.EntityId == keyId.ToString() && (a.Action == "reveal" || a.Action == "copy")).ToListAsync(TestContext.Current.CancellationToken);
        audits.Select(a => a.Action).Order().ShouldBe(["copy", "reveal"]);
        audits.ShouldAllBe(a => !string.IsNullOrWhiteSpace(a.Actor));
        string.Join("", audits.Select(a => a.Details)).ShouldNotContain(secret);

        var stored = await db.VirtualKeys.SingleAsync(k => k.Id == keyId, TestContext.Current.CancellationToken);
        stored.EncryptedSecret.ShouldNotBeNullOrEmpty();
        stored.EncryptedSecret.ShouldNotContain(secret);

        using var revoke = await client.PostAsync($"/api/keys/{keyId}/revoke", null, TestContext.Current.CancellationToken);
        revoke.EnsureSuccessStatusCode();
        using var afterRevoke = await client.PostAsJsonAsync($"/api/keys/{keyId}/reveal", new { purpose = "Reveal" }, TestContext.Current.CancellationToken);
        afterRevoke.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("department-admin")]
    [InlineData("viewer")]
    public async Task Only_gateway_admin_can_reveal_keys(string role)
    {
        using var admin = await fixture.ClientAsync();
        var org = await fixture.OrganisationAsync(admin);
        using var create = await admin.PostAsJsonAsync("/api/keys", KeyBody(org.Team), TestContext.Current.CancellationToken);
        var keyId = (await AdminFixture.ReadAsync(create, HttpStatusCode.Created))["key"]!["id"]!.GetValue<Guid>();
        using var other = await fixture.ClientAsync(role, org.Code);
        using var response = await other.PostAsJsonAsync($"/api/keys/{keyId}/reveal", new { purpose = "Reveal" }, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Keys_without_stored_secret_cannot_be_revealed()
    {
        using var client = await fixture.ClientAsync();
        var org = await fixture.OrganisationAsync(client);
        using var create = await client.PostAsJsonAsync("/api/keys", KeyBody(org.Team), TestContext.Current.CancellationToken);
        var keyId = (await AdminFixture.ReadAsync(create, HttpStatusCode.Created))["key"]!["id"]!.GetValue<Guid>();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            (await db.VirtualKeys.SingleAsync(k => k.Id == keyId, TestContext.Current.CancellationToken)).EncryptedSecret = null;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var get = await client.GetAsync($"/api/keys/{keyId}", TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(get))["canReveal"]!.GetValue<bool>().ShouldBeFalse();
        using var response = await client.PostAsJsonAsync($"/api/keys/{keyId}/reveal", new { purpose = "Reveal" }, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
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
    public async Task Stored_provider_credential_is_not_kept_for_a_new_address()
    {
        using var client = await fixture.ClientAsync();
        var name = "provider-" + Guid.NewGuid().ToString("N");
        object Body(string baseUrl, string? credential) => credential is null
            ? new { name, baseUrl, type = "OpenAICompatible", authMode = "Bearer", residency = "Eu", capabilities = new[] { "ChatCompletions" }, timeoutSeconds = 10, isEnabled = true }
            : new { name, baseUrl, type = "OpenAICompatible", authMode = "Bearer", residency = "Eu", capabilities = new[] { "ChatCompletions" }, timeoutSeconds = 10, isEnabled = true, credential };
        using var create = await client.PostAsJsonAsync("/api/providers", Body("https://provider.invalid/v1", "secret"), TestContext.Current.CancellationToken);
        var id = (await AdminFixture.ReadAsync(create, HttpStatusCode.Created))["id"]!.GetValue<Guid>();

        // Another path on the same host keeps the credential.
        using var samePlace = await client.PutAsJsonAsync($"/api/providers/{id}", Body("https://PROVIDER.invalid/openai/v1", null), TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(samePlace))["hasCredential"]!.GetValue<bool>().ShouldBeTrue();

        // Another host or port without the credential is refused, and nothing changes (a scheme change: see SameOrigin below).
        foreach (var elsewhere in new[] { "https://attacker.invalid/v1", "https://provider.invalid:8443/v1", "https://provider.invalid.attacker.example/v1" })
        {
            using var moved = await client.PutAsJsonAsync($"/api/providers/{id}", Body(elsewhere, null), TestContext.Current.CancellationToken);
            moved.StatusCode.ShouldBe(HttpStatusCode.BadRequest, elsewhere);
            (await AdminFixture.ReadAsync(moved, HttpStatusCode.BadRequest))["errors"]!["credential"].ShouldNotBeNull();
        }

        using var get = await client.GetAsync("/api/providers", TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(get)).AsArray().Single(p => p!["id"]!.GetValue<Guid>() == id)!["baseUrl"]!.GetValue<string>()
            .ShouldBe("https://PROVIDER.invalid/openai/v1");

        // Entering the credential again (or removing it) allows the move.
        using var withCredential = await client.PutAsJsonAsync($"/api/providers/{id}", Body("https://other.invalid/v1", "new-secret"), TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(withCredential))["hasCredential"]!.GetValue<bool>().ShouldBeTrue();
        using var cleared = await client.PutAsJsonAsync($"/api/providers/{id}", Body("https://third.invalid/v1", ""), TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(cleared))["hasCredential"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Theory]
    [InlineData("https://provider.invalid/v1", "https://PROVIDER.invalid/openai/v1?api-version=1", true)]
    [InlineData("https://provider.invalid/v1", "https://provider.invalid:443/v2", true)] // default port written out
    [InlineData("https://provider.invalid/v1", "http://provider.invalid/v1", false)]
    [InlineData("https://provider.invalid/v1", "https://provider.invalid:8443/v1", false)]
    [InlineData("https://provider.invalid/v1", "https://provider.invalid.example/v1", false)]
    [InlineData("http://10.0.0.5:11434/v1", "http://10.0.0.6:11434/v1", false)]
    public void Same_origin_compares_scheme_host_and_port(string current, string next, bool same) =>
        ProviderCatalogService.SameOrigin(current, next).ShouldBe(same);

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
                RequestedModel = "test/model", InputTokens = 10, OutputTokens = 20, CostUsd = 1.25m, Outcome = RequestOutcome.Success, StatusCode = 200 });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var client = await fixture.ClientAsync("viewer", own.Code);
        using var lookup = await client.GetAsync($"/api/usage/requests/{requestId}", TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(lookup))["costUsd"]!.GetValue<decimal>().ShouldBe(1.25m);
        foreach (var group in new[] { "department", "team", "key", "model", "provider", "day" })
        {
            using var summary = await client.GetAsync("/api/usage/summary?groupBy=" + group, TestContext.Current.CancellationToken);
            (await AdminFixture.ReadAsync(summary))["totalCostUsd"]!.GetValue<decimal>().ShouldBe(1.25m);
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
    public async Task Usage_requests_filter_to_errors_model_and_provider()
    {
        using var admin = await fixture.ClientAsync();
        var org = await fixture.OrganisationAsync(admin);
        using var keyResponse = await admin.PostAsJsonAsync("/api/keys", KeyBody(org.Team), TestContext.Current.CancellationToken);
        var key = (await AdminFixture.ReadAsync(keyResponse, HttpStatusCode.Created))["key"]!["id"]!.GetValue<Guid>();
        var model = "errors-" + Guid.NewGuid().ToString("N")[..8];
        UsageRecord Record(RequestOutcome outcome, int status, string? provider, int? providerFirstTokenMs = null) => new()
        {
            RequestId = "req_" + Guid.NewGuid().ToString("N"), Timestamp = DateTimeOffset.UtcNow, VirtualKeyId = key, TeamId = org.Team,
            DepartmentId = org.Department, Endpoint = GatewayEndpoint.ChatCompletions, RequestedModel = model, ProviderName = provider,
            Outcome = outcome, StatusCode = status, Streamed = providerFirstTokenMs is not null, ProviderFirstTokenMs = providerFirstTokenMs,
        };
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            db.UsageRecords.AddRange(
                Record(RequestOutcome.Success, 200, "eu", 120), Record(RequestOutcome.Success, 200, "eu"),
                Record(RequestOutcome.ProviderError, 502, "eu"), Record(RequestOutcome.ProviderError, 502, "onprem"),
                Record(RequestOutcome.BudgetExceeded, 429, null), Record(RequestOutcome.ClientCancelled, 499, "eu"));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        async Task<JsonNode> ListAsync(string query)
        {
            using var response = await admin.GetAsync($"/api/usage/requests?departmentId={org.Department}&model={model}{query}", TestContext.Current.CancellationToken);
            return await AdminFixture.ReadAsync(response);
        }

        (await ListAsync(""))["total"]!.GetValue<int>().ShouldBe(6);
        var errors = await ListAsync("&errors=true");
        errors["total"]!.GetValue<int>().ShouldBe(4); // everything that did not succeed: provider errors, refusals and cancellations
        errors["items"]!.AsArray().ShouldAllBe(i => i!["outcome"]!.GetValue<string>() != "Success");
        (await ListAsync("&errors=true&provider=eu"))["total"]!.GetValue<int>().ShouldBe(2);
        (await ListAsync("&errors=true&provider=-"))["total"]!.GetValue<int>().ShouldBe(1); // refused before any provider
        (await ListAsync("&outcome=ProviderError"))["total"]!.GetValue<int>().ShouldBe(2);
        var streamed = (await ListAsync("&provider=eu"))["items"]!.AsArray().Where(i => i!["providerFirstTokenMs"] is not null).ToList();
        streamed.Single()!["providerFirstTokenMs"]!.GetValue<int>().ShouldBe(120);
    }

    [Fact]
    public async Task Image_models_carry_a_per_picture_price_through_export_and_import()
    {
        using var client = await fixture.ClientAsync();
        var suffix = Guid.NewGuid().ToString("N");
        using var providerResponse = await client.PostAsJsonAsync("/api/providers", new
        {
            name = "images-" + suffix, baseUrl = "https://images.invalid/v1", type = "OpenAICompatible", authMode = "None", residency = "Eu",
            capabilities = new[] { "Images" }, timeoutSeconds = 60, isEnabled = true,
        }, TestContext.Current.CancellationToken);
        var provider = await AdminFixture.ReadAsync(providerResponse, HttpStatusCode.Created);
        provider["capabilities"]!.AsArray().Select(c => c!.GetValue<string>()).ShouldBe(["Images"]);

        using var modelResponse = await client.PostAsJsonAsync("/api/models", new
        {
            providerId = provider["id"]!.GetValue<Guid>(), name = "images-" + suffix + "/pic", upstreamModel = "dall-e-3", kind = "Image", parameterProfile = "Standard",
            isEnabled = true, price = new { inputPerMillionUsd = 0, cachedInputPerMillionUsd = 0, outputPerMillionUsd = 0, perImageUsd = 0.04m },
        }, TestContext.Current.CancellationToken);
        var model = await AdminFixture.ReadAsync(modelResponse, HttpStatusCode.Created);
        model["kind"]!.GetValue<string>().ShouldBe("Image");
        model["currentPrice"]!["perImageUsd"]!.GetValue<decimal>().ShouldBe(0.04m);

        // Exporting and importing again keeps the price; a changed per-picture price on the same date is refused like any other price edit.
        using var export = await client.GetAsync("/api/ops/config/export", TestContext.Current.CancellationToken);
        var document = await AdminFixture.ReadAsync(export);
        var exported = document["models"]!.AsArray().Single(m => m!["configuration"]!["name"]!.GetValue<string>() == "images-" + suffix + "/pic")!;
        exported["prices"]![0]!["perImageUsd"]!.GetValue<decimal>().ShouldBe(0.04m);
        using var import = await client.PostAsJsonAsync("/api/ops/config/import", document, TestContext.Current.CancellationToken);
        await AdminFixture.ReadAsync(import);

        exported["prices"]![0]!["perImageUsd"] = 0.08m;
        using var changed = await client.PostAsJsonAsync("/api/ops/config/import", document, TestContext.Current.CancellationToken);
        changed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Cache_write_price_is_optional_and_survives_export_and_import()
    {
        using var client = await fixture.ClientAsync();
        var suffix = Guid.NewGuid().ToString("N");
        using var providerResponse = await client.PostAsJsonAsync("/api/providers", new
        {
            name = "claude-" + suffix, baseUrl = "https://claude.invalid", type = "Anthropic", authMode = "XApiKeyHeader", residency = "External",
            capabilities = new[] { "AnthropicMessages", "ChatCompletions" }, timeoutSeconds = 60, isEnabled = true,
        }, TestContext.Current.CancellationToken);
        var provider = await AdminFixture.ReadAsync(providerResponse, HttpStatusCode.Created);

        using var modelResponse = await client.PostAsJsonAsync("/api/models", new
        {
            providerId = provider["id"]!.GetValue<Guid>(), name = "claude-" + suffix + "/sonnet", upstreamModel = "claude-sonnet", kind = "Chat", parameterProfile = "Standard",
            isEnabled = true, price = new { inputPerMillionUsd = 3, cachedInputPerMillionUsd = 0.3m, cacheWriteInputPerMillionUsd = 3.75m, outputPerMillionUsd = 15 },
        }, TestContext.Current.CancellationToken);
        var model = await AdminFixture.ReadAsync(modelResponse, HttpStatusCode.Created);
        model["currentPrice"]!["cacheWriteInputPerMillionUsd"]!.GetValue<decimal>().ShouldBe(3.75m);

        // A price without one bills cache writes at the input price (null).
        using var plain = await client.PostAsJsonAsync($"/api/models/{model["id"]!.GetValue<Guid>()}/prices",
            new { inputPerMillionUsd = 3, cachedInputPerMillionUsd = 0.3m, outputPerMillionUsd = 15 }, TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(plain, HttpStatusCode.Created))["cacheWriteInputPerMillionUsd"].ShouldBeNull();

        using var export = await client.GetAsync("/api/ops/config/export", TestContext.Current.CancellationToken);
        var document = await AdminFixture.ReadAsync(export);
        var exported = document["models"]!.AsArray().Single(m => m!["configuration"]!["name"]!.GetValue<string>() == "claude-" + suffix + "/sonnet")!;
        exported["prices"]!.AsArray().Select(p => p!["cacheWriteInputPerMillionUsd"]?.GetValue<decimal>()).ShouldBe([3.75m, null], ignoreOrder: true);
        using var import = await client.PostAsJsonAsync("/api/ops/config/import", document, TestContext.Current.CancellationToken);
        await AdminFixture.ReadAsync(import);

        exported["prices"]![0]!["cacheWriteInputPerMillionUsd"] = 6m; // differs from either stored price
        using var changed = await client.PostAsJsonAsync("/api/ops/config/import", document, TestContext.Current.CancellationToken);
        changed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
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
        // Costs are USD only: there is no exchange rate to set any more.
        using var rate = await client.GetAsync("/api/settings/exchange-rate", TestContext.Current.CancellationToken);
        rate.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var catalog = await client.GetAsync("/api/catalog", TestContext.Current.CancellationToken);
        (await AdminFixture.ReadAsync(catalog))["routes"]!.AsArray().ShouldContain(r => r!["name"]!.GetValue<string>() == route["name"]!.GetValue<string>());
        var org = await fixture.OrganisationAsync(client);
        using var budgetResponse = await client.PostAsJsonAsync("/api/budgets", new
        {
            scope = "Team", scopeId = org.Team, limitUsd = 100, period = "Monthly", alertThresholds = new[] { 50, 80, 100 }, isActive = true,
        }, TestContext.Current.CancellationToken);
        var budget = await AdminFixture.ReadAsync(budgetResponse, HttpStatusCode.Created);
        budget["scopeName"]!.GetValue<string>().ShouldBe("Team");
        budget["spentUsd"]!.GetValue<decimal>().ShouldBe(0);
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
    [InlineData("+1+1")]
    [InlineData("-A1")]
    [InlineData("\tvalue")]
    [InlineData("a;=b")]
    public void Csv_escapes_spreadsheet_formula_cells_and_the_delimiter(string input)
    {
        var cell = ReportEndpoints.Csv(input);
        if (input.Contains(';', StringComparison.Ordinal) || input.Contains('"', StringComparison.Ordinal))
        {
            cell.ShouldStartWith("\"");
        }
        if (input.TrimStart()[0] is '=' or '+' or '-' or '@' || input[0] == '\t')
        {
            cell.TrimStart('"').ShouldStartWith("'");
        }
    }

    private static object KeyBody(Guid team) => new { teamId = team, name = "Key", allowedModels = Array.Empty<string>(), allowedResidencies = Array.Empty<string>(), piiPolicy = "Block", requestsPerMinute = 60 };
}
