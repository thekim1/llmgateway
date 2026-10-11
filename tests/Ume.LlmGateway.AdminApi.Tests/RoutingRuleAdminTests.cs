using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.Infrastructure.Persistence;

namespace Ume.LlmGateway.AdminApi.Tests;

/// <summary>
/// The routing rule admin API. The database is shared with other test classes, so rules are scoped to a team created
/// by the test or use header names unique to the test, and assertions only look at the test's own rules.
/// </summary>
public sealed class RoutingRuleAdminTests(AdminFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unique() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>Creates a provider and a model through the API and returns the model's name and id.</summary>
    private static async Task<(string Name, Guid Id)> ModelAsync(HttpClient client, string? prefix = null)
    {
        var provider = "p-" + Unique();
        using var p = await client.PostAsJsonAsync("/api/providers", new
        {
            name = provider, baseUrl = "https://provider.invalid/v1", type = "OpenAICompatible", authMode = "Bearer",
            residency = "Eu", capabilities = new[] { "ChatCompletions" }, timeoutSeconds = 10, isEnabled = true,
        }, Ct);
        var providerId = (await AdminFixture.ReadAsync(p, HttpStatusCode.Created))["id"]!.GetValue<Guid>();
        var name = (prefix ?? "m") + "-" + Unique();
        using var m = await client.PostAsJsonAsync("/api/models", new { providerId, name, upstreamModel = "up", kind = "Chat", parameterProfile = "Standard", isEnabled = true }, Ct);
        return (name, (await AdminFixture.ReadAsync(m, HttpStatusCode.Created))["id"]!.GetValue<Guid>());
    }

    private static object Body(string name, string[] targets, string condition = "", string scope = "Global", Guid? scopeId = null, int priority = 0,
        bool chain = false, bool enabled = true, string[]? fallbacks = null, Guid[]? scopeIds = null) => new
        {
            name, scope, scopeIds = scopeIds ?? (scopeId is { } id ? new[] { id } : Array.Empty<Guid>()), condition, priority, chain, isEnabled = enabled,
            targets = targets.Select(t => new { model = t, weight = 1 }).ToArray(), fallbacks = fallbacks ?? [],
        };

    private static async Task<JsonNode> CreateAsync(HttpClient client, object body, HttpStatusCode expected = HttpStatusCode.Created)
    {
        using var response = await client.PostAsJsonAsync("/api/routing-rules", body, Ct);
        return await AdminFixture.ReadAsync(response, expected);
    }

    private static async Task<JsonNode> GetAsync(HttpClient client, string id)
    {
        using var response = await client.GetAsync($"/api/routing-rules/{id}", Ct);
        return await AdminFixture.ReadAsync(response);
    }

    private static string Id(JsonNode rule) => rule["id"]!.GetValue<Guid>().ToString();

    [Theory]
    [InlineData(null, 401)]
    [InlineData("viewer", 403)]
    [InlineData("department-admin", 403)]
    public async Task Rules_require_the_admin_role(string? role, int status)
    {
        using var client = await fixture.ClientAsync(role);
        using var response = await client.GetAsync("/api/routing-rules", Ct);
        ((int)response.StatusCode).ShouldBe(status);
    }

    [Fact]
    public async Task Rule_can_be_created_read_updated_and_deleted_with_audit()
    {
        using var client = await fixture.ClientAsync();
        var model = await ModelAsync(client);
        var other = await ModelAsync(client);
        var header = "x-" + Unique();

        var created = await CreateAsync(client, new
        {
            name = "Premium " + Unique(), description = "Premium traffic", scope = "Global", priority = 5, chain = false, isEnabled = true,
            condition = $"headers['{header}'] == 'premium'",
            targets = new[] { new { model = model.Name, weight = 70 }, new { model = other.Name, weight = 30 } },
            fallbacks = new[] { other.Name, other.Name.ToUpperInvariant() },
        });
        var id = Id(created);
        created["scope"]!.GetValue<string>().ShouldBe("Global");
        created["isOrphaned"]!.GetValue<bool>().ShouldBeFalse();
        created["targets"]!.AsArray().Select(t => (t!["model"]!.GetValue<string>(), t["weight"]!.GetValue<int>())).ShouldBe([(model.Name, 70), (other.Name, 30)]);
        created["fallbacks"]!.AsArray().Count.ShouldBe(1); // case-insensitive duplicate removed
        created["validationErrors"]!.AsArray().ShouldBeEmpty();

        (await GetAsync(client, id))["description"]!.GetValue<string>().ShouldBe("Premium traffic");
        using (var list = await client.GetAsync("/api/routing-rules?scope=Global", Ct))
        {
            (await AdminFixture.ReadAsync(list)).AsArray().Select(r => Id(r!)).ShouldContain(id);
        }

        using var update = await client.PutAsJsonAsync($"/api/routing-rules/{id}", Body(created["name"]!.GetValue<string>(), [other.Name], $"headers['{header}'] == 'gold'", priority: 9, enabled: false), Ct);
        var updated = await AdminFixture.ReadAsync(update);
        updated["priority"]!.GetValue<int>().ShouldBe(9);
        updated["isEnabled"]!.GetValue<bool>().ShouldBeFalse();
        updated["targets"]!.AsArray().Count.ShouldBe(1);

        using var delete = await client.DeleteAsync($"/api/routing-rules/{id}", Ct);
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var gone = await client.GetAsync($"/api/routing-rules/{id}", Ct);
        gone.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await using var scope = fixture.Services.CreateAsyncScope();
        var actions = await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().AuditLog
            .Where(a => a.EntityType == "RoutingRule" && a.EntityId == id).OrderBy(a => a.Id).Select(a => a.Action).ToListAsync(Ct);
        actions.ShouldBe(["create", "update", "delete"]);
    }

    [Fact]
    public async Task Invalid_rules_are_rejected_with_field_errors()
    {
        using var client = await fixture.ClientAsync();
        var model = await ModelAsync(client);
        var org = await fixture.OrganisationAsync(client);

        async Task<JsonNode> Rejected(object body, string field)
        {
            using var response = await client.PostAsJsonAsync("/api/routing-rules", body, Ct);
            var json = await AdminFixture.ReadAsync(response, HttpStatusCode.BadRequest);
            json["errors"]![field].ShouldNotBeNull($"expected an error for '{field}': {json}");
            return json;
        }

        var syntax = await Rejected(Body("Bad " + Unique(), [model.Name], "budget_used >"), "condition");
        syntax["errors"]!["condition"]![0]!.GetValue<string>().ShouldContain("tecken");
        (await Rejected(Body("Bad " + Unique(), [model.Name], "budget_usd > 1"), "condition"))["errors"]!["condition"]![0]!.GetValue<string>().ShouldContain("Unknown variable");
        await Rejected(Body("Bad " + Unique(), [model.Name], "budget_used"), "condition"); // not true/false
        await Rejected(Body("Bad " + Unique(), ["no/such-model"]), "targets");
        await Rejected(Body("Bad " + Unique(), [model.Name], fallbacks: ["no/such-model"]), "fallbacks");
        await Rejected(Body("Bad " + Unique(), [model.Name], scope: "Global", scopeId: Guid.NewGuid()), "scopeIds");
        await Rejected(Body("Bad " + Unique(), [model.Name], scope: "Team"), "scopeIds");
        await Rejected(Body("Bad " + Unique(), [model.Name], scope: "Team", scopeId: Guid.NewGuid()), "scopeIds"); // team does not exist
        await Rejected(Body("Bad " + Unique(), [model.Name], scope: "Team", scopeIds: [org.Team, Guid.NewGuid()]), "scopeIds"); // one of them does not exist
        await Rejected(new { name = "Bad", scope = "Global", targets = new[] { new { model = model.Name, weight = 0 } } }, "Targets[0].weight");
        await Rejected(new { name = "", scope = "Global", targets = new[] { new { model = model.Name, weight = 1 } } }, "name");
        await Rejected(new { name = "Bad", scope = "Global", targets = Array.Empty<object>() }, "targets");

        // A chained rule may point at a name another rule handles; a plain rule may not.
        var chained = await CreateAsync(client, Body("Chain " + Unique(), ["legacy-" + Unique()], "model == 'x'", scope: "Team", scopeId: org.Team, chain: true));
        chained["chain"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task Names_are_unique_per_scope_but_may_repeat_across_scopes()
    {
        using var client = await fixture.ClientAsync();
        var model = await ModelAsync(client);
        var org = await fixture.OrganisationAsync(client);
        var name = "Same " + Unique();
        await CreateAsync(client, Body(name, [model.Name], scope: "Team", scopeId: org.Team));
        using var duplicate = await client.PostAsJsonAsync("/api/routing-rules", Body(name.ToUpperInvariant(), [model.Name], scope: "Team", scopeId: org.Team), Ct);
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await CreateAsync(client, Body(name, [model.Name], scope: "Department", scopeId: org.Department));

        // Shared rules: the same name may not apply twice to one owner, but may to different owners.
        var other = await fixture.OrganisationAsync(client);
        using var overlapping = await client.PostAsJsonAsync("/api/routing-rules", Body(name, [model.Name], scope: "Team", scopeIds: [other.Team, org.Team]), Ct);
        overlapping.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await CreateAsync(client, Body(name, [model.Name], scope: "Team", scopeIds: [other.Team]));
    }

    [Fact]
    public async Task Condition_can_be_validated_while_typing()
    {
        using var client = await fixture.ClientAsync();
        using var ok = await client.PostAsJsonAsync("/api/routing-rules/validate", new { condition = "budget_used > 85 && headers['x'] == 'y'" }, Ct);
        var good = await AdminFixture.ReadAsync(ok);
        good["valid"]!.GetValue<bool>().ShouldBeTrue();
        good["variables"]!.AsArray().Select(v => v!.GetValue<string>()).ShouldBe(["budget_used", "headers"]);
        good["available"]!.AsArray().Count.ShouldBeGreaterThan(10);

        const string source = "budget_used > 85 && budget_usd > 1";
        using var bad = await client.PostAsJsonAsync("/api/routing-rules/validate", new { condition = source }, Ct);
        var json = await AdminFixture.ReadAsync(bad);
        json["valid"]!.GetValue<bool>().ShouldBeFalse();
        var error = json["errors"]![0]!;
        source.Substring(error["position"]!.GetValue<int>(), error["length"]!.GetValue<int>()).ShouldBe("budget_usd");

        using var empty = await client.PostAsJsonAsync("/api/routing-rules/validate", new { condition = "" }, Ct);
        (await AdminFixture.ReadAsync(empty))["valid"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task Rules_can_be_reordered_within_a_scope()
    {
        using var client = await fixture.ClientAsync();
        var model = await ModelAsync(client);
        var org = await fixture.OrganisationAsync(client);
        var a = Id(await CreateAsync(client, Body("A", [model.Name], scope: "Team", scopeId: org.Team, priority: 0)));
        var b = Id(await CreateAsync(client, Body("B", [model.Name], scope: "Team", scopeId: org.Team, priority: 1)));
        var c = Id(await CreateAsync(client, Body("C", [model.Name], scope: "Team", scopeId: org.Team, priority: 2)));

        using var reorder = await client.PostAsJsonAsync("/api/routing-rules/reorder", new { scope = "Team", ruleIds = new[] { c, a, b } }, Ct);
        var result = (await AdminFixture.ReadAsync(reorder)).AsArray();
        result.Select(r => (r!["name"]!.GetValue<string>(), r["priority"]!.GetValue<int>())).ShouldBe([("C", 0), ("A", 10), ("B", 20)]);

        using var list = await client.GetAsync($"/api/routing-rules?scope=Team&scopeId={org.Team}", Ct);
        (await AdminFixture.ReadAsync(list)).AsArray().Select(r => r!["name"]!.GetValue<string>()).ShouldBe(["C", "A", "B"]);

        using var wrongScope = await client.PostAsJsonAsync("/api/routing-rules/reorder", new { scope = "Global", ruleIds = new[] { a } }, Ct);
        wrongScope.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var duplicated = await client.PostAsJsonAsync("/api/routing-rules/reorder", new { scope = "Team", ruleIds = new[] { a, a } }, Ct);
        duplicated.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Dry_run_shows_which_rule_applies_and_why()
    {
        using var client = await fixture.ClientAsync();
        var premium = await ModelAsync(client, "premium");
        var cheap = await ModelAsync(client, "cheap");
        var org = await fixture.OrganisationAsync(client);
        var vip = Id(await CreateAsync(client, Body("VIP", [premium.Name], "headers['x-tier'] == 'premium'", scope: "Team", scopeId: org.Team, priority: 0, fallbacks: [cheap.Name])));
        var budget = Id(await CreateAsync(client, Body("Budget", [cheap.Name], "budget_used > 90", scope: "Team", scopeId: org.Team, priority: 1)));
        var broken = Id(await CreateAsync(client, Body("Disabled broken", [cheap.Name], enabled: false, scope: "Team", scopeId: org.Team, priority: 2)));
        broken.ShouldNotBeNull();

        async Task<JsonNode> Test(object body)
        {
            using var response = await client.PostAsJsonAsync("/api/routing-rules/test", body, Ct);
            return await AdminFixture.ReadAsync(response);
        }

        var hit = await Test(new { model = "ume/chat", teamId = org.Team, headers = new Dictionary<string, string> { ["X-Tier"] = "premium" }, seed = 1 });
        hit["matched"]!.GetValue<bool>().ShouldBeTrue();
        hit["primaryModel"]!.GetValue<string>().ShouldBe(premium.Name);
        hit["models"]!.AsArray().Select(m => m!["name"]!.GetValue<string>()).ShouldBe([premium.Name, cheap.Name]);
        hit["models"]![0]!["exists"]!.GetValue<bool>().ShouldBeTrue();
        hit["applied"]![0]!["ruleId"]!.GetValue<string>().ShouldBe(vip);
        var vipEval = hit["evaluation"]!.AsArray().Single(e => e!["name"]!.GetValue<string>() == "VIP")!;
        vipEval["outcome"]!.GetValue<string>().ShouldBe("Matched");
        vipEval["trace"]![0]!["result"]!.GetValue<bool>().ShouldBeTrue();

        // Only the usage values the caller supplies are known: without budgetUsed the budget rule cannot match.
        var none = await Test(new { model = "ume/chat", teamId = org.Team });
        none["matched"]!.GetValue<bool>().ShouldBeFalse();
        var own = none["evaluation"]!.AsArray().Where(e => e!["name"]!.GetValue<string>() is "VIP" or "Budget").ToList(); // other tests' global rules share the database
        own.Select(e => e!["outcome"]!.GetValue<string>()).ShouldBe(["NotMatched", "NotMatched"]);
        own[0]!["trace"]![0]!["result"].ShouldBeNull(); // header missing => could not be evaluated

        var guard = await Test(new { model = "ume/chat", teamId = org.Team, budgetUsed = 95 });
        guard["applied"]![0]!["ruleId"]!.GetValue<string>().ShouldBe(budget);
        guard["evaluation"]!.AsArray().Single(e => e!["name"]!.GetValue<string>() == "Budget")!["trace"]![0]!["leftValue"]!.GetValue<string>().ShouldBe("95");

        // Another team is outside these rules' scope entirely.
        (await Test(new { model = "ume/chat", teamId = Guid.NewGuid(), budgetUsed = 95 }))["evaluation"]!.AsArray().Where(e => e!["name"]!.GetValue<string>() is "VIP" or "Budget").ShouldBeEmpty();

        using var invalid = await client.PostAsJsonAsync("/api/routing-rules/test", new { model = "m", budgetUsed = 150 }, Ct);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Dry_run_can_derive_the_team_from_a_key()
    {
        using var client = await fixture.ClientAsync();
        var model = await ModelAsync(client);
        var org = await fixture.OrganisationAsync(client);
        using var key = await client.PostAsJsonAsync("/api/keys", new
        {
            teamId = org.Team, name = "Dry run key", allowedModels = Array.Empty<string>(), allowedResidencies = Array.Empty<string>(),
            allowedProviders = Array.Empty<string>(), piiPolicy = "Off",
        }, Ct);
        var keyId = (await AdminFixture.ReadAsync(key, HttpStatusCode.Created))["key"]!["id"]!.GetValue<Guid>();
        await CreateAsync(client, Body("Key rule", [model.Name], "key_name == 'Dry run key' && team_name == 'Team'", scope: "VirtualKey", scopeId: keyId));

        using var response = await client.PostAsJsonAsync("/api/routing-rules/test", new { model = "any", keyId }, Ct);
        (await AdminFixture.ReadAsync(response))["primaryModel"]!.GetValue<string>().ShouldBe(model.Name);
    }

    [Fact]
    public async Task Usage_records_show_which_routing_rule_routed_the_request()
    {
        using var admin = await fixture.ClientAsync();
        var org = await fixture.OrganisationAsync(admin);
        using var keyResponse = await admin.PostAsJsonAsync("/api/keys", new
        {
            teamId = org.Team, name = "Usage key", allowedModels = Array.Empty<string>(), allowedResidencies = Array.Empty<string>(),
            allowedProviders = Array.Empty<string>(), piiPolicy = "Off",
        }, Ct);
        var key = (await AdminFixture.ReadAsync(keyResponse, HttpStatusCode.Created))["key"]!["id"]!.GetValue<Guid>();
        var ruleId = Guid.NewGuid();
        string Request(string suffix) => "req_" + suffix + Unique();
        var routed = Request("routed");
        var plain = Request("plain");
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            foreach (var (id, rule) in new[] { (routed, "Premium via header"), (plain, (string?)null) })
            {
                db.UsageRecords.Add(new Ume.LlmGateway.Domain.Entities.UsageRecord
                {
                    RequestId = id, Timestamp = DateTimeOffset.UtcNow, VirtualKeyId = key, TeamId = org.Team, DepartmentId = org.Department,
                    RequestedModel = "ume/chat", Outcome = Ume.LlmGateway.Domain.RequestOutcome.Success, StatusCode = 200,
                    RoutingRuleId = rule is null ? null : ruleId, RoutingRuleName = rule,
                });
            }

            await db.SaveChangesAsync(Ct);
        }

        using var a = await admin.GetAsync($"/api/usage/requests/{routed}", Ct);
        var withRule = await AdminFixture.ReadAsync(a);
        withRule["routingRuleName"]!.GetValue<string>().ShouldBe("Premium via header");
        withRule["routingRuleId"]!.GetValue<Guid>().ShouldBe(ruleId);
        using var b = await admin.GetAsync($"/api/usage/requests/{plain}", Ct);
        var without = await AdminFixture.ReadAsync(b);
        without["routingRuleName"].ShouldBeNull();
        without["routingRuleId"].ShouldBeNull();
    }

    // ---- deleting a team or department that has rules -------------------------------------------------------

    private static async Task<JsonNode> ConflictAsync(HttpClient client, string url)
    {
        using var response = await client.DeleteAsync(url, Ct);
        return await AdminFixture.ReadAsync(response, HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Deleting_a_team_with_rules_asks_what_to_do_with_them()
    {
        using var client = await fixture.ClientAsync();
        var model = await ModelAsync(client);
        var org = await fixture.OrganisationAsync(client);
        var on = Id(await CreateAsync(client, Body("On", [model.Name], scope: "Team", scopeId: org.Team)));
        var off = Id(await CreateAsync(client, Body("Off", [model.Name], scope: "Team", scopeId: org.Team, enabled: false)));

        var conflict = await ConflictAsync(client, $"/api/teams/{org.Team}");
        conflict["code"]!.GetValue<string>().ShouldBe("routing_rules_scoped");
        conflict["choices"]!.AsArray().Select(c => c!.GetValue<string>()).ShouldBe(["delete", "deactivate"]);
        conflict["rules"]!.AsArray().Select(r => r!["name"]!.GetValue<string>()).Order().ShouldBe(["Off", "On"]);
        conflict["detail"]!.GetValue<string>().ShouldContain("2 routingregel");

        // Nothing changed while the question is open.
        using var teams = await client.GetAsync($"/api/teams?departmentId={org.Department}", Ct);
        (await AdminFixture.ReadAsync(teams)).AsArray().Count.ShouldBe(1);
        (await GetAsync(client, on))["isEnabled"]!.GetValue<bool>().ShouldBeTrue();

        using var bogus = await client.DeleteAsync($"/api/teams/{org.Team}?routingRules=maybe", Ct);
        bogus.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        off.ShouldNotBeNull();
    }

    [Fact]
    public async Task Deleting_a_team_can_delete_its_rules()
    {
        using var client = await fixture.ClientAsync();
        var model = await ModelAsync(client);
        var org = await fixture.OrganisationAsync(client);
        var rule = Id(await CreateAsync(client, Body("Doomed", [model.Name], scope: "Team", scopeId: org.Team)));

        using var delete = await client.DeleteAsync($"/api/teams/{org.Team}?routingRules=delete", Ct);
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var gone = await client.GetAsync($"/api/routing-rules/{rule}", Ct);
        gone.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        (await db.Teams.AnyAsync(t => t.Id == org.Team, Ct)).ShouldBeFalse();
        var audit = await db.AuditLog.Where(a => a.EntityType == "RoutingRule" && a.EntityId == rule).OrderBy(a => a.Id).Select(a => a.Action).ToListAsync(Ct);
        audit.ShouldBe(["create", "delete"]);
    }

    [Fact]
    public async Task Deleting_a_team_can_deactivate_its_rules_until_a_new_team_is_assigned()
    {
        using var client = await fixture.ClientAsync();
        var model = await ModelAsync(client);
        var org = await fixture.OrganisationAsync(client);
        var other = await fixture.OrganisationAsync(client);
        var rule = await CreateAsync(client, Body("Keep me", [model.Name], "headers['x-a'] == '1'", scope: "Team", scopeId: org.Team, priority: 3, fallbacks: [model.Name]));
        var id = Id(rule);

        using var delete = await client.DeleteAsync($"/api/teams/{org.Team}?routingRules=deactivate", Ct);
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var parked = await GetAsync(client, id);
        parked["isEnabled"]!.GetValue<bool>().ShouldBeFalse();
        parked["isOrphaned"]!.GetValue<bool>().ShouldBeTrue();
        parked["owners"]!.AsArray().ShouldBeEmpty(); // its only owner is gone
        parked["scopeIds"]!.AsArray().ShouldBeEmpty();
        parked["condition"]!.GetValue<string>().ShouldBe("headers['x-a'] == '1'"); // everything else is kept
        parked["priority"]!.GetValue<int>().ShouldBe(3);
        parked["targets"]!.AsArray().Count.ShouldBe(1);

        using (var orphans = await client.GetAsync("/api/routing-rules?orphaned=true", Ct))
        {
            (await AdminFixture.ReadAsync(orphans)).AsArray().Select(r => Id(r!)).ShouldContain(id);
        }

        // It cannot be switched on while it has no owner...
        using var enable = await client.PutAsJsonAsync($"/api/routing-rules/{id}", Body("Keep me", [model.Name], "headers['x-a'] == '1'", scope: "Team", scopeId: org.Team, priority: 3, enabled: true), Ct);
        (await AdminFixture.ReadAsync(enable, HttpStatusCode.BadRequest))["errors"]!["scopeIds"].ShouldNotBeNull();
        using var toggle = await client.PutAsJsonAsync($"/api/routing-rules/{id}/enabled", new { isEnabled = true }, Ct);
        (await AdminFixture.ReadAsync(toggle, HttpStatusCode.BadRequest))["detail"]!.GetValue<string>().ShouldContain("kan inte aktiveras");

        // ...but can still be edited while it waits.
        using var edit = await client.PutAsJsonAsync($"/api/routing-rules/{id}", Body("Keep me", [model.Name], "headers['x-a'] == '2'", scope: "Team", priority: 3, enabled: false), Ct);
        (await AdminFixture.ReadAsync(edit))["condition"]!.GetValue<string>().ShouldBe("headers['x-a'] == '2'");

        // Assigning a new team activates it again.
        using var reassign = await client.PostAsJsonAsync($"/api/routing-rules/{id}/reassign", new { scope = "Team", scopeIds = new[] { other.Team } }, Ct);
        var back = await AdminFixture.ReadAsync(reassign);
        back["isEnabled"]!.GetValue<bool>().ShouldBeTrue();
        back["isOrphaned"]!.GetValue<bool>().ShouldBeFalse();
        back["owners"]![0]!["name"]!.GetValue<string>().ShouldBe("Team");
        back["scopeIds"]!.AsArray().Select(x => x!.GetValue<Guid>()).ShouldBe([other.Team]);

        await using var scope = fixture.Services.CreateAsyncScope();
        var actions = await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().AuditLog
            .Where(a => a.EntityType == "RoutingRule" && a.EntityId == id).OrderBy(a => a.Id).Select(a => a.Action).ToListAsync(Ct);
        actions.ShouldBe(["create", "deactivate", "update", "reassign"]);
    }

    [Fact]
    public async Task Reassign_can_leave_the_rule_disabled_and_validates_the_new_owner()
    {
        using var client = await fixture.ClientAsync();
        var model = await ModelAsync(client);
        var org = await fixture.OrganisationAsync(client);
        var other = await fixture.OrganisationAsync(client);
        var id = Id(await CreateAsync(client, Body("Move", [model.Name], scope: "Team", scopeId: org.Team)));

        using var missing = await client.PostAsJsonAsync($"/api/routing-rules/{id}/reassign", new { scope = "Team", scopeIds = new[] { Guid.NewGuid() } }, Ct);
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var noId = await client.PostAsJsonAsync($"/api/routing-rules/{id}/reassign", new { scope = "Department" }, Ct);
        noId.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var toDepartment = await client.PostAsJsonAsync($"/api/routing-rules/{id}/reassign", new { scope = "Department", scopeIds = new[] { other.Department }, enable = false }, Ct);
        var moved = await AdminFixture.ReadAsync(toDepartment);
        moved["scope"]!.GetValue<string>().ShouldBe("Department");
        moved["isEnabled"]!.GetValue<bool>().ShouldBeFalse();

        // A rule with the same name already lives in the target scope.
        await CreateAsync(client, Body("Clash", [model.Name], scope: "Team", scopeId: other.Team));
        var clash = Id(await CreateAsync(client, Body("Clash", [model.Name], scope: "Team", scopeId: org.Team)));
        using var conflict = await client.PostAsJsonAsync($"/api/routing-rules/{clash}/reassign", new { scope = "Team", scopeIds = new[] { other.Team } }, Ct);
        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Deleting_a_department_asks_the_same_question()
    {
        using var client = await fixture.ClientAsync();
        var model = await ModelAsync(client);
        var code = Unique();
        using var dept = await client.PostAsJsonAsync("/api/departments", new { name = "Old department " + code, costCenterCode = code }, Ct);
        var oldDept = (await AdminFixture.ReadAsync(dept, HttpStatusCode.Created))["id"]!.GetValue<Guid>();
        var code2 = Unique();
        using var dept2 = await client.PostAsJsonAsync("/api/departments", new { name = "New department " + code2, costCenterCode = code2 }, Ct);
        var newDept = (await AdminFixture.ReadAsync(dept2, HttpStatusCode.Created))["id"]!.GetValue<Guid>();
        var id = Id(await CreateAsync(client, Body("Dept rule", [model.Name], scope: "Department", scopeId: oldDept)));

        (await ConflictAsync(client, $"/api/departments/{oldDept}"))["code"]!.GetValue<string>().ShouldBe("routing_rules_scoped");
        using var delete = await client.DeleteAsync($"/api/departments/{oldDept}?routingRules=deactivate", Ct);
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await GetAsync(client, id))["isOrphaned"]!.GetValue<bool>().ShouldBeTrue();

        using var reassign = await client.PostAsJsonAsync($"/api/routing-rules/{id}/reassign", new { scope = "Department", scopeIds = new[] { newDept } }, Ct);
        var moved = await AdminFixture.ReadAsync(reassign);
        moved["isEnabled"]!.GetValue<bool>().ShouldBeTrue();
        moved["owners"]![0]!["name"]!.GetValue<string>().ShouldBe("New department " + code2);
    }

    [Fact]
    public async Task Deleting_a_team_without_rules_needs_no_choice()
    {
        using var client = await fixture.ClientAsync();
        var org = await fixture.OrganisationAsync(client);
        using var delete = await client.DeleteAsync($"/api/teams/{org.Team}", Ct);
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Rules_of_other_teams_do_not_block_deleting_a_team()
    {
        using var client = await fixture.ClientAsync();
        var model = await ModelAsync(client);
        var org = await fixture.OrganisationAsync(client);
        var other = await fixture.OrganisationAsync(client);
        var id = Id(await CreateAsync(client, Body("Other team", [model.Name], scope: "Team", scopeId: other.Team)));
        using var delete = await client.DeleteAsync($"/api/teams/{org.Team}", Ct);
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await GetAsync(client, id))["isEnabled"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task A_rule_shared_by_several_teams_applies_to_each_and_survives_losing_one()
    {
        using var client = await fixture.ClientAsync();
        var model = await ModelAsync(client);
        var org = await fixture.OrganisationAsync(client);
        var other = await fixture.OrganisationAsync(client);
        var header = "x-" + Unique();
        var shared = await CreateAsync(client, Body("Shared", [model.Name], $"headers['{header}'] == '1'", scope: "Team", scopeIds: [org.Team, other.Team, org.Team]));
        var id = Id(shared);
        shared["scopeIds"]!.AsArray().Count.ShouldBe(2); // duplicates dropped
        shared["owners"]!.AsArray().Select(o => o!["name"]!.GetValue<string>()).ShouldBe(["Team", "Team"]);
        var sole = Id(await CreateAsync(client, Body("Only mine", [model.Name], scope: "Team", scopeId: org.Team, enabled: false)));

        using (var filtered = await client.GetAsync($"/api/routing-rules?scope=Team&scopeId={other.Team}", Ct))
        {
            (await AdminFixture.ReadAsync(filtered)).AsArray().Select(r => Id(r!)).ShouldBe([id]);
        }

        foreach (var team in new[] { org.Team, other.Team })
        {
            using var response = await client.PostAsJsonAsync("/api/routing-rules/test", new { model = "any", teamId = team, headers = new Dictionary<string, string> { [header] = "1" } }, Ct);
            (await AdminFixture.ReadAsync(response))["primaryModel"]!.GetValue<string>().ShouldBe(model.Name);
        }

        // Deleting one team only asks about the rules it is the sole owner of; the shared rule just loses it.
        var conflict = await ConflictAsync(client, $"/api/teams/{org.Team}");
        conflict["rules"]!.AsArray().Select(r => r!["name"]!.GetValue<string>()).ShouldBe(["Only mine"]);
        using var delete = await client.DeleteAsync($"/api/teams/{org.Team}?routingRules=delete", Ct);
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var after = await GetAsync(client, id);
        after["isEnabled"]!.GetValue<bool>().ShouldBeTrue();
        after["scopeIds"]!.AsArray().Select(x => x!.GetValue<Guid>()).ShouldBe([other.Team]);
        using var gone = await client.GetAsync($"/api/routing-rules/{sole}", Ct);
        gone.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await using var scope = fixture.Services.CreateAsyncScope();
        var actions = await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().AuditLog
            .Where(a => a.EntityType == "RoutingRule" && a.EntityId == id).OrderBy(a => a.Id).Select(a => a.Action).ToListAsync(Ct);
        actions.ShouldBe(["create", "update"]);
    }

    // ---- templates and the on/off switch ---------------------------------------------------------------------

    private const string PiiTemplate = "0199d2a0-7e00-7000-8000-000000000001";

    [Fact]
    public async Task Templates_are_seeded_disabled_and_cannot_be_enabled_until_their_models_exist()
    {
        using var client = await fixture.ClientAsync();
        using var list = await client.GetAsync("/api/routing-rules?scope=Global", Ct);
        var templates = (await AdminFixture.ReadAsync(list)).AsArray().Where(r => r!["isTemplate"]!.GetValue<bool>()).ToList();
        templates.Count.ShouldBeGreaterThanOrEqualTo(4);
        templates.ShouldAllBe(t => !t!["isEnabled"]!.GetValue<bool>());
        templates.ShouldAllBe(t => t!["validationErrors"]!.AsArray().Count == 0); // their conditions are valid
        templates.ShouldAllBe(t => !string.IsNullOrEmpty(t!["description"]!.GetValue<string>()));

        var pii = await GetAsync(client, PiiTemplate);
        pii["condition"]!.GetValue<string>().ShouldContain("pii_detected");
        pii["missingModels"]!.AsArray().Select(m => m!.GetValue<string>()).ShouldBe(["local"]);

        using var enable = await client.PutAsJsonAsync($"/api/routing-rules/{PiiTemplate}/enabled", new { isEnabled = true }, Ct);
        var problem = await AdminFixture.ReadAsync(enable, HttpStatusCode.BadRequest);
        problem["detail"]!.GetValue<string>().ShouldContain("local");
        (await GetAsync(client, PiiTemplate))["isEnabled"]!.GetValue<bool>().ShouldBeFalse();

        // Templates exist in every environment, so they are not exported.
        using var export = await client.GetAsync("/api/ops/config/export", Ct);
        (await AdminFixture.ReadAsync(export))["routingRules"]!.AsArray().Select(r => r!["name"]!.GetValue<string>()).ShouldNotContain(pii["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_disabled_rule_may_name_a_missing_model_and_the_switch_enables_it_once_the_model_exists()
    {
        using var client = await fixture.ClientAsync();
        var alias = "later-" + Unique();
        var created = await CreateAsync(client, Body("Waits for " + alias, [alias], enabled: false));
        var id = Id(created);
        created["missingModels"]!.AsArray().Select(m => m!.GetValue<string>()).ShouldBe([alias]);

        using var early = await client.PutAsJsonAsync($"/api/routing-rules/{id}/enabled", new { isEnabled = true }, Ct);
        var problem = await AdminFixture.ReadAsync(early, HttpStatusCode.BadRequest);
        problem["detail"]!.GetValue<string>().ShouldContain(alias);
        problem["errors"]!["targets"].ShouldNotBeNull();

        var model = await ModelAsync(client);
        using var route = await client.PostAsJsonAsync("/api/routes", new
        {
            name = alias, kind = "Chat", isEnabled = true, targets = new[] { new { modelId = model.Id, priority = 0, weight = 1 } },
        }, Ct);
        route.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var on = await client.PutAsJsonAsync($"/api/routing-rules/{id}/enabled", new { isEnabled = true }, Ct);
        var enabled = await AdminFixture.ReadAsync(on);
        enabled["isEnabled"]!.GetValue<bool>().ShouldBeTrue();
        enabled["missingModels"]!.AsArray().ShouldBeEmpty();
        using var off = await client.PutAsJsonAsync($"/api/routing-rules/{id}/enabled", new { isEnabled = false }, Ct);
        (await AdminFixture.ReadAsync(off))["isEnabled"]!.GetValue<bool>().ShouldBeFalse();

        await using var scope = fixture.Services.CreateAsyncScope();
        var actions = await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().AuditLog
            .Where(a => a.EntityType == "RoutingRule" && a.EntityId == id).OrderBy(a => a.Id).Select(a => a.Action).ToListAsync(Ct);
        actions.ShouldBe(["create", "update", "update"]);
    }

    [Fact]
    public async Task Editing_a_template_makes_it_an_ordinary_rule()
    {
        using var client = await fixture.ClientAsync();
        var model = await ModelAsync(client);
        var name = "Template " + Unique();
        Guid id;
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var rule = new Ume.LlmGateway.Domain.Entities.RoutingRule
            {
                Name = name, IsEnabled = false, IsTemplate = true, Targets = [new() { Model = "nowhere-" + Unique() }],
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.RoutingRules.Add(rule);
            await db.SaveChangesAsync(Ct);
            id = rule.Id;
        }

        (await GetAsync(client, id.ToString()))["isTemplate"]!.GetValue<bool>().ShouldBeTrue();
        using var update = await client.PutAsJsonAsync($"/api/routing-rules/{id}", Body(name, [model.Name], enabled: true), Ct);
        var adopted = await AdminFixture.ReadAsync(update);
        adopted["isTemplate"]!.GetValue<bool>().ShouldBeFalse();
        adopted["isEnabled"]!.GetValue<bool>().ShouldBeTrue();
        using var delete = await client.DeleteAsync($"/api/routing-rules/{id}", Ct);
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    // ---- names used by rules cannot disappear --------------------------------------------------------------

    [Fact]
    public async Task Models_and_routes_used_by_rules_cannot_be_removed_or_renamed()
    {
        using var client = await fixture.ClientAsync();
        var model = await ModelAsync(client);
        using var route = await client.PostAsJsonAsync("/api/routes", new
        {
            name = "alias-" + Unique(), kind = "Chat", isEnabled = true, targets = new[] { new { modelId = model.Id, priority = 0, weight = 1 } },
        }, Ct);
        var alias = await AdminFixture.ReadAsync(route, HttpStatusCode.Created);
        var aliasId = alias["id"]!.GetValue<Guid>();
        var aliasName = alias["name"]!.GetValue<string>();
        var standalone = await ModelAsync(client);
        var rule = Id(await CreateAsync(client, Body("Uses both", [aliasName], fallbacks: [standalone.Name])));

        using var deleteRoute = await client.DeleteAsync($"/api/routes/{aliasId}", Ct);
        (await AdminFixture.ReadAsync(deleteRoute, HttpStatusCode.Conflict))["detail"]!.GetValue<string>().ShouldContain("Uses both");
        using var renameRoute = await client.PutAsJsonAsync($"/api/routes/{aliasId}", new
        {
            name = aliasName + "-renamed", kind = "Chat", isEnabled = true, targets = new[] { new { modelId = model.Id, priority = 0, weight = 1 } },
        }, Ct);
        renameRoute.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var editRoute = await client.PutAsJsonAsync($"/api/routes/{aliasId}", new
        {
            name = aliasName, kind = "Chat", isEnabled = true, description = "same name is fine", targets = new[] { new { modelId = model.Id, priority = 0, weight = 2 } },
        }, Ct);
        editRoute.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var deleteModel = await client.DeleteAsync($"/api/models/{standalone.Id}", Ct);
        (await AdminFixture.ReadAsync(deleteModel, HttpStatusCode.Conflict))["detail"]!.GetValue<string>().ShouldContain("Uses both");
        using var renameModel = await client.PutAsJsonAsync($"/api/models/{standalone.Id}", new
        {
            name = standalone.Name + "-renamed", upstreamModel = "up", kind = "Chat", parameterProfile = "Standard", isEnabled = true,
        }, Ct);
        renameModel.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Once the rule is gone they can be removed again.
        using var deleteRule = await client.DeleteAsync($"/api/routing-rules/{rule}", Ct);
        deleteRule.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var deleteModelAgain = await client.DeleteAsync($"/api/models/{standalone.Id}", Ct);
        deleteModelAgain.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    // ---- export / import -------------------------------------------------------------------------------------

    [Fact]
    public async Task Global_rules_are_exported_and_imported_but_scoped_rules_are_not()
    {
        using var client = await fixture.ClientAsync();
        var model = await ModelAsync(client);
        var org = await fixture.OrganisationAsync(client);
        var header = "x-" + Unique();
        var name = "Exported " + Unique();
        var global = Id(await CreateAsync(client, Body(name, [model.Name], $"headers['{header}'] == '1'", priority: 4, fallbacks: [model.Name])));
        var scopedName = "Scoped " + Unique();
        await CreateAsync(client, Body(scopedName, [model.Name], scope: "Team", scopeId: org.Team));

        using var export = await client.GetAsync("/api/ops/config/export", Ct);
        var document = await AdminFixture.ReadAsync(export);
        var rules = document["routingRules"]!.AsArray();
        rules.Select(r => r!["name"]!.GetValue<string>()).ShouldContain(name);
        rules.Select(r => r!["name"]!.GetValue<string>()).ShouldNotContain(scopedName);
        rules.Single(r => r!["name"]!.GetValue<string>() == name)!["targets"]![0]!["model"]!.GetValue<string>().ShouldBe(model.Name);

        // Delete it, then import restores it.
        using var delete = await client.DeleteAsync($"/api/routing-rules/{global}", Ct);
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var import = await client.PostAsJsonAsync("/api/ops/config/import", document, Ct);
        (await AdminFixture.ReadAsync(import))["created"]!.GetValue<int>().ShouldBeGreaterThanOrEqualTo(1);
        using var list = await client.GetAsync("/api/routing-rules?scope=Global", Ct);
        var restored = (await AdminFixture.ReadAsync(list)).AsArray().Single(r => r!["name"]!.GetValue<string>() == name)!;
        restored["priority"]!.GetValue<int>().ShouldBe(4);
        restored["condition"]!.GetValue<string>().ShouldBe($"headers['{header}'] == '1'");

        // Importing again updates instead of duplicating.
        using var again = await client.PostAsJsonAsync("/api/ops/config/import", document, Ct);
        (await AdminFixture.ReadAsync(again))["updated"]!.GetValue<int>().ShouldBeGreaterThanOrEqualTo(1);
        using var list2 = await client.GetAsync("/api/routing-rules?scope=Global", Ct);
        (await AdminFixture.ReadAsync(list2)).AsArray().Count(r => r!["name"]!.GetValue<string>() == name).ShouldBe(1);
    }

    [Fact]
    public async Task Import_rejects_invalid_rules_without_changing_anything_and_accepts_old_documents()
    {
        using var client = await fixture.ClientAsync();
        using var export = await client.GetAsync("/api/ops/config/export", Ct);
        var document = await AdminFixture.ReadAsync(export);
        var name = "Imported " + Unique();

        document["routingRules"] = new JsonArray(JsonNode.Parse($$"""{"name":"{{name}}","condition":"budget_used >","targets":[{"model":"x","weight":1}]}"""));
        using var badCondition = await client.PostAsJsonAsync("/api/ops/config/import", document, Ct);
        (await AdminFixture.ReadAsync(badCondition, HttpStatusCode.BadRequest))["detail"]!.GetValue<string>().ShouldContain(name);

        document["routingRules"] = new JsonArray(JsonNode.Parse($$"""{"name":"{{name}}","condition":"","targets":[{"model":"no/such-model","weight":1}]}"""));
        using var badTarget = await client.PostAsJsonAsync("/api/ops/config/import", document, Ct);
        badTarget.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        document["routingRules"] = new JsonArray(
            JsonNode.Parse($$"""{"name":"{{name}}","condition":"","targets":[{"model":"x","weight":1}],"chain":true}"""),
            JsonNode.Parse($$"""{"name":"{{name.ToUpperInvariant()}}","condition":"","targets":[{"model":"x","weight":1}],"chain":true}"""));
        using var duplicate = await client.PostAsJsonAsync("/api/ops/config/import", document, Ct);
        duplicate.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var list = await client.GetAsync("/api/routing-rules?scope=Global", Ct);
        (await AdminFixture.ReadAsync(list)).AsArray().Select(r => r!["name"]!.GetValue<string>()).ShouldNotContain(name);

        // Documents exported before routing rules existed have no routingRules member and still import.
        document.AsObject().Remove("routingRules");
        using var old = await client.PostAsJsonAsync("/api/ops/config/import", document, Ct);
        old.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
