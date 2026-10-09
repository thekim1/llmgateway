namespace Ume.LlmGateway.Domain.Routing.Expressions;

/// <summary>
/// A compiled routing condition written in a CEL subset, e.g. <c>headers["x-tier"] == "premium" &amp;&amp; budget_used &gt; 85</c>.
/// Compile once (validation and parsing happen here), evaluate many times. Instances are immutable and thread-safe.
/// </summary>
public sealed class RoutingExpression
{
    public const int MaxLength = 2000;

    private readonly Node? _root;

    private RoutingExpression(string source, Node? root, IReadOnlySet<string> variables)
    {
        Source = source;
        _root = root;
        Variables = variables;
    }

    public string Source { get; }

    /// <summary>The variables the expression refers to.</summary>
    public IReadOnlySet<string> Variables { get; }

    /// <summary>True when the expression is empty, which always matches.</summary>
    public bool IsAlwaysTrue => _root is null;

    /// <summary>Compiles and checks an expression. Returns errors (never throws) when it is invalid.</summary>
    public static (RoutingExpression? Expression, IReadOnlyList<ExpressionError> Errors) Compile(string? source, ExpressionSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        source ??= "";
        if (string.IsNullOrWhiteSpace(source))
        {
            return (new RoutingExpression("", null, new HashSet<string>()), []);
        }

        if (source.Length > MaxLength)
        {
            return (null, [new ExpressionError(MaxLength, 1, $"The expression is longer than {MaxLength} characters.")]);
        }

        try
        {
            var root = Parser.Parse(source);
            var checker = new Checker(schema);
            var type = checker.Check(root);
            if (type is not (ExprType.Bool or ExprType.Any))
            {
                throw new ExpressionException(root.Start, root.End - root.Start, "The condition must be true or false.");
            }

            return (new RoutingExpression(source, root, checker.ReferencedVariables), []);
        }
        catch (ExpressionException ex)
        {
            return (null, [new ExpressionError(ex.Position, ex.Length, ex.Message)]);
        }
    }

    /// <summary>Evaluates against the given variables. An expression that cannot be evaluated counts as not matching.</summary>
    public bool Matches(ExpressionVariables variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        return _root is null || new Evaluator(Source, variables.Values, null).Matches(_root);
    }

    /// <summary>Like <see cref="Matches"/> but also returns each comparison and call that was evaluated, to explain the outcome.</summary>
    public EvaluationResult Explain(ExpressionVariables variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        if (_root is null)
        {
            return new EvaluationResult(true, []);
        }

        var trace = new List<TraceEntry>();
        var matched = new Evaluator(Source, variables.Values, trace).Matches(_root);
        return new EvaluationResult(matched, trace);
    }
}
