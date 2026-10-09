using Ume.LlmGateway.Domain.Routing;
using Ume.LlmGateway.Domain.Routing.Expressions;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.Domain.Tests;

public class RoutingSignalsTests
{
    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(50, 100, 50)]
    [InlineData(92.5, 100, 92.5)]
    [InlineData(1, 3, 33.333333333333)]
    [InlineData(100, 100, 100)]
    [InlineData(250, 100, 100)] // overspend is clamped
    [InlineData(-5, 100, 0)]
    [InlineData(0, 0, 100)] // a zero limit is always full: enforcement rejects at spend >= limit
    [InlineData(10, -1, 100)]
    public void Utilisation_percent_is_clamped_and_unrounded(double spent, double limit, double expected) =>
        BudgetEvaluator.UtilisationPercent((decimal)spent, (decimal)limit).ShouldBe(expected, 1e-9);

    private static RoutingRuleDefinition Rule(string condition, bool enabled = true) =>
        new(Guid.NewGuid(), Guid.NewGuid().ToString("N"), enabled, 0, RoutingScope.Global, null, condition, false, [new RuleTarget("m")], []);

    [Fact]
    public void Expression_reports_the_variables_it_uses()
    {
        var (expr, errors) = RoutingExpression.Compile("""budget_used > 80 && headers["x"] == "y" && model in ["a"]""", RoutingSchema.Instance);
        errors.ShouldBeEmpty();
        expr!.Variables.Order().ShouldBe(["budget_used", "headers", "model"]);
        RoutingExpression.Compile("", RoutingSchema.Instance).Expression!.Variables.ShouldBeEmpty();
    }

    [Fact]
    public void Rule_set_knows_which_variables_its_enabled_rules_need()
    {
        var (set, errors) = RoutingRuleSet.Build(
        [
            Rule("budget_used > 90"),
            Rule("""headers["x"] == "1" """),
            Rule("tokens_used > 50", enabled: false), // disabled: not needed
        ]);
        errors.ShouldBeEmpty();
        set.References("budget_used").ShouldBeTrue();
        set.References("headers").ShouldBeTrue();
        set.References("tokens_used").ShouldBeFalse();
        set.References("pii_detected").ShouldBeFalse();
        RoutingRuleSet.Empty.References("budget_used").ShouldBeFalse();
    }
}
