using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Requests;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.TestKit;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>Integrations on the data plane (docs/integrations.md): key roles, <c>/v1/ume/context</c> and the model facts.</summary>
public sealed class IntegrationGatewayTests(GatewayFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_assigned_key_cannot_be_used_directly()
    {
        var integration = await CreateIntegrationAsync();
        var assigned = await fixture.CreateKeyAsync(k => Assign(k, integration, IntegrationRole.Assigned));

        using var chat = await fixture.SendAsync(assigned);
        chat.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(chat)).ShouldBe("integration_only");

        using var context = await GetAsync(assigned, "/v1/ume/context");
        context.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Keys_of_a_deactivated_integration_are_refused()
    {
        var integration = await CreateIntegrationAsync();
        var credential = await fixture.CreateKeyAsync(k => Assign(k, integration, IntegrationRole.Credential));
        using (var ok = await fixture.SendAsync(credential))
        {
            ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await UpdateIntegrationAsync(integration, i => i.IsActive = false);
        using var refused = await fixture.SendAsync(credential);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(refused)).ShouldBe("key_disabled");
    }

    [Fact]
    public async Task The_context_of_a_credential_key_lists_its_integrations_assigned_keys_without_secrets()
    {
        var integration = await CreateIntegrationAsync();
        var credential = await fixture.CreateKeyAsync(k => Assign(k, integration, IntegrationRole.Credential));
        var chatbot = await fixture.CreateKeyAsync(k =>
        {
            Assign(k, integration, IntegrationRole.Assigned);
            k.Name = "Chatbot";
            k.AllowedResidencies = [DataResidency.OnPrem, DataResidency.Eu];
            k.PiiPolicy = PiiPolicy.Redact;
            k.RequestsPerMinute = 120;
            k.RequestPolicy = new RequestPolicy { MaxOutputTokens = 2000 };
        });
        var revoked = await fixture.CreateKeyAsync(k => { Assign(k, integration, IntegrationRole.Assigned); k.RevokedAt = DateTimeOffset.UtcNow.AddMinutes(-1); });
        var other = await CreateIntegrationAsync();
        var foreign = await fixture.CreateKeyAsync(k => Assign(k, other, IntegrationRole.Assigned));

        using var response = await GetAsync(credential, "/v1/ume/context");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;

        body["integration"]!["id"]!.GetValue<Guid>().ShouldBe(integration);
        body["key"]!["integration_role"]!.GetValue<string>().ShouldBe("Credential");
        ((JsonArray)body["gateway"]!["features"]!).Select(f => f!.GetValue<string>()).ShouldContain("integrations");
        var keys = ((JsonArray)body["assigned_keys"]!).Select(k => k!).ToList();
        keys.Select(k => k["id"]!.GetValue<Guid>()).ShouldBe([chatbot.Id]); // not the revoked key, not another integration's
        keys.ShouldNotContain(k => k["id"]!.GetValue<Guid>() == revoked.Id || k["id"]!.GetValue<Guid>() == foreign.Id);
        var key = keys[0];
        key["name"]!.GetValue<string>().ShouldBe("Chatbot");
        key["team_id"]!.GetValue<Guid>().ShouldBe(chatbot.TeamId);
        key["status"]!.GetValue<string>().ShouldBe("Active");
        ((JsonArray)key["allowed_residencies"]!).Select(r => r!.GetValue<string>()).ShouldBe(["OnPrem", "Eu"]);
        key["pii_policy"]!.GetValue<string>().ShouldBe("Redact");
        key["requests_per_minute"]!.GetValue<int>().ShouldBe(120);
        key["max_output_tokens"]!.GetValue<int>().ShouldBe(2000);
        key["has_system_prompt"]!.GetValue<bool>().ShouldBeFalse();
        var text = body.ToJsonString();
        text.ShouldNotContain(chatbot.Secret);
        text.ShouldNotContain("hash", Case.Insensitive);

        // The credential key's team and the assigned key's team, with the department.
        ((JsonArray)body["teams"]!).Select(t => t!["id"]!.GetValue<Guid>()).ShouldBe([credential.TeamId, chatbot.TeamId], ignoreOrder: true);
        ((JsonArray)body["departments"]!).Select(d => d!["id"]!.GetValue<Guid>()).ShouldBe([fixture.DepartmentId]);
    }

    [Fact]
    public async Task The_context_of_an_ordinary_key_is_the_key_its_team_and_department()
    {
        var key = await fixture.CreateKeyAsync();
        using var response = await GetAsync(key, "/v1/ume/context");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        body["integration"].ShouldBeNull();
        body["key"]!["id"]!.GetValue<Guid>().ShouldBe(key.Id);
        body["key"]!["integration_role"]!.GetValue<string>().ShouldBe("None");
        ((JsonArray)body["assigned_keys"]!).ShouldBeEmpty();
        ((JsonArray)body["teams"]!).Select(t => t!["id"]!.GetValue<Guid>()).ShouldBe([key.TeamId]);
    }

    [Fact]
    public async Task The_context_answers_304_while_unchanged_and_200_after_a_change()
    {
        var integration = await CreateIntegrationAsync();
        var credential = await fixture.CreateKeyAsync(k => Assign(k, integration, IntegrationRole.Credential));
        string tag;
        using (var first = await GetAsync(credential, "/v1/ume/context"))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
            first.Headers.CacheControl!.Private.ShouldBeTrue();
            tag = first.Headers.ETag!.Tag;
        }

        using (var unchanged = await GetAsync(credential, "/v1/ume/context", tag))
        {
            unchanged.StatusCode.ShouldBe(HttpStatusCode.NotModified);
            (await unchanged.Content.ReadAsByteArrayAsync(Ct)).ShouldBeEmpty();
        }

        await fixture.CreateKeyAsync(k => Assign(k, integration, IntegrationRole.Assigned));
        using var changed = await GetAsync(credential, "/v1/ume/context", tag);
        changed.StatusCode.ShouldBe(HttpStatusCode.OK);
        changed.Headers.ETag!.Tag.ShouldNotBe(tag);
    }

    [Fact]
    public async Task The_model_list_gives_limits_features_prices_and_the_models_own_residency()
    {
        var name = await fixture.AddDeploymentAsync("external", "ok", contextWindow: 128_000);
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var deployment = await db.ModelDeployments.SingleAsync(d => d.Name == name, Ct);
            deployment.MaxOutputTokens = 16_000;
            deployment.Features = ["tools", "vision"];
            deployment.Residency = DataResidency.Eu; // processes data in the EU although its provider is external
            await db.SaveChangesAsync(Ct);
        }

        var key = await fixture.CreateKeyAsync();
        using var response = await GetAsync(key, "/v1/models");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag.ShouldNotBeNull();
        var model = ((JsonArray)JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["data"]!).Single(m => m!["id"]!.GetValue<string>() == name)!;
        var facts = model["ume"]!;
        facts["residency"]!.GetValue<string>().ShouldBe("Eu");
        facts["context_window"]!.GetValue<int>().ShouldBe(128_000);
        facts["max_output_tokens"]!.GetValue<int>().ShouldBe(16_000);
        ((JsonArray)facts["features"]!).Select(f => f!.GetValue<string>()).ShouldBe(["tools", "vision"]);
        facts["pricing"]!["input_per_million_usd"]!.GetValue<decimal>().ShouldBe(1m);
        facts["pricing"]!["output_per_million_usd"]!.GetValue<decimal>().ShouldBe(2m);
    }

    [Fact]
    public async Task An_aliases_limits_are_the_smallest_of_its_targets_and_unknown_when_one_does_not_know()
    {
        var large = await fixture.AddDeploymentAsync("eu", "ok", contextWindow: 200_000);
        var small = await fixture.AddDeploymentAsync("onprem", "ok", contextWindow: 32_000);
        var unknown = await fixture.AddDeploymentAsync("onprem", "ok", contextWindow: null);
        var route = await fixture.CreateRouteAsync(large, small);
        var partial = await fixture.CreateRouteAsync(large, unknown);
        var key = await fixture.CreateKeyAsync();

        using var response = await GetAsync(key, "/v1/models");
        var data = (JsonArray)JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["data"]!;
        var alias = data.Single(m => m!["id"]!.GetValue<string>() == route)!["ume"]!;
        alias["type"]!.GetValue<string>().ShouldBe("alias");
        alias["context_window"]!.GetValue<int>().ShouldBe(32_000);
        ((JsonArray)alias["residencies"]!).Select(r => r!.GetValue<string>()).ShouldBe(["Eu", "OnPrem"], ignoreOrder: true);
        alias["pricing"]!["input_per_million_usd"]!.GetValue<decimal>().ShouldBe(1m); // the first target's
        data.Single(m => m!["id"]!.GetValue<string>() == partial)!["ume"]!["context_window"].ShouldBeNull();
    }

    [Fact]
    public async Task A_credential_key_acts_as_an_assigned_key_whose_team_and_department_are_charged()
    {
        var integration = await CreateIntegrationAsync();
        var credential = await fixture.CreateKeyAsync(k => Assign(k, integration, IntegrationRole.Credential));
        var chatbot = await fixture.CreateKeyAsync(k => Assign(k, integration, IntegrationRole.Assigned));

        using var response = await fixture.SendAsync(credential, headers: new Dictionary<string, string>
        {
            [OnBehalfOf] = chatbot.Id.ToString(), ["x-ume-run-id"] = " run-42 ", ["x-ume-step"] = "answer",
        });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues(OnBehalfOf).Single().ShouldBe(chatbot.Id.ToString());

        var usage = await fixture.UsageAsync(response);
        usage.VirtualKeyId.ShouldBe(chatbot.Id);
        usage.TeamId.ShouldBe(chatbot.TeamId);
        usage.ViaKeyId.ShouldBe(credential.Id);
        usage.RunId.ShouldBe("run-42");
        usage.Step.ShouldBe("answer");
    }

    [Fact]
    public async Task The_assigned_keys_models_and_budget_apply_not_the_credential_keys()
    {
        var integration = await CreateIntegrationAsync();
        var credential = await fixture.CreateKeyAsync(k => Assign(k, integration, IntegrationRole.Credential));
        var limited = await fixture.CreateKeyAsync(k => { Assign(k, integration, IntegrationRole.Assigned); k.AllowedModels = ["onprem/ok"]; });
        var broke = await fixture.CreateKeyAsync(k => Assign(k, integration, IntegrationRole.Assigned));
        await fixture.AddBudgetAsync(BudgetScope.VirtualKey, broke.Id, 0);

        using (var notAllowed = await fixture.SendAsync(credential, "eu/ok", headers: Acting(limited)))
        {
            notAllowed.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await ErrorCodeAsync(notAllowed)).ShouldBe("model_not_allowed");
        }

        using (var spent = await fixture.SendAsync(credential, headers: Acting(broke)))
        {
            ((int)spent.StatusCode).ShouldBe(402);
            (await ErrorCodeAsync(spent)).ShouldBe("budget_exceeded");
        }

        using var own = await fixture.SendAsync(credential); // the credential key itself is not limited by either
        own.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fixture.UsageAsync(own)).ViaKeyId.ShouldBeNull();

        using var models = await GetAsync(credential, "/v1/models", headers: Acting(limited));
        ((JsonArray)JsonNode.Parse(await models.Content.ReadAsStringAsync(Ct))!["data"]!).Select(m => m!["id"]!.GetValue<string>()).ShouldBe(["onprem/ok"]);
    }

    [Fact]
    public async Task Acting_as_a_key_is_refused_unless_it_is_an_assigned_key_of_the_credential_keys_integration()
    {
        var integration = await CreateIntegrationAsync();
        var credential = await fixture.CreateKeyAsync(k => Assign(k, integration, IntegrationRole.Credential));
        var foreign = await fixture.CreateKeyAsync(k => Assign(k, integration, IntegrationRole.Assigned));
        var other = await CreateIntegrationAsync();
        var otherKey = await fixture.CreateKeyAsync(k => Assign(k, other, IntegrationRole.Assigned));
        var disabled = await fixture.CreateKeyAsync(k => { Assign(k, integration, IntegrationRole.Assigned); k.IsEnabled = false; });
        var revoked = await fixture.CreateKeyAsync(k => { Assign(k, integration, IntegrationRole.Assigned); k.RevokedAt = DateTimeOffset.UtcNow.AddMinutes(-1); });
        var expired = await fixture.CreateKeyAsync(k => { Assign(k, integration, IntegrationRole.Assigned); k.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); });
        var ordinary = await fixture.CreateKeyAsync();

        (await RefusalAsync(ordinary, foreign.Id.ToString())).ShouldBe((403, "delegation_not_allowed"));
        (await RefusalAsync(credential, otherKey.Id.ToString())).ShouldBe((403, "delegation_not_allowed"));
        (await RefusalAsync(credential, Guid.NewGuid().ToString())).ShouldBe((403, "delegation_not_allowed"));
        (await RefusalAsync(credential, "chatbot")).ShouldBe((400, "invalid_request"));
        (await RefusalAsync(credential, disabled.Id.ToString())).ShouldBe((403, "key_disabled"));
        // The credential key authenticated, so a refusal of the assigned key is never a 401.
        (await RefusalAsync(credential, revoked.Id.ToString())).ShouldBe((403, "key_revoked"));
        (await RefusalAsync(credential, expired.Id.ToString())).ShouldBe((403, "key_expired"));
    }

    [Fact]
    public async Task A_credential_key_that_requires_the_residency_header_requires_it_when_acting()
    {
        var integration = await CreateIntegrationAsync();
        var credential = await fixture.CreateKeyAsync(k => { Assign(k, integration, IntegrationRole.Credential); k.RequireMaxResidency = true; });
        var chatbot = await fixture.CreateKeyAsync(k => Assign(k, integration, IntegrationRole.Assigned));

        using var missing = await fixture.SendAsync(credential, headers: Acting(chatbot));
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var headers = Acting(chatbot);
        headers["x-ume-max-residency"] = "Eu";
        using var ok = await fixture.SendAsync(credential, headers: headers);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_credential_keys_residencies_are_an_upper_bound_on_the_keys_it_acts_as()
    {
        var integration = await CreateIntegrationAsync();
        var worker = await fixture.CreateKeyAsync(k => { Assign(k, integration, IntegrationRole.Credential); k.AllowedResidencies = [DataResidency.OnPrem]; });
        var api = await fixture.CreateKeyAsync(k => Assign(k, integration, IntegrationRole.Credential));
        var open = await fixture.CreateKeyAsync(k => Assign(k, integration, IntegrationRole.Assigned));
        var both = await fixture.CreateKeyAsync(k => { Assign(k, integration, IntegrationRole.Assigned); k.AllowedResidencies = [DataResidency.OnPrem, DataResidency.Eu]; });
        var euOnly = await fixture.CreateKeyAsync(k => { Assign(k, integration, IntegrationRole.Assigned); k.AllowedResidencies = [DataResidency.Eu]; });

        foreach (var chatbot in new[] { open, both })
        {
            using var above = await fixture.SendAsync(worker, "eu/ok", headers: Acting(chatbot));
            above.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable); // as for any key whose residencies exclude the model's providers
            (await ErrorCodeAsync(above)).ShouldBe("no_eligible_provider");
            using var within = await fixture.SendAsync(worker, "onprem/ok", headers: Acting(chatbot));
            within.StatusCode.ShouldBe(HttpStatusCode.OK);

            // A credential key without a list bounds nothing.
            using var unbounded = await fixture.SendAsync(api, "eu/ok", headers: Acting(chatbot));
            unbounded.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Nothing in common: refused as a delegation, not run with either list.
        (await RefusalAsync(worker, euOnly.Id.ToString())).ShouldBe((403, "delegation_not_allowed"));

        using var models = await GetAsync(worker, "/v1/models", headers: Acting(both));
        var ids = ((JsonArray)JsonNode.Parse(await models.Content.ReadAsStringAsync(Ct))!["data"]!).Select(m => m!["id"]!.GetValue<string>()).ToList();
        ids.ShouldContain("onprem/ok");
        ids.ShouldNotContain("eu/ok");

        // The context shows each key's own setting, the credential key's bound, and whether the two leave anything.
        using var response = await GetAsync(worker, "/v1/ume/context");
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        ((JsonArray)body["key"]!["allowed_residencies"]!).Select(r => r!.GetValue<string>()).ShouldBe(["OnPrem"]);
        var listed = ((JsonArray)body["assigned_keys"]!).ToDictionary(k => k!["id"]!.GetValue<Guid>(), k => k!);
        ((JsonArray)listed[both.Id]["allowed_residencies"]!).Select(r => r!.GetValue<string>()).ShouldBe(["OnPrem", "Eu"]);
        listed[both.Id]["usable"]!.GetValue<bool>().ShouldBeTrue();
        listed[euOnly.Id]["usable"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task The_context_lists_the_whole_organisation_only_for_a_credential_key_with_the_scope()
    {
        var integration = await CreateIntegrationAsync();
        var plain = await fixture.CreateKeyAsync(k => Assign(k, integration, IntegrationRole.Credential));
        var scoped = await fixture.CreateKeyAsync(k => { Assign(k, integration, IntegrationRole.Credential); k.IntegrationScopes = ["organisation.read"]; });
        Guid departmentId, teamId, inactiveTeamId;
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var department = new Department { Name = "Org " + Guid.NewGuid().ToString("N"), CostCenterCode = Guid.NewGuid().ToString("N")[..8], CreatedAt = DateTimeOffset.UtcNow, MemberGroups = ["IFO-medarbetare"] };
            var team = new Team { Name = "Without keys", Department = department, CreatedAt = DateTimeOffset.UtcNow };
            var inactive = new Team { Name = "Closed", Department = department, IsActive = false, CreatedAt = DateTimeOffset.UtcNow };
            db.AddRange(department, team, inactive);
            await db.SaveChangesAsync(Ct);
            (departmentId, teamId, inactiveTeamId) = (department.Id, team.Id, inactive.Id);
        }

        using var all = await GetAsync(scoped, "/v1/ume/context");
        var body = JsonNode.Parse(await all.Content.ReadAsStringAsync(Ct))!;
        var features = ((JsonArray)body["gateway"]!["features"]!).Select(f => f!.GetValue<string>()).ToList();
        features.ShouldContain("organisation");
        features.ShouldContain("member-groups");
        var departments = ((JsonArray)body["departments"]!).Select(d => d!["id"]!.GetValue<Guid>()).ToList();
        var listed = ((JsonArray)body["departments"]!).Single(d => d!["id"]!.GetValue<Guid>() == departmentId)!;
        ((JsonArray)listed["member_groups"]!).Select(g => g!.GetValue<string>()).ShouldBe(["IFO-medarbetare"]);
        departments.ShouldContain(departmentId);
        departments.ShouldContain(fixture.DepartmentId);
        departments.Count.ShouldBe(departments.Distinct().Count());
        var teams = ((JsonArray)body["teams"]!).ToDictionary(t => t!["id"]!.GetValue<Guid>(), t => t!);
        teams[teamId]["department_id"]!.GetValue<Guid>().ShouldBe(departmentId);
        teams[inactiveTeamId]["is_active"]!.GetValue<bool>().ShouldBeFalse();
        teams.ShouldContainKey(scoped.TeamId);

        // The published contract describes exactly these answers (docs/openapi/integrations.json).
        await fixture.CreateKeyAsync(k => Assign(k, integration, IntegrationRole.Assigned));
        using var withKeys = await GetAsync(scoped, "/v1/ume/context");
        IntegrationContract.Differences(JsonNode.Parse(await withKeys.Content.ReadAsStringAsync(Ct)), "UmeContext").ShouldBeEmpty();
        using var models = await GetAsync(scoped, "/v1/models");
        IntegrationContract.Differences(JsonNode.Parse(await models.Content.ReadAsStringAsync(Ct)), "ModelList").ShouldBeEmpty();

        using var own = await GetAsync(plain, "/v1/ume/context");
        var ownTeams = ((JsonArray)JsonNode.Parse(await own.Content.ReadAsStringAsync(Ct))!["teams"]!).Select(t => t!["id"]!.GetValue<Guid>()).ToList();
        ownTeams.ShouldContain(plain.TeamId);
        ownTeams.ShouldNotContain(teamId); // a team without a key of the integration
    }

    [Fact]
    public async Task Run_metadata_is_capped_and_cleaned_never_refused()
    {
        var key = await fixture.CreateKeyAsync();
        using var response = await fixture.SendAsync(key, headers: new Dictionary<string, string> { ["x-ume-run-id"] = new string('r', 150), ["x-ume-step"] = "  " });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var usage = await fixture.UsageAsync(response);
        usage.RunId.ShouldBe(new string('r', 100));
        usage.Step.ShouldBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_streamed_answer_ends_with_its_cost_when_asked(bool asked)
    {
        var key = await fixture.CreateKeyAsync();
        var headers = asked ? new Dictionary<string, string> { ["x-ume-stream-cost"] = "1" } : null;
        using var response = await fixture.SendAsync(key, stream: true, includeUsage: true, headers: headers);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var text = await response.Content.ReadAsStringAsync(Ct);
        var usage = await fixture.UsageAsync(response);
        if (!asked)
        {
            text.ShouldNotContain("ume-cost-usd");
            return;
        }

        var line = text.Split('\n').Single(l => l.StartsWith(": ume-cost-usd=", StringComparison.Ordinal));
        decimal.Parse(line[": ume-cost-usd=".Length..], System.Globalization.CultureInfo.InvariantCulture).ShouldBe(usage.CostUsd);
        usage.CostUsd.ShouldBe(0.000135m);
        text.IndexOf(": ume-cost-usd=", StringComparison.Ordinal).ShouldBeLessThan(text.IndexOf("[DONE]", StringComparison.Ordinal));
    }

    private const string OnBehalfOf = "x-ume-on-behalf-of";

    private static Dictionary<string, string> Acting(TestKey key) => new() { [OnBehalfOf] = key.Id.ToString() };

    private async Task<(int Status, string Code)> RefusalAsync(TestKey key, string onBehalfOf)
    {
        using var response = await fixture.SendAsync(key, headers: new Dictionary<string, string> { [OnBehalfOf] = onBehalfOf });
        response.Headers.Contains(OnBehalfOf).ShouldBeFalse();
        return ((int)response.StatusCode, await ErrorCodeAsync(response));
    }

    private static void Assign(VirtualKey key, Guid integration, IntegrationRole role)
    {
        key.IntegrationId = integration;
        key.IntegrationRole = role;
    }

    private async Task<Guid> CreateIntegrationAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var team = new Team { Name = Guid.NewGuid().ToString(), DepartmentId = fixture.DepartmentId, CreatedAt = DateTimeOffset.UtcNow };
        var integration = new Integration { Name = "Platform " + Guid.NewGuid().ToString("N"), OwnerTeam = team, CreatedAt = DateTimeOffset.UtcNow };
        db.Integrations.Add(integration);
        await db.SaveChangesAsync(Ct);
        return integration.Id;
    }

    private async Task UpdateIntegrationAsync(Guid id, Action<Integration> change)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var integration = await db.Integrations.SingleAsync(i => i.Id == id, Ct);
        change(integration);
        await db.SaveChangesAsync(Ct);
    }

    private async Task<HttpResponseMessage> GetAsync(TestKey key, string path, string? ifNoneMatch = null, Dictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        foreach (var (name, value) in headers ?? [])
        {
            request.Headers.Add(name, value);
        }

        if (ifNoneMatch is not null)
        {
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(ifNoneMatch));
        }

        return await fixture.Client.SendAsync(request, Ct);
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["error"]!["code"]!.GetValue<string>();
}
