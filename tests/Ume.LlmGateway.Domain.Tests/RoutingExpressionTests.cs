using Ume.LlmGateway.Domain.Routing.Expressions;

namespace Ume.LlmGateway.Domain.Tests;

public class RoutingExpressionTests
{
    private static readonly ExpressionSchema Schema = new(new Dictionary<string, ExprType>
    {
        ["model"] = ExprType.Text,
        ["endpoint"] = ExprType.Text,
        ["headers"] = ExprType.TextMap,
        ["params"] = ExprType.AnyMap,
        ["team_name"] = ExprType.Text,
        ["budget_used"] = ExprType.Number,
        ["tokens_used"] = ExprType.Number,
        ["pii_detected"] = ExprType.Bool,
        ["prompt_tokens"] = ExprType.Number,
        ["tags"] = ExprType.List,
        ["complexity_tier"] = ExprType.Text,
    });

    private static ExpressionVariables Vars() => new ExpressionVariables()
        .Set("model", "claude-sonnet")
        .Set("endpoint", "chat_completions")
        .Set("headers", new Dictionary<string, string> { ["X-Tier"] = "premium", ["x-region"] = "eu", ["tier"] = "gold" })
        .Set("team_name", "ml-research")
        .Set("budget_used", 92.5)
        .Set("tokens_used", 10)
        .Set("pii_detected", false)
        .Set("prompt_tokens", 1200)
        .Set("tags", ["a", "b"]);

    private static RoutingExpression Compile(string source)
    {
        var (expr, errors) = RoutingExpression.Compile(source, Schema);
        errors.ShouldBeEmpty();
        return expr!;
    }

    private static bool Eval(string source) => Compile(source).Matches(Vars());

    private static ExpressionError Invalid(string source)
    {
        var (expr, errors) = RoutingExpression.Compile(source, Schema);
        expr.ShouldBeNull();
        errors.Count.ShouldBe(1);
        return errors[0];
    }

    [Theory]
    [InlineData("""headers["x-tier"] == "premium" """, true)]
    [InlineData("""headers["X-TIER"] == "premium" """, true)] // header lookup is case-insensitive
    [InlineData("""headers["x-tier"] == "basic" """, false)]
    [InlineData("""headers.tier == "gold" """, true)] // field access on a map
    [InlineData("""headers.nope == "gold" """, false)]
    [InlineData("budget_used > 85", true)]
    [InlineData("budget_used >= 92.5", true)]
    [InlineData("budget_used < 92.5", false)]
    [InlineData("budget_used != 92.5", false)]
    [InlineData("""team_name == "ml-research" && model.startsWith("claude-")""", true)]
    [InlineData("""team_name == "other" || model.endsWith("sonnet")""", true)]
    [InlineData("""model.contains("son")""", true)]
    [InlineData("""model.matches("^claude-(sonnet|opus)$")""", true)]
    [InlineData("""matches(model, "^gpt")""", false)]
    [InlineData("""model in ["claude-sonnet", "gpt-4o"]""", true)]
    [InlineData("""!(model in ["gpt-4o"])""", true)]
    [InlineData("""headers["x-tier"] in ["premium", "gold"]""", true)]
    [InlineData("""headers["x-region"] in ["us"]""", false)]
    [InlineData("'x-tier' in headers", true)]
    [InlineData("'x-missing' in headers", false)]
    [InlineData("pii_detected", false)]
    [InlineData("!pii_detected", true)]
    [InlineData("prompt_tokens * 2 > 2000", true)]
    [InlineData("prompt_tokens / 4 == 300", true)]
    [InlineData("prompt_tokens % 1000 == 200", true)]
    [InlineData("-prompt_tokens < 0", true)]
    [InlineData("""model + "-x" == "claude-sonnet-x" """, true)]
    [InlineData("size(tags) == 2", true)]
    [InlineData("tags.size() == 2", true)]
    [InlineData("""size(model) == 13""", true)]
    [InlineData("tags[0] == 'a'", true)]
    [InlineData("tags[5] == 'a'", false)]
    [InlineData("""model == r"claude-sonnet" """, true)]
    [InlineData("""model.matches(r"^claude-\w+$")""", true)]
    [InlineData("budget_used > 50 && budget_used < 95 && tokens_used < 20", true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Evaluates(string source, bool expected) => Eval(source).ShouldBe(expected);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_expression_always_matches(string source)
    {
        var expr = Compile(source);
        expr.IsAlwaysTrue.ShouldBeTrue();
        expr.Matches(new ExpressionVariables()).ShouldBeTrue();
    }

    [Fact]
    public void Null_source_always_matches() => RoutingExpression.Compile(null, Schema).Expression!.IsAlwaysTrue.ShouldBeTrue();

    [Fact]
    public void Missing_header_does_not_match_either_way()
    {
        Eval("""headers["x-nope"] == "a" """).ShouldBeFalse();
        Eval("""headers["x-nope"] != "a" """).ShouldBeFalse();
        Eval("""!(headers["x-nope"] == "a")""").ShouldBeFalse();
    }

    [Fact]
    public void Unset_variable_does_not_match()
    {
        // complexity_tier is declared but not provided (classification unavailable).
        Eval("""complexity_tier in ["MEDIUM", "COMPLEX"]""").ShouldBeFalse();
        Eval("""complexity_tier == "SIMPLE" """).ShouldBeFalse();
    }

    [Fact]
    public void Short_circuit_decides_despite_missing_operand()
    {
        Eval("""headers["x-nope"] == "a" || budget_used > 10""").ShouldBeTrue();
        Eval("""budget_used > 10 || headers["x-nope"] == "a" """).ShouldBeTrue();
        Eval("""headers["x-nope"] == "a" && budget_used < 10""").ShouldBeFalse();
        Eval("""budget_used < 10 && headers["x-nope"] == "a" """).ShouldBeFalse();
        // Missing combined with a value that does not decide the result stays undecided => no match.
        Eval("""budget_used > 10 && headers["x-nope"] == "a" """).ShouldBeFalse();
        Eval("""!(budget_used > 10 && headers["x-nope"] == "a")""").ShouldBeFalse();
    }

    [Fact]
    public void Division_by_zero_does_not_match()
    {
        Eval("prompt_tokens / 0 > 1").ShouldBeFalse();
        Eval("prompt_tokens % 0 > 1").ShouldBeFalse();
    }

    [Fact]
    public void Precedence_and_grouping()
    {
        Eval("true || false && false").ShouldBeTrue(); // && binds tighter than ||
        Eval("(true || false) && false").ShouldBeFalse();
        Eval("1 + 2 * 3 == 7").ShouldBeTrue();
        Eval("(1 + 2) * 3 == 9").ShouldBeTrue();
        Eval("10 - 2 - 3 == 5").ShouldBeTrue(); // left associative
    }

    [Fact]
    public void Params_of_any_type_are_compared_at_runtime()
    {
        var schema = new ExpressionSchema(new Dictionary<string, ExprType> { ["params"] = ExprType.AnyMap });
        var (expr, errors) = RoutingExpression.Compile("""params["temperature"] > 0.5 && params["stream"] == true""", schema);
        errors.ShouldBeEmpty();
        expr!.Matches(new ExpressionVariables().Set("params", new Dictionary<string, string> { ["temperature"] = "0.9" }))
            .ShouldBeFalse(); // string vs number cannot be compared at runtime => no match
    }

    [Theory]
    [InlineData("budget_used >", "ended unexpectedly")]
    [InlineData("budget_used > 5 &&", "ended unexpectedly")]
    [InlineData("(budget_used > 5", "Expected ')'")]
    [InlineData("budget_used > 5)", "Unexpected ')'")]
    [InlineData("tags[0", "Expected ']'")]
    [InlineData("budget_used > 5 6", "Unexpected '6'")]
    [InlineData("budget_used > @", "Unexpected character")]
    [InlineData("model == 'abc", "Unterminated string")]
    [InlineData("model == 'a\\qb'", "Unsupported escape")]
    [InlineData("budget_used ? 1 : 2", "Unexpected character")]
    [InlineData("in", "Unexpected 'in'")]
    [InlineData("1.", "Expected a name after '.'")]
    public void Syntax_errors_are_reported(string source, string message) => Invalid(source).Message.ShouldContain(message);

    [Theory]
    [InlineData("budget_usd > 1", "Unknown variable 'budget_usd'")]
    [InlineData("headers.x-tier == 'a'", "Unknown variable 'tier'")] // '-' is subtraction, not part of a name
    [InlineData("""model == 5""", "Cannot compare")]
    [InlineData("""budget_used > "a" """, "needs two numbers or two strings")]
    [InlineData("pii_detected > false", "needs two numbers or two strings")]
    [InlineData("""!model""", "'!' needs")]
    [InlineData("-model == 1", "'-' needs a number")]
    [InlineData("model && true", "needs true/false")]
    [InlineData("budget_used", "must be true or false")]
    [InlineData("model", "must be true or false")]
    [InlineData("budget_used.size() > 1", "size() needs")]
    [InlineData("model.foo()", "Unknown function 'foo'")]
    [InlineData("foo(model)", "Unknown function 'foo'")]
    [InlineData("""model.startsWith(1)""", "must be a string")]
    [InlineData("""budget_used.startsWith("a")""", "needs a string")]
    [InlineData("""model.startsWith("a", "b")""", "takes 1 argument")]
    [InlineData("""matches(model)""", "takes a string and a pattern")]
    [InlineData("""model.matches(team_name)""", "must be a string literal")]
    [InlineData("""model in 5""", "needs a list or map")]
    [InlineData("""5 in headers""", "must be a string")]
    [InlineData("model[0] == 'a'", "Cannot index")]
    [InlineData("""tags["a"] == 1""", "must be numbers")]
    [InlineData("""headers[1] == "a" """, "must be strings")]
    [InlineData("budget_used.foo == 1", "has no field")]
    [InlineData("1 + model == 1", "needs numbers")]
    [InlineData("""model.matches("(")""", "Invalid pattern")]
    [InlineData("""model.matches("(?=a)a")""", "Unsupported pattern")]
    public void Type_errors_are_reported(string source, string message) => Invalid(source).Message.ShouldContain(message);

    [Fact]
    public void Error_position_points_at_the_problem()
    {
        const string source = "budget_used > 5 && budget_usd > 1";
        var error = Invalid(source);
        source.Substring(error.Position, error.Length).ShouldBe("budget_usd");
    }

    [Fact]
    public void Regex_pattern_length_is_limited()
    {
        Invalid($"model.matches('{new string('a', 300)}')").Message.ShouldContain("longer than");
    }

    [Fact]
    public void Catastrophic_regex_is_safe()
    {
        // Backtracking engines take exponential time on this; the non-backtracking engine is linear.
        var vars = new ExpressionVariables().Set("model", new string('a', 5000) + "!");
        var (expr, errors) = RoutingExpression.Compile("""model.matches("^(a+)+$")""", Schema);
        errors.ShouldBeEmpty();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        expr!.Matches(vars).ShouldBeFalse();
        sw.ElapsedMilliseconds.ShouldBeLessThan(1000);
    }

    [Fact]
    public void Expression_length_is_limited()
    {
        var source = "true" + string.Concat(Enumerable.Repeat(" && true", 300)); // > 2000 chars
        Invalid(source).Message.ShouldContain("longer than");
    }

    [Fact]
    public void Token_count_is_limited()
    {
        var source = string.Join(" && ", Enumerable.Repeat("a", 400));
        var (expr, errors) = RoutingExpression.Compile(source.Replace("a", "1==1"), Schema);
        expr.ShouldBeNull();
        errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Deep_nesting_is_rejected_without_stack_overflow()
    {
        Invalid(new string('(', 100) + "true" + new string(')', 100)).Message.ShouldContain("nested too deeply");
        Invalid(new string('!', 100) + "true").Message.ShouldContain("nested too deeply");
        Invalid(new string('[', 100) + new string(']', 100)).Message.ShouldContain("nested too deeply");
    }

    [Fact]
    public void Explain_reports_each_comparison()
    {
        var result = Compile("""headers["x-tier"] == "premium" && budget_used > 95""").Explain(Vars());
        result.Matched.ShouldBeFalse();
        result.Trace.Count.ShouldBe(2);
        result.Trace[0].Text.ShouldBe("""headers["x-tier"] == "premium" """.Trim());
        result.Trace[0].Result.ShouldBe(true);
        result.Trace[1].Text.ShouldBe("budget_used > 95");
        result.Trace[1].LeftValue.ShouldBe("92.5");
        result.Trace[1].Result.ShouldBe(false);
    }

    [Fact]
    public void Explain_marks_unevaluable_comparisons()
    {
        var result = Compile("""headers["x-nope"] == "a" """).Explain(Vars());
        result.Matched.ShouldBeFalse();
        result.Trace.Single().Result.ShouldBeNull();
        result.Trace.Single().LeftValue.ShouldBeNull();
    }

    [Fact]
    public void Explain_includes_function_calls_and_empty_is_true()
    {
        var result = Compile("""model.startsWith("claude")""").Explain(Vars());
        result.Matched.ShouldBeTrue();
        result.Trace.Single().Text.ShouldBe("""model.startsWith("claude")""");
        result.Trace.Single().LeftValue.ShouldBe("\"claude-sonnet\"");
        Compile("").Explain(Vars()).Matched.ShouldBeTrue();
    }

    [Fact]
    public void Compiled_expression_is_reusable_across_threads()
    {
        var expr = Compile("""headers["x-tier"] == "premium" && budget_used > 85""");
        Parallel.For(0, 2000, _ => expr.Matches(Vars()).ShouldBeTrue());
    }

    [Fact]
    public void Bifrost_documentation_examples_compile()
    {
        foreach (var source in new[]
        {
            """headers["x-tier"] == "premium" """,
            "budget_used > 85",
            """team_name == "ml-research" && model.startsWith("claude-")""",
            """complexity_tier in ["MEDIUM", "COMPLEX"]""",
            """model == "gpt-4" """,
            "true",
        })
        {
            Compile(source);
        }
    }
}
