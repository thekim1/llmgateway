namespace Ume.LlmGateway.Domain.Routing.Expressions;

/// <summary>Static types known to the expression checker. <c>Any</c> is unknown and defers checks to runtime.</summary>
public enum ExprType
{
    Any,
    Bool,
    Number,
    Text,
    List,
    /// <summary>Map with string keys and string values (e.g. headers).</summary>
    TextMap,
    /// <summary>Map with string keys and values of any type (e.g. request parameters).</summary>
    AnyMap,
}

/// <summary>The variables an expression may reference, and their types.</summary>
public sealed class ExpressionSchema(IReadOnlyDictionary<string, ExprType> variables)
{
    public IReadOnlyDictionary<string, ExprType> Variables { get; } = new Dictionary<string, ExprType>(variables, StringComparer.Ordinal);
}

/// <summary>A problem found while compiling an expression. <c>Position</c> and <c>Length</c> index into the source text.</summary>
public sealed record ExpressionError(int Position, int Length, string Message);

/// <summary>One evaluated comparison or call, for explaining why a condition matched or not. <c>Result</c> null = could not be evaluated.</summary>
public sealed record TraceEntry(string Text, string? LeftValue, bool? Result);

/// <summary>Outcome of evaluating a condition. <c>Matched</c> is false when the expression is false or cannot be evaluated.</summary>
public sealed record EvaluationResult(bool Matched, IReadOnlyList<TraceEntry> Trace);

/// <summary>Variable values for one evaluation. Values are normalised on assignment so evaluation never copies.</summary>
public sealed class ExpressionVariables
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    internal IReadOnlyDictionary<string, object?> Values => _values;

    public ExpressionVariables Set(string name, bool value) => Put(name, value);

    /// <summary>Sets the variable only when a value is present; otherwise it stays unset.</summary>
    public ExpressionVariables SetOptional(string name, double? value) => value is { } v ? Put(name, v) : this;

    public ExpressionVariables SetOptional(string name, long? value) => value is { } v ? Put(name, (double)v) : this;

    public ExpressionVariables SetOptional(string name, bool? value) => value is { } v ? Put(name, v) : this;

    public ExpressionVariables Set(string name, double value) => Put(name, value);

    public ExpressionVariables Set(string name, string? value) => value is null ? this : Put(name, value);

    public ExpressionVariables Set(string name, IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Put(name, values.Cast<object?>().ToList());
    }

    /// <summary>Sets a map variable; key lookup is case-insensitive.</summary>
    public ExpressionVariables Set(string name, IEnumerable<KeyValuePair<string, string>> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var copy = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in map)
        {
            copy[k] = v;
        }

        return Put(name, copy);
    }

    /// <summary>Sets a map of mixed values (e.g. request parameters). Booleans, strings and numbers are kept; other values are dropped.</summary>
    public ExpressionVariables SetMap(string name, IEnumerable<KeyValuePair<string, object?>> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var copy = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in map)
        {
            object? normalised = v switch
            {
                bool or string => v,
                byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture),
                _ => null,
            };
            if (normalised is not null)
            {
                copy[k] = normalised;
            }
        }

        return Put(name, copy);
    }

    private ExpressionVariables Put(string name, object? value)
    {
        _values[name] = value;
        return this;
    }
}
