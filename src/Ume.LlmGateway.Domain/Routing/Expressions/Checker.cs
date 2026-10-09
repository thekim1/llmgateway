using System.Text.RegularExpressions;

namespace Ume.LlmGateway.Domain.Routing.Expressions;

/// <summary>Static checks: unknown names, wrong operand types, wrong arity and invalid or non-literal regex patterns.</summary>
internal sealed class Checker(ExpressionSchema schema)
{
    private readonly HashSet<string> _referenced = new(StringComparer.Ordinal);

    /// <summary>Variables used by the expression, collected while checking.</summary>
    public IReadOnlySet<string> ReferencedVariables => _referenced;

    internal const int MaxPatternLength = 256;
    internal static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    public ExprType Check(Node node) => node switch
    {
        LiteralNode l => l.Value switch { bool => ExprType.Bool, double => ExprType.Number, _ => ExprType.Text },
        IdentNode i => schema.Variables.TryGetValue(i.Name, out var t)
            ? Reference(i.Name, t)
            : throw new ExpressionException(i.Start, i.End - i.Start, $"Unknown variable '{i.Name}'. Available: {string.Join(", ", schema.Variables.Keys.Order(StringComparer.Ordinal))}."),
        ListNode l => CheckList(l),
        UnaryNode u => CheckUnary(u),
        BinaryNode b => CheckBinary(b),
        IndexNode ix => CheckIndex(ix),
        MemberNode m => CheckMember(m),
        CallNode c => CheckCall(c),
        _ => throw new InvalidOperationException("Unknown node."),
    };

    private ExprType Reference(string name, ExprType type)
    {
        _referenced.Add(name);
        return type;
    }

    private ExprType CheckList(ListNode l)
    {
        foreach (var item in l.Items)
        {
            Check(item);
        }

        return ExprType.List;
    }

    private ExprType CheckUnary(UnaryNode u)
    {
        var t = Check(u.Operand);
        if (u.Op == "!")
        {
            Require(u.Operand, t, ExprType.Bool, "'!' needs a true/false value");
            return ExprType.Bool;
        }

        Require(u.Operand, t, ExprType.Number, "'-' needs a number");
        return ExprType.Number;
    }

    private ExprType CheckBinary(BinaryNode b)
    {
        var l = Check(b.Left);
        var r = Check(b.Right);
        switch (b.Op)
        {
            case "&&" or "||":
                Require(b.Left, l, ExprType.Bool, $"'{b.Op}' needs true/false values");
                Require(b.Right, r, ExprType.Bool, $"'{b.Op}' needs true/false values");
                return ExprType.Bool;

            case "==" or "!=":
                if (l != ExprType.Any && r != ExprType.Any && l != r)
                {
                    throw new ExpressionException(b.Start, b.End - b.Start, $"Cannot compare {Name(l)} with {Name(r)}.");
                }

                return ExprType.Bool;

            case "<" or "<=" or ">" or ">=":
                if (l != r && l != ExprType.Any && r != ExprType.Any || (l is not (ExprType.Number or ExprType.Text or ExprType.Any)))
                {
                    throw new ExpressionException(b.Start, b.End - b.Start, $"'{b.Op}' needs two numbers or two strings, not {Name(l)} and {Name(r)}.");
                }

                return ExprType.Bool;

            case "in":
                if (r is ExprType.TextMap or ExprType.AnyMap)
                {
                    Require(b.Left, l, ExprType.Text, "Key of 'in' on a map must be a string");
                }
                else if (r is not (ExprType.List or ExprType.Any))
                {
                    throw new ExpressionException(b.Right.Start, b.Right.End - b.Right.Start, $"'in' needs a list or map on the right, not {Name(r)}.");
                }

                return ExprType.Bool;

            case "+":
                if (l == ExprType.Text && r == ExprType.Text)
                {
                    return ExprType.Text;
                }

                goto default;

            default:
                Require(b.Left, l, ExprType.Number, $"'{b.Op}' needs numbers");
                Require(b.Right, r, ExprType.Number, $"'{b.Op}' needs numbers");
                return ExprType.Number;
        }
    }

    private ExprType CheckIndex(IndexNode ix)
    {
        var t = Check(ix.Target);
        var k = Check(ix.Key);
        switch (t)
        {
            case ExprType.TextMap or ExprType.AnyMap:
                Require(ix.Key, k, ExprType.Text, "Map keys must be strings");
                return t == ExprType.TextMap ? ExprType.Text : ExprType.Any;
            case ExprType.List:
                Require(ix.Key, k, ExprType.Number, "List indexes must be numbers");
                return ExprType.Any;
            case ExprType.Any:
                return ExprType.Any;
            default:
                throw new ExpressionException(ix.Start, ix.End - ix.Start, $"Cannot index into {Name(t)}.");
        }
    }

    private ExprType CheckMember(MemberNode m)
    {
        var t = Check(m.Target);
        return t switch
        {
            ExprType.TextMap => ExprType.Text,
            ExprType.AnyMap or ExprType.Any => ExprType.Any,
            _ => throw new ExpressionException(m.Start, m.End - m.Start, $"{Name(t)} has no field '{m.Name}'."),
        };
    }

    private ExprType CheckCall(CallNode c)
    {
        // matches(s, re) / size(x) are also callable as s.matches(re) / x.size().
        var args = c.Target is null ? c.Args : [c.Target, .. c.Args];
        switch (c.Name)
        {
            case "startsWith" or "endsWith" or "contains" when c.Target is not null:
                ExpectArgs(c, 1);
                Require(c.Target, Check(c.Target), ExprType.Text, $"{c.Name}() needs a string");
                Require(c.Args[0], Check(c.Args[0]), ExprType.Text, $"{c.Name}() argument must be a string");
                return ExprType.Bool;

            case "matches":
                if (args.Count != 2)
                {
                    throw new ExpressionException(c.Start, c.End - c.Start, "matches() takes a string and a pattern.");
                }

                Require(args[0], Check(args[0]), ExprType.Text, "matches() needs a string");
                if (args[1] is not LiteralNode { Value: string pattern })
                {
                    throw new ExpressionException(args[1].Start, args[1].End - args[1].Start, "The pattern of matches() must be a string literal.");
                }

                c.Pattern = CompilePattern(pattern, args[1]);
                return ExprType.Bool;

            case "size":
                if (args.Count != 1)
                {
                    throw new ExpressionException(c.Start, c.End - c.Start, "size() takes one value.");
                }

                var st = Check(args[0]);
                if (st is ExprType.Bool or ExprType.Number)
                {
                    throw new ExpressionException(args[0].Start, args[0].End - args[0].Start, $"size() needs a string, list or map, not {Name(st)}.");
                }

                return ExprType.Number;

            default:
                throw new ExpressionException(c.Start, c.End - c.Start, $"Unknown function '{c.Name}'. Available: startsWith, endsWith, contains, matches, size.");
        }
    }

    private static Regex CompilePattern(string pattern, Node at)
    {
        if (pattern.Length > MaxPatternLength)
        {
            throw new ExpressionException(at.Start, at.End - at.Start, $"Pattern is longer than {MaxPatternLength} characters.");
        }

        try
        {
            // NonBacktracking guarantees linear-time matching, so a pattern cannot cause catastrophic backtracking.
            return new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new ExpressionException(at.Start, at.End - at.Start, $"Invalid pattern: {ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            throw new ExpressionException(at.Start, at.End - at.Start, $"Unsupported pattern (no lookarounds or backreferences): {ex.Message}");
        }
    }

    private static void ExpectArgs(CallNode c, int count)
    {
        if (c.Args.Count != count)
        {
            throw new ExpressionException(c.Start, c.End - c.Start, $"{c.Name}() takes {count} argument(s).");
        }
    }

    private static void Require(Node at, ExprType actual, ExprType expected, string message)
    {
        if (actual != expected && actual != ExprType.Any)
        {
            throw new ExpressionException(at.Start, at.End - at.Start, $"{message}, not {Name(actual)}.");
        }
    }

    private static string Name(ExprType t) => t switch
    {
        ExprType.Bool => "a boolean",
        ExprType.Number => "a number",
        ExprType.Text => "a string",
        ExprType.List => "a list",
        ExprType.TextMap or ExprType.AnyMap => "a map",
        _ => "a value",
    };
}
