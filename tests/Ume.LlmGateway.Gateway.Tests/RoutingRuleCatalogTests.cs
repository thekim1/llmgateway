using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Routing;
using Ume.LlmGateway.Gateway.Pipeline;
using Ume.LlmGateway.Infrastructure.Persistence;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>
/// Routing rules are stored in PostgreSQL and compiled once per catalogue snapshot. The database and catalogue are
/// shared with tests running in parallel, so every rule here is scoped to a team id unique to the test and the
/// assertions look only at that scope.
/// </summary>
public sealed class RoutingRuleCatalogTests(GatewayFixture fixture)
{
    private readonly Guid _team = Guid.NewGuid();

    private RoutingRule Rule(string name, string condition, string target, Action<RoutingRule>? configure = null)
    {
        var rule = new RoutingRule
        {
            Name = name,
            Condition = condition,
            Scope = RoutingScope.Team,
            ScopeId = _team,
            Targets = [new RoutingRuleTarget { Model = target, Weight = 3 }],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        configure?.Invoke(rule);
        return rule;
    }

    private RoutingContext Ctx(Dictionary<string, string>? headers = null, double? budgetUsed = null) =>
        new() { Model = "any", TeamId = _team, Headers = headers ?? [], BudgetUsed = budgetUsed };

    private static async Task<CatalogSnapshot> SnapshotAsync(GatewayCatalog catalog)
    {
        catalog.Invalidate();
        return await catalog.GetAsync(TestContext.Current.CancellationToken);
    }

    private async Task WithRulesAsync(IEnumerable<RoutingRule> rules, Func<CatalogSnapshot, Task> assert)
    {
        var list = rules.ToList();
        var ids = list.Select(l => l.Id).ToList();
        var catalog = fixture.Services.GetRequiredService<GatewayCatalog>();
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            db.RoutingRules.AddRange(list);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        try
        {
            await assert(await SnapshotAsync(catalog));
        }
        finally
        {
            await using var scope = fixture.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            await db.RoutingRules.Where(r => ids.Contains(r.Id)).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            catalog.Invalidate();
        }
    }

    [Fact]
    public async Task Catalog_loads_rules_with_targets_fallbacks_and_scope()
    {
        var rule = Rule("Premium", "headers[\"x-tier\"] == \"premium\"", "eu/ok", r =>
        {
            r.Priority = 7;
            r.Chain = true;
            r.Fallbacks = ["onprem/ok", "external/ok"];
            r.Targets.Add(new RoutingRuleTarget { Model = "external/ok", Weight = 1 });
        });

        await WithRulesAsync([rule], snapshot =>
        {
            snapshot.RuleErrors.Where(e => e.RuleId == rule.Id).ShouldBeEmpty();

            var decision = snapshot.Rules.Evaluate(Ctx(new() { ["X-Tier"] = "premium" }), new Random(1));
            decision.Models.Take(2).Order().ShouldBe(["eu/ok", "external/ok"]);
            decision.Models.Skip(2).ShouldBe(["onprem/ok"]); // "external/ok" fallback duplicates a target
            decision.Applied.Single().Name.ShouldBe("Premium");

            // A request from another team is outside the rule's scope.
            snapshot.Rules.Evaluate(Ctx(new() { ["x-tier"] = "premium" }) with { TeamId = Guid.NewGuid() }, new Random(1)).Matched.ShouldBeFalse();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Invalid_rules_are_skipped_and_reported_without_breaking_the_catalogue()
    {
        var good = Rule("Good", "budget_used > 90", "eu/ok");
        var bad = Rule("Bad", "budget_usd > 90", "eu/ok");
        var noTargets = Rule("NoTargets", "", "x", r => r.Targets = []);

        await WithRulesAsync([good, bad, noTargets], snapshot =>
        {
            snapshot.RuleErrors.Where(e => e.RuleId == bad.Id || e.RuleId == noTargets.Id || e.RuleId == good.Id)
                .Select(e => e.RuleName).Order().ShouldBe(["Bad", "NoTargets"]);
            snapshot.Rules.Evaluate(Ctx(budgetUsed: 95), new Random(1)).Applied.Select(a => a.Name).ShouldBe(["Good"]);
            snapshot.Providers.ShouldNotBeEmpty(); // routing itself still works
            return Task.CompletedTask;
        });

        fixture.Logs.Text.ShouldContain("Routing rule 'Bad'");
    }

    [Fact]
    public async Task Disabled_rules_are_not_evaluated()
    {
        var off = Rule("Off", "", "eu/ok", r => r.IsEnabled = false);
        await WithRulesAsync([off], snapshot =>
        {
            snapshot.Rules.Evaluate(Ctx(), new Random(1)).Matched.ShouldBeFalse();
            snapshot.RuleErrors.Where(e => e.RuleId == off.Id).ShouldBeEmpty();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Deleting_a_rule_removes_its_targets_and_a_new_snapshot_forgets_it()
    {
        var rule = Rule("Temp", "", "eu/ok");
        await WithRulesAsync([rule], snapshot =>
        {
            snapshot.Rules.Evaluate(Ctx(), new Random(1)).Matched.ShouldBeTrue();
            return Task.CompletedTask;
        });

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        (await db.RoutingRuleTargets.CountAsync(t => t.RoutingRuleId == rule.Id, TestContext.Current.CancellationToken)).ShouldBe(0); // cascade delete
        var fresh = await SnapshotAsync(fixture.Services.GetRequiredService<GatewayCatalog>());
        fresh.Rules.Evaluate(Ctx(), new Random(1)).Matched.ShouldBeFalse();
    }
}
