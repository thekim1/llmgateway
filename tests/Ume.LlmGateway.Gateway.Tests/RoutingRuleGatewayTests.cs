using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Routing;
using Ume.LlmGateway.Infrastructure.Persistence;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>
/// Routing rules applied to live requests. Every rule is scoped to the key created by the test, so it cannot
/// affect other tests that share the database and catalogue.
/// </summary>
public sealed class RoutingRuleGatewayTests(GatewayFixture fixture) : IAsyncLifetime
{
    private readonly List<Guid> _rules = [];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().RoutingRules.Where(r => _rules.Contains(r.Id)).ExecuteDeleteAsync();
    }

    private async Task<RoutingRule> AddRuleAsync(TestKey key, string name, string condition, string[] targets, Action<RoutingRule>? configure = null)
    {
        var rule = new RoutingRule
        {
            Name = name,
            Condition = condition,
            Scope = RoutingScope.VirtualKey,
            ScopeId = key.Id,
            Targets = [.. targets.Select(t => new RoutingRuleTarget { Model = t })],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        configure?.Invoke(rule);
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        db.RoutingRules.Add(rule);
        await db.SaveChangesAsync(Ct);
        _rules.Add(rule.Id);
        return rule;
    }

    private static string Provider(HttpResponseMessage response) => response.Headers.GetValues("x-ume-provider").Single();

    private static string[] RuleHeader(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-ume-rule", out var values) ? values.Single().Split(',') : [];

    private static Dictionary<string, string> Tier(string value) => new() { ["x-tier"] = value };

    [Fact]
    public async Task Header_rule_reroutes_only_matching_requests_and_is_recorded()
    {
        var key = await fixture.CreateKeyAsync();
        var rule = await AddRuleAsync(key, "Premium", """headers["x-tier"] == "premium" """, ["onprem/ok"]);

        using var plain = await fixture.SendAsync(key, "eu/ok");
        plain.StatusCode.ShouldBe(HttpStatusCode.OK);
        Provider(plain).ShouldBe("eu");
        RuleHeader(plain).ShouldBeEmpty();
        var plainUsage = await fixture.UsageAsync(plain);
        plainUsage.RoutingRuleId.ShouldBeNull();
        plainUsage.RoutingRuleName.ShouldBeNull();

        using var premium = await fixture.SendAsync(key, "eu/ok", headers: Tier("premium"));
        premium.StatusCode.ShouldBe(HttpStatusCode.OK);
        Provider(premium).ShouldBe("onprem");
        RuleHeader(premium).ShouldBe([rule.Id.ToString("D")]);
        var usage = await fixture.UsageAsync(premium);
        usage.RoutingRuleId.ShouldBe(rule.Id);
        usage.RoutingRuleName.ShouldBe("Premium");
        usage.RequestedModel.ShouldBe("eu/ok"); // reports still show what the client asked for

        using var basic = await fixture.SendAsync(key, "eu/ok", headers: Tier("basic"));
        Provider(basic).ShouldBe("eu");
    }

    [Fact]
    public async Task Rules_of_other_keys_do_not_apply()
    {
        var owner = await fixture.CreateKeyAsync();
        var other = await fixture.CreateKeyAsync();
        await AddRuleAsync(owner, "Only owner", "", ["onprem/ok"]);
        using var response = await fixture.SendAsync(other, "eu/ok");
        Provider(response).ShouldBe("eu");
        RuleHeader(response).ShouldBeEmpty();
    }

    [Fact]
    public async Task Team_scoped_rule_applies_to_the_teams_keys()
    {
        var key = await fixture.CreateKeyAsync();
        await AddRuleAsync(key, "Team rule", "", ["external/ok"], r => { r.Scope = RoutingScope.Team; r.ScopeId = key.TeamId; });
        using var response = await fixture.SendAsync(key, "eu/ok");
        Provider(response).ShouldBe("external");
    }

    [Fact]
    public async Task A_rule_scoped_to_a_key_keeps_applying_after_the_key_is_rotated()
    {
        var old = await fixture.CreateKeyAsync();
        var replacement = await fixture.CreateKeyAsync();
        await AddRuleAsync(old, "Follows rotation", "", ["onprem/ok"]);

        using var before = await fixture.SendAsync(replacement, "eu/ok");
        Provider(before).ShouldBe("eu"); // not yet the old key's successor

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            (await db.VirtualKeys.SingleAsync(k => k.Id == old.Id, Ct)).RotatedToKeyId = replacement.Id;
            await db.SaveChangesAsync(Ct);
        }

        using var after = await fixture.SendAsync(replacement, "eu/ok");
        Provider(after).ShouldBe("onprem");
    }

    [Fact]
    public async Task Key_rule_wins_over_team_rule_regardless_of_priority()
    {
        var key = await fixture.CreateKeyAsync();
        await AddRuleAsync(key, "Team", "", ["external/ok"], r => { r.Scope = RoutingScope.Team; r.ScopeId = key.TeamId; r.Priority = -50; });
        await AddRuleAsync(key, "Key", "", ["onprem/ok"], r => r.Priority = 50);
        using var response = await fixture.SendAsync(key, "eu/ok");
        Provider(response).ShouldBe("onprem");
    }

    [Fact]
    public async Task Budget_rule_moves_traffic_when_the_budget_is_nearly_used()
    {
        var key = await fixture.CreateKeyAsync();
        await fixture.AddBudgetAsync(BudgetScope.VirtualKey, key.Id, 100m);
        await AddRuleAsync(key, "Budget guard", "budget_used > 90", ["onprem/ok"]);

        using var fresh = await fixture.SendAsync(key, "eu/ok");
        Provider(fresh).ShouldBe("eu"); // 0 % used

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            db.UsageRecords.Add(new UsageRecord
            {
                RequestId = "budget-" + Guid.NewGuid().ToString("N"), RequestedModel = "x", Timestamp = DateTimeOffset.UtcNow,
                VirtualKeyId = key.Id, TeamId = key.TeamId, DepartmentId = fixture.DepartmentId, CostSek = 95m,
            });
            await db.SaveChangesAsync(Ct);
        }

        // The counter already exists in the shared ledger from the first request, so add the spend there too.
        var ledger = fixture.Services.GetRequiredService<Ume.LlmGateway.Infrastructure.Stores.ISpendLedger>();
        var budgetKey = Ume.LlmGateway.Infrastructure.Stores.SpendCounter.KeyFor(BudgetScope.VirtualKey, key.Id, BudgetPeriod.Monthly,
            Ume.LlmGateway.Domain.Services.BudgetPeriods.GetWindow(BudgetPeriod.Monthly, DateTimeOffset.UtcNow).Start);
        await ledger.AddAsync([new Ume.LlmGateway.Infrastructure.Stores.SpendCounter(budgetKey, 100_000_000, TimeSpan.FromDays(40))], 95_000_000, Ct);

        using var nearly = await fixture.SendAsync(key, "eu/ok");
        nearly.StatusCode.ShouldBe(HttpStatusCode.OK);
        Provider(nearly).ShouldBe("onprem");
        RuleHeader(nearly).Length.ShouldBe(1);
    }

    [Fact]
    public async Task Weighted_targets_split_traffic()
    {
        var key = await fixture.CreateKeyAsync();
        await AddRuleAsync(key, "A/B", "", ["eu/ok", "onprem/ok"]);
        var providers = new List<string>();
        for (var i = 0; i < 40; i++)
        {
            using var response = await fixture.SendAsync(key, "external/ok");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            providers.Add(Provider(response));
        }

        providers.Distinct().Order().ShouldBe(["eu", "onprem"]); // both used; neither is the requested external one
        providers.Count(p => p == "eu").ShouldBeInRange(5, 35);
    }

    [Fact]
    public async Task Rule_fallbacks_are_used_when_the_target_fails()
    {
        var key = await fixture.CreateKeyAsync();
        await AddRuleAsync(key, "Failing target", "", ["eu/fail503"], r => r.Fallbacks = ["onprem/ok"]);
        using var response = await fixture.SendAsync(key, "external/ok");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Provider(response).ShouldBe("onprem");
        response.Headers.GetValues("x-ume-fallbacks").Single().ShouldBe("1");
    }

    [Fact]
    public async Task Rule_fallback_model_is_tried_before_an_unhealthy_primary_provider_is_retried()
    {
        var key = await fixture.CreateKeyAsync();
        await AddRuleAsync(key, "Alias then fallback", "", [await fixture.CreateRouteAsync("eu/fail503", "external/fail429")], r => r.Fallbacks = ["onprem/ok"]);
        using var response = await fixture.SendAsync(key, "external/ok");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Provider(response).ShouldBe("onprem");
        response.Headers.GetValues("x-ume-fallbacks").Single().ShouldBe("2");
    }

    [Fact]
    public async Task Chained_rules_normalise_a_legacy_name_then_route()
    {
        var alias = await fixture.CreateRouteAsync("eu/ok");
        var legacy = "legacy-" + Guid.NewGuid().ToString("N");
        var key = await fixture.CreateKeyAsync();
        var first = await AddRuleAsync(key, "Normalise", $"model == '{legacy}'", [alias], r => { r.Chain = true; r.Priority = 0; });
        var second = await AddRuleAsync(key, "Route", $"model == '{alias}'", ["external/ok"], r => r.Priority = 1);

        using var response = await fixture.SendAsync(key, legacy);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Provider(response).ShouldBe("external");
        RuleHeader(response).ShouldBe([first.Id.ToString("D"), second.Id.ToString("D")]);
        var usage = await fixture.UsageAsync(response);
        usage.RoutingRuleId.ShouldBe(second.Id); // the last rule decided
        usage.RequestedModel.ShouldBe(legacy);
    }

    [Fact]
    public async Task Legacy_name_is_checked_against_the_model_it_stands_for()
    {
        var alias = await fixture.CreateRouteAsync("eu/ok");
        var legacy = "legacy-" + Guid.NewGuid().ToString("N");

        var allowed = await fixture.CreateKeyAsync(k => k.AllowedModels = [alias]);
        await AddRuleAsync(allowed, "Alias", $"model == '{legacy}'", [alias]);
        using var ok = await fixture.SendAsync(allowed, legacy);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);

        var restricted = await fixture.CreateKeyAsync(k => k.AllowedModels = ["some/other-model"]);
        await AddRuleAsync(restricted, "Alias", $"model == '{legacy}'", [alias]);
        using var denied = await fixture.SendAsync(restricted, legacy);
        await AssertErrorAsync(denied, 403, "model_not_allowed");
    }

    [Fact]
    public async Task Unknown_model_without_a_matching_rule_is_still_not_found()
    {
        var key = await fixture.CreateKeyAsync();
        await AddRuleAsync(key, "Never", "false", ["eu/ok"]);
        using var response = await fixture.SendAsync(key, "no/such-model");
        await AssertErrorAsync(response, 404, "model_not_found");
    }

    [Fact]
    public async Task A_rule_cannot_send_a_request_to_a_provider_the_key_may_not_use()
    {
        var key = await fixture.CreateKeyAsync(k => k.AllowedProviders = ["eu"]);
        var only = await AddRuleAsync(key, "Escape", "", ["external/ok"]);
        using var blocked = await fixture.SendAsync(key, "eu/ok");
        await AssertErrorAsync(blocked, 503, "no_eligible_provider");

        // With an explicit allowed fallback the request is served, never by the forbidden provider.
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            (await db.RoutingRules.SingleAsync(r => r.Id == only.Id, Ct)).Fallbacks = ["eu/ok"];
            await db.SaveChangesAsync(Ct);
        }

        using var served = await fixture.SendAsync(key, "eu/ok");
        served.StatusCode.ShouldBe(HttpStatusCode.OK);
        Provider(served).ShouldBe("eu");
    }

    [Fact]
    public async Task A_rule_cannot_send_pii_off_premises()
    {
        var key = await fixture.CreateKeyAsync(k => k.PiiPolicy = PiiPolicy.RerouteToOnPrem);
        await AddRuleAsync(key, "To external", "", ["external/ok"], r => r.Fallbacks = ["onprem/ok"]);
        using var response = await fixture.SendAsync(key, "eu/ok", prompt: "Synthetic identifier 19121212-1212");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Provider(response).ShouldBe("onprem");
    }

    [Fact]
    public async Task Rules_can_react_to_personal_data_even_when_the_key_policy_is_off()
    {
        var key = await fixture.CreateKeyAsync(k => k.PiiPolicy = PiiPolicy.Off);
        await AddRuleAsync(key, "PII to on-prem", "pii_detected", ["onprem/ok"]);

        using var clean = await fixture.SendAsync(key, "eu/ok", prompt: "Hello");
        Provider(clean).ShouldBe("eu");
        using var pii = await fixture.SendAsync(key, "eu/ok", prompt: "Synthetic identifier 19121212-1212");
        Provider(pii).ShouldBe("onprem");
        pii.Headers.Contains("x-ume-pii").ShouldBeFalse(); // a signal only: the key's policy took no action
    }

    [Fact]
    public async Task Credentials_and_prompt_content_are_not_visible_to_conditions()
    {
        var key = await fixture.CreateKeyAsync();
        await AddRuleAsync(key, "Spy on secrets", "'authorization' in headers || 'x-api-key' in headers || 'cookie' in headers", ["onprem/ok"]);
        await AddRuleAsync(key, "Spy on prompt", "'prompt' in params || 'messages' in params || 'input' in params", ["onprem/ok"], r => r.Priority = 1);
        using var response = await fixture.SendRawAsync(key, "/v1/chat/completions",
            """{"model":"eu/ok","messages":[{"role":"user","content":"hi"}],"prompt":"secret","max_tokens":20}""",
            headers: new Dictionary<string, string> { ["Cookie"] = "a=b" });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Provider(response).ShouldBe("eu");
        RuleHeader(response).ShouldBeEmpty();
    }

    [Fact]
    public async Task Conditions_can_use_request_parameters_endpoint_and_team()
    {
        var key = await fixture.CreateKeyAsync();
        await AddRuleAsync(key, "Params", """params["max_tokens"] == 20 && params["stream"] == false && endpoint == "chat_completions" && prompt_tokens > 0 && key_name == "Test key" """, ["onprem/ok"]);
        using var response = await fixture.SendAsync(key, "eu/ok");
        Provider(response).ShouldBe("onprem");
    }

    [Fact]
    public async Task Rule_pointing_at_missing_or_wrong_kind_models_is_rejected_clearly()
    {
        var key = await fixture.CreateKeyAsync();
        await AddRuleAsync(key, "Nowhere", "headers[\"x-t\"] == \"missing\"", ["no/such-model"]);
        await AddRuleAsync(key, "Embedding", "headers[\"x-t\"] == \"embedding\"", ["eu/embedding"], r => r.Priority = 1);

        foreach (var tier in new[] { "missing", "embedding" })
        {
            using var response = await fixture.SendAsync(key, "eu/ok", headers: new Dictionary<string, string> { ["x-t"] = tier });
            await AssertErrorAsync(response, 503, "no_eligible_provider");
            (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("Routingregeln");
        }
    }

    [Fact]
    public async Task Disabled_rules_do_nothing()
    {
        var key = await fixture.CreateKeyAsync();
        await AddRuleAsync(key, "Off", "", ["onprem/ok"], r => r.IsEnabled = false);
        using var response = await fixture.SendAsync(key, "eu/ok");
        Provider(response).ShouldBe("eu");
    }

    [Fact]
    public async Task An_invalid_rule_is_ignored_and_does_not_break_requests()
    {
        var key = await fixture.CreateKeyAsync();
        await AddRuleAsync(key, "Broken", "budget_usd > 1 &&", ["onprem/ok"]);
        using var response = await fixture.SendAsync(key, "eu/ok");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Provider(response).ShouldBe("eu");
    }

    [Fact]
    public async Task Routing_also_applies_to_the_other_endpoints()
    {
        var key = await fixture.CreateKeyAsync();
        await AddRuleAsync(key, "Embeddings", """endpoint == "embeddings" """, ["onprem/embedding"]);
        using var response = await fixture.SendRawAsync(key, "/v1/embeddings", new JsonObject { ["model"] = "eu/embedding", ["input"] = "text" }.ToJsonString());
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Provider(response).ShouldBe("onprem");
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, int status, string code)
    {
        ((int)response.StatusCode).ShouldBe(status);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct));
        json!["error"]!["code"]!.GetValue<string>().ShouldBe(code);
    }
}
