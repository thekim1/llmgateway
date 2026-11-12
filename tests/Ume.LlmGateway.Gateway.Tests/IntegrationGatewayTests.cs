using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Requests;
using Ume.LlmGateway.Infrastructure.Persistence;

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

    private async Task<HttpResponseMessage> GetAsync(TestKey key, string path, string? ifNoneMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        if (ifNoneMatch is not null)
        {
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(ifNoneMatch));
        }

        return await fixture.Client.SendAsync(request, Ct);
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["error"]!["code"]!.GetValue<string>();
}
