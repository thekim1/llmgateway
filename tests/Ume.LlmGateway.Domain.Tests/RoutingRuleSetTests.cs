using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Routing;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.Domain.Tests;

public class RoutingRuleSetTests
{
    private static readonly Guid KeyA = Guid.NewGuid();
    private static readonly Guid TeamA = Guid.NewGuid();
    private static readonly Guid DeptA = Guid.NewGuid();

    private static RoutingRuleDefinition Rule(
        string name,
        string condition = "",
        string target = "ume/out",
        int priority = 0,
        RoutingScope scope = RoutingScope.Global,
        Guid? scopeId = null,
        bool chain = false,
        bool enabled = true,
        string[]? fallbacks = null,
        params RuleTarget[] targets) => new(
            Guid.NewGuid(), name, enabled, priority, scope, scopeId, condition, chain,
            targets.Length > 0 ? targets : [new RuleTarget(target)], fallbacks ?? []);

    private static RoutingRuleSet Build(params RoutingRuleDefinition[] rules)
    {
        var (set, errors) = RoutingRuleSet.Build(rules);
        errors.ShouldBeEmpty();
        return set;
    }

    private static RoutingContext Ctx(string model = "ume/chat", Dictionary<string, string>? headers = null) => new()
    {
        Model = model,
        Endpoint = "chat_completions",
        Headers = headers ?? [],
        KeyId = KeyA,
        TeamId = TeamA,
        DepartmentId = DeptA,
        KeyName = "k",
        TeamName = "ml-research",
        DepartmentName = "it",
    };

    private static RoutingDecision Run(RoutingRuleSet set, RoutingContext ctx, int seed = 1) => set.Evaluate(ctx, new Random(seed));

    [Fact]
    public void No_rules_or_no_match_means_unmatched()
    {
        Run(RoutingRuleSet.Empty, Ctx()).Matched.ShouldBeFalse();
        Run(Build(Rule("r", """model == "other" """)), Ctx()).Matched.ShouldBeFalse();
    }

    [Fact]
    public void Matching_rule_rewrites_to_its_target()
    {
        var decision = Run(Build(Rule("premium", """headers["x-tier"] == "premium" """, target: "ume/premium")), Ctx(headers: new() { ["X-Tier"] = "premium" }));
        decision.Models.ShouldBe(["ume/premium"]);
        decision.Applied.Single().Name.ShouldBe("premium");
        decision.Applied.Single().FromModel.ShouldBe("ume/chat");
    }

    [Fact]
    public void Empty_condition_always_matches() => Run(Build(Rule("all")), Ctx()).PrimaryModel.ShouldBe("ume/out");

    [Fact]
    public void Disabled_rules_are_ignored() => Run(Build(Rule("off", enabled: false)), Ctx()).Matched.ShouldBeFalse();

    [Fact]
    public void Lower_priority_number_is_checked_first_and_first_match_wins()
    {
        var set = Build(Rule("b", priority: 10, target: "B"), Rule("a", priority: 5, target: "A"), Rule("c", priority: 20, target: "C"));
        Run(set, Ctx()).PrimaryModel.ShouldBe("A");
    }

    [Fact]
    public void Equal_priority_is_ordered_by_name_for_determinism()
    {
        var set = Build(Rule("zeta", target: "Z"), Rule("alpha", target: "A"));
        Run(set, Ctx()).PrimaryModel.ShouldBe("A");
    }

    [Fact]
    public void Scope_order_is_key_then_team_then_department_then_global_regardless_of_priority()
    {
        var set = Build(
            Rule("global", priority: -100, target: "global"),
            Rule("dept", priority: 0, scope: RoutingScope.Department, scopeId: DeptA, target: "dept"),
            Rule("team", priority: 99, scope: RoutingScope.Team, scopeId: TeamA, target: "team"),
            Rule("key", priority: 999, scope: RoutingScope.VirtualKey, scopeId: KeyA, target: "key"));
        Run(set, Ctx()).PrimaryModel.ShouldBe("key");
        Run(set, Ctx() with { KeyId = Guid.NewGuid() }).PrimaryModel.ShouldBe("team");
        Run(set, Ctx() with { KeyId = Guid.NewGuid(), TeamId = Guid.NewGuid() }).PrimaryModel.ShouldBe("dept");
        Run(set, Ctx() with { KeyId = Guid.NewGuid(), TeamId = Guid.NewGuid(), DepartmentId = Guid.NewGuid() }).PrimaryModel.ShouldBe("global");
    }

    [Fact]
    public void Key_rule_follows_the_key_through_rotation()
    {
        var oldKey = Guid.NewGuid();
        var set = Build(Rule("old key", scope: RoutingScope.VirtualKey, scopeId: oldKey, target: "kept"));

        // The replacement key has a new id, but its lineage includes the key it replaced.
        Run(set, Ctx() with { KeyId = Guid.NewGuid(), KeyLineage = new HashSet<Guid> { oldKey } }).PrimaryModel.ShouldBe("kept");
        Run(set, Ctx() with { KeyId = Guid.NewGuid(), KeyLineage = new HashSet<Guid> { Guid.NewGuid() } }).Matched.ShouldBeFalse();
        Run(set, Ctx() with { KeyId = Guid.NewGuid() }).Matched.ShouldBeFalse(); // no lineage supplied: only the key itself counts
    }

    [Fact]
    public void Scoped_rule_does_not_apply_when_the_request_has_no_such_scope()
    {
        var set = Build(Rule("team", scope: RoutingScope.Team, scopeId: TeamA));
        Run(set, new RoutingContext { Model = "m" }).Matched.ShouldBeFalse();
    }

    [Fact]
    public void Non_matching_higher_scope_falls_through_to_lower_scope()
    {
        var set = Build(
            Rule("key", """headers["x-vip"] == "1" """, scope: RoutingScope.VirtualKey, scopeId: KeyA, target: "key"),
            Rule("global", target: "global"));
        Run(set, Ctx()).PrimaryModel.ShouldBe("global");
        Run(set, Ctx(headers: new() { ["x-vip"] = "1" })).PrimaryModel.ShouldBe("key");
    }

    [Fact]
    public void Missing_header_or_unset_budget_does_not_match()
    {
        var set = Build(Rule("h", """headers["x-nope"] == "1" """), Rule("b", "budget_used > 85", target: "cheap"));
        Run(set, Ctx()).Matched.ShouldBeFalse();
        Run(set, Ctx() with { BudgetUsed = 90 }).PrimaryModel.ShouldBe("cheap");
        Run(set, Ctx() with { BudgetUsed = 50 }).Matched.ShouldBeFalse();
    }

    [Fact]
    public void Conditions_can_use_all_context_variables()
    {
        var set = Build(Rule("all",
            """team_name == "ml-research" && department_name == "it" && key_name == "k" && endpoint == "chat_completions" && prompt_tokens > 1000 && tokens_used < 50 && pii_detected == false && params["stream"] == true && params["temperature"] <= 0.5 && model.startsWith("ume/")"""));
        var ctx = Ctx() with { TokensUsed = 10, PiiDetected = false, PromptTokens = 2000, Params = new Dictionary<string, object?> { ["stream"] = true, ["temperature"] = 0.2 } };
        Run(set, ctx).Matched.ShouldBeTrue();
        Run(set, ctx with { PiiDetected = true }).Matched.ShouldBeFalse();
        Run(set, ctx with { PromptTokens = null }).Matched.ShouldBeFalse();
    }

    [Fact]
    public void Ids_are_matched_as_strings()
    {
        var set = Build(Rule("id", $"team_id == '{TeamA}'"));
        Run(set, Ctx()).Matched.ShouldBeTrue();
    }

    [Fact]
    public void Weighted_targets_follow_the_weights()
    {
        var set = Build(Rule("split", targets: [new RuleTarget("openai", 70), new RuleTarget("groq", 30)]));
        var random = new Random(42);
        var openai = Enumerable.Range(0, 10_000).Count(_ => set.Evaluate(Ctx(), random).PrimaryModel == "openai");
        openai.ShouldBeInRange(6700, 7300);
    }

    [Fact]
    public void Remaining_targets_and_fallbacks_follow_in_order_without_duplicates()
    {
        var set = Build(Rule("r", fallbacks: ["x", "A", "y"], targets: [new RuleTarget("a", 1), new RuleTarget("b", 1000000)]));
        for (var seed = 0; seed < 20; seed++)
        {
            var models = Run(set, Ctx(), seed).Models;
            models.Count.ShouldBe(4); // a, b, x, y: "A" duplicates "a" case-insensitively
            models.Take(2).Order().ShouldBe(["a", "b"]);
            models.Skip(2).ShouldBe(["x", "y"]);
        }
    }

    [Fact]
    public void Single_target_with_fallbacks()
    {
        Run(Build(Rule("r", target: "azure/gpt-4o", fallbacks: ["groq/llama"])), Ctx()).Models.ShouldBe(["azure/gpt-4o", "groq/llama"]);
    }

    // ---- chaining -------------------------------------------------------------------------------------------

    [Fact]
    public void Chain_normalises_an_alias_then_routes()
    {
        var set = Build(
            Rule("normalize", """model == "gpt-4" """, "gpt-4-turbo", priority: 0, chain: true),
            Rule("route", """model == "gpt-4-turbo" """, "azure/gpt-4-turbo", priority: 1));
        var decision = Run(set, Ctx("gpt-4"));
        decision.Models.ShouldBe(["azure/gpt-4-turbo"]);
        decision.Applied.Select(a => a.Name).ShouldBe(["normalize", "route"]);
        decision.Applied[1].FromModel.ShouldBe("gpt-4-turbo");
    }

    [Fact]
    public void Without_chain_the_first_rule_is_final()
    {
        var set = Build(
            Rule("normalize", """model == "gpt-4" """, "gpt-4-turbo", chain: false),
            Rule("route", """model == "gpt-4-turbo" """, "azure/gpt-4-turbo", priority: 1));
        Run(set, Ctx("gpt-4")).PrimaryModel.ShouldBe("gpt-4-turbo");
    }

    [Fact]
    public void Chain_with_no_following_match_keeps_the_chained_result()
    {
        Run(Build(Rule("normalize", """model == "gpt-4" """, "gpt-4-turbo", chain: true)), Ctx("gpt-4")).PrimaryModel.ShouldBe("gpt-4-turbo");
    }

    [Fact]
    public void Last_matched_rule_decides_models_and_fallbacks()
    {
        var set = Build(
            Rule("first", """model == "a" """, "b", chain: true, fallbacks: ["from-first"]),
            Rule("second", """model == "b" """, "c", priority: 1, fallbacks: ["from-second"]));
        Run(set, Ctx("a")).Models.ShouldBe(["c", "from-second"]);
    }

    [Fact]
    public void Chain_stops_when_the_model_does_not_change()
    {
        var decision = Run(Build(Rule("same", """model == "a" """, "a", chain: true, fallbacks: ["f"])), Ctx("a"));
        decision.Models.ShouldBe(["a", "f"]);
        decision.Applied.Count.ShouldBe(1);
        decision.ChainLimitReached.ShouldBeFalse();
    }

    [Fact]
    public void Chain_cycles_terminate()
    {
        var set = Build(
            Rule("a-to-b", """model == "a" """, "b", chain: true),
            Rule("b-to-a", """model == "b" """, "a", priority: 1, chain: true));
        var decision = Run(set, Ctx("a"));
        decision.Applied.Select(r => r.Name).ShouldBe(["a-to-b", "b-to-a"]);
        decision.PrimaryModel.ShouldBe("a");
        decision.ChainLimitReached.ShouldBeFalse();
    }

    [Fact]
    public void A_catch_all_chain_rule_is_applied_only_once()
    {
        var decision = Run(Build(Rule("all", "", "x", chain: true)), Ctx("a"));
        decision.Applied.Count.ShouldBe(1);
        decision.PrimaryModel.ShouldBe("x");
    }

    [Fact]
    public void Chain_depth_is_limited()
    {
        var rules = Enumerable.Range(0, 20).Select(i => Rule($"r{i}", $"model == 'm{i}'", $"m{i + 1}", priority: i, chain: true)).ToArray();
        var decision = Run(Build(rules), Ctx("m0"));
        decision.ChainLimitReached.ShouldBeTrue();
        decision.Applied.Count.ShouldBe(RoutingRuleSet.MaxChainDepth);
        decision.PrimaryModel.ShouldBe($"m{RoutingRuleSet.MaxChainDepth}");
    }

    [Fact]
    public void Chain_conditions_see_the_rewritten_model_and_the_original_request()
    {
        var set = Build(
            Rule("rewrite", """headers["x-tier"] == "premium" """, "ume/premium", chain: true),
            Rule("pinned", """model == "ume/premium" && budget_used > 90""", "ume/premium-eu", priority: 1));
        Run(set, Ctx(headers: new() { ["x-tier"] = "premium" }) with { BudgetUsed = 95 }).PrimaryModel.ShouldBe("ume/premium-eu");
        Run(set, Ctx(headers: new() { ["x-tier"] = "premium" }) with { BudgetUsed = 10 }).PrimaryModel.ShouldBe("ume/premium");
    }

    // ---- build validation -----------------------------------------------------------------------------------

    [Fact]
    public void Invalid_rules_are_reported_and_left_out()
    {
        var good = Rule("good", target: "ok");
        var (set, errors) = RoutingRuleSet.Build(
        [
            good,
            Rule("bad-expr", "budget_usd > 1"),
            Rule("bad-type", "budget_used"),
            Rule("no-name") with { Name = " " },
            Rule("no-scope-id", scope: RoutingScope.Team),
            Rule("global-with-id", scopeId: Guid.NewGuid()),
            Rule("bad-weight", targets: [new RuleTarget("x", 0)]),
            Rule("no-target") with { Targets = [] },
            Rule("blank-target", targets: [new RuleTarget(" ")]),
            Rule("blank-fallback", fallbacks: [" "]),
            Rule("too-many", targets: [.. Enumerable.Range(0, 21).Select(i => new RuleTarget($"m{i}"))]),
        ]);
        set.Count.ShouldBe(1);
        errors.Select(e => e.RuleName).ShouldBe(["bad-expr", "bad-type", " ", "no-scope-id", "global-with-id", "bad-weight", "no-target", "blank-target", "blank-fallback", "too-many"]);
        errors[0].Expression.ShouldNotBeNull();
        errors[0].Message.ShouldContain("Unknown variable");
        Run(set, Ctx()).PrimaryModel.ShouldBe("ok");
    }

    [Fact]
    public void Duplicate_names_in_the_same_scope_are_rejected_but_allowed_across_scopes()
    {
        var (set, errors) = RoutingRuleSet.Build(
        [
            Rule("Dup", target: "1"),
            Rule("dup", target: "2"),
            Rule("Dup", scope: RoutingScope.Team, scopeId: TeamA, target: "3"),
            Rule("Dup", scope: RoutingScope.Team, scopeId: Guid.NewGuid(), target: "4"),
        ]);
        errors.Single().Message.ShouldContain("already exists");
        set.Count.ShouldBe(3);
    }

    [Fact]
    public void Disabled_rules_are_still_validated()
    {
        var (set, errors) = RoutingRuleSet.Build([Rule("off", "nope > 1", enabled: false)]);
        errors.ShouldNotBeEmpty();
        set.Count.ShouldBe(0);
    }

    [Fact]
    public void Check_validates_a_single_rule()
    {
        RoutingRuleSet.Check(Rule("ok", "budget_used > 1")).ShouldBeEmpty();
        RoutingRuleSet.Check(Rule("bad", "budget_used >")).ShouldNotBeEmpty();
        RoutingRuleSet.Check(Rule("bad") with { Targets = [] }).ShouldNotBeEmpty();
    }

    // ---- explain ---------------------------------------------------------------------------------------------

    [Fact]
    public void Explain_shows_each_rule_checked_and_why()
    {
        var set = Build(
            Rule("vip", """headers["x-tier"] == "premium" """, "p", priority: 0),
            Rule("budget", "budget_used > 85", "cheap", priority: 1),
            Rule("never", "true", "z", priority: 2),
            Rule("other-team", scope: RoutingScope.Team, scopeId: Guid.NewGuid()));
        var explain = new List<RuleEvaluation>();
        var decision = set.Evaluate(Ctx() with { BudgetUsed = 90 }, new Random(1), explain);

        decision.PrimaryModel.ShouldBe("cheap");
        explain.Select(e => (e.Name, e.Outcome)).ShouldBe([("vip", RuleOutcome.NotMatched), ("budget", RuleOutcome.Matched)]);
        explain[0].Trace.Single().Result.ShouldBeNull(); // header missing
        explain[1].Trace.Single().LeftValue.ShouldBe("90");
    }

    [Fact]
    public void Explain_reports_skipped_rules_in_a_chain()
    {
        var set = Build(Rule("all", "", "x", chain: true));
        var explain = new List<RuleEvaluation>();
        set.Evaluate(Ctx("a"), new Random(1), explain);
        explain.Select(e => e.Outcome).ShouldBe([RuleOutcome.Matched, RuleOutcome.Skipped]); // applied once, not re-applied on the next pass

        var chained = Build(Rule("first", """model == "a" """, "a2", chain: true), Rule("second", "true", "z", priority: 1, chain: true));
        explain.Clear();
        chained.Evaluate(Ctx("a"), new Random(1), explain);
        explain.Select(e => (e.Name, e.ChainStep, e.Outcome)).ShouldBe(
        [
            ("first", 0, RuleOutcome.Matched),
            ("first", 1, RuleOutcome.Skipped),
            ("second", 1, RuleOutcome.Matched),
            ("first", 2, RuleOutcome.Skipped),
            ("second", 2, RuleOutcome.Skipped),
        ]);
    }

    // ---- rules never widen access ----------------------------------------------------------------------------

    private static RouteTarget Deployment(string name, string provider, DataResidency residency)
    {
        var account = new ProviderAccount { Name = provider, BaseUrl = "http://x", Residency = residency, Capabilities = ProviderCapabilities.ChatCompletions };
        var deployment = new ModelDeployment { Name = name, UpstreamModel = name, ProviderAccount = account, ProviderAccountId = account.Id };
        return new RouteTarget { ModelDeployment = deployment, ModelDeploymentId = deployment.Id };
    }

    [Fact]
    public void A_rule_cannot_route_a_key_to_a_provider_it_may_not_use()
    {
        var catalog = new Dictionary<string, RouteTarget>
        {
            ["azure/gpt"] = Deployment("azure/gpt", "azure", DataResidency.Eu),
            ["openai/gpt"] = Deployment("openai/gpt", "openai", DataResidency.External),
        };
        var set = Build(Rule("escape", "", "openai/gpt", fallbacks: ["azure/gpt"]));
        var decision = Run(set, Ctx());

        // The key only allows the azure provider and EU residency: the primary is dropped, the fallback survives.
        var constraints = new RoutingConstraints(GatewayEndpoint.ChatCompletions, RouteSelector.EffectiveResidencies([DataResidency.Eu], null), null, ["azure"]);
        var candidates = RouteSelector.Order(decision.Models.Select(m => catalog[m]), constraints, new Random(1));
        candidates.Select(c => c.ModelDeployment!.Name).ShouldBe(["azure/gpt"]);

        // With only the disallowed target there is nothing to attempt (the gateway then answers 503, never calls it).
        var onlyEscape = Run(Build(Rule("escape", "", "openai/gpt")), Ctx());
        RouteSelector.Order(onlyEscape.Models.Select(m => catalog[m]), constraints, new Random(1)).ShouldBeEmpty();
    }

    [Fact]
    public void A_rule_cannot_send_pii_requests_off_premises()
    {
        var onPrem = Deployment("local/llama", "local", DataResidency.OnPrem);
        var external = Deployment("openai/gpt", "openai", DataResidency.External);
        var decision = Run(Build(Rule("to-external", "", "openai/gpt", fallbacks: ["local/llama"])), Ctx());
        var catalog = new Dictionary<string, RouteTarget> { ["local/llama"] = onPrem, ["openai/gpt"] = external };

        var constraints = new RoutingConstraints(GatewayEndpoint.ChatCompletions, RouteSelector.EffectiveResidencies([], DataResidency.OnPrem));
        RouteSelector.Order(decision.Models.Select(m => catalog[m]), constraints, new Random(1))
            .Select(c => c.ModelDeployment!.Name).ShouldBe(["local/llama"]);
    }

    [Fact]
    public void Evaluation_is_thread_safe()
    {
        var set = Build(Rule("split", """headers["x-tier"] == "premium" """, targets: [new RuleTarget("a", 1), new RuleTarget("b", 1)]));
        Parallel.For(0, 2000, _ => set.Evaluate(Ctx(headers: new() { ["x-tier"] = "premium" }), new Random()).Matched.ShouldBeTrue());
    }
}
