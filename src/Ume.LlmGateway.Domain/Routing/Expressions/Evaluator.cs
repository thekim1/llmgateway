using System.Globalization;
using System.Text.RegularExpressions;

namespace Ume.LlmGateway.Domain.Routing.Expressions;

/// <summary>
/// Evaluates a checked expression tree. There are no loops or side effects, so cost is bounded by expression size.
/// Anything that cannot be evaluated (missing header, unset variable, division by zero, type mismatch at runtime)
/// yields <see cref="Missing"/>, which propagates and finally counts as "no match". As in CEL, <c>false &amp;&amp; x</c>
/// and <c>true || x</c> are decided even if <c>x</c> is missing.
/// </summary>
internal sealed class Evaluator(string source, IReadOnlyDictionary<string, object?> variables, List<TraceEntry>? trace)
{
    internal sealed class MissingValue
    {
        public static readonly MissingValue Instance = new();
    }

    private static readonly object Missing = MissingValue.Instance;

    public bool Matches(Node root) => Eval(root) is true;

    private object Eval(Node node)
    {
        switch (node)
        {
            case LiteralNode l:
                return l.Value;
            case IdentNode i:
                return variables.TryGetValue(i.Name, out var v) && v is not null ? v : Missing;
            case ListNode l:
                return l.Items.Select(Eval).ToList();
            case UnaryNode u:
                return Unary(u);
            case BinaryNode b:
                return Binary(b);
            case IndexNode ix:
                return Index(Eval(ix.Target), Eval(ix.Key));
            case MemberNode m:
                return Index(Eval(m.Target), m.Name);
            case CallNode c:
                return Call(c);
            default:
                return Missing;
        }
    }

    private object Unary(UnaryNode u)
    {
        var v = Eval(u.Operand);
        return u.Op switch
        {
            "!" when v is bool b => !b,
            "-" when v is double d => -d,
            _ => Missing,
        };
    }

    private object Binary(BinaryNode b)
    {
        if (b.Op is "&&" or "||")
        {
            return Logic(b);
        }

        var left = Eval(b.Left);
        var right = Eval(b.Right);
        object result = (left, right) switch
        {
            (MissingValue, _) or (_, MissingValue) => Missing,
            _ => b.Op switch
            {
                "==" => Equal(left, right),
                "!=" => !Equal(left, right),
                "in" => In(left, right),
                "<" or "<=" or ">" or ">=" => Compare(b.Op, left, right),
                _ => Arithmetic(b.Op, left, right),
            },
        };

        if (b.Op is "==" or "!=" or "<" or "<=" or ">" or ">=" or "in")
        {
            Record(b, left, result);
        }

        return result;
    }

    private object Logic(BinaryNode b)
    {
        var and = b.Op == "&&";
        var left = Eval(b.Left);
        if (left is bool lb && lb != and)
        {
            return lb; // false && _ => false, true || _ => true
        }

        var right = Eval(b.Right);
        if (right is bool rb && rb != and)
        {
            return rb;
        }

        return left is bool && right is bool ? and : Missing;
    }

    private static bool Equal(object left, object right) => left switch
    {
        string s when right is string t => string.Equals(s, t, StringComparison.Ordinal),
        double d when right is double e => d == e,
        bool x when right is bool y => x == y,
        _ => false,
    };

    private static object In(object left, object right) => right switch
    {
        List<object?> list => list.Any(item => item is not null && Equal(left, item)),
        Dictionary<string, object?> map => left is string key ? map.ContainsKey(key) : Missing,
        _ => Missing,
    };

    private static object Compare(string op, object left, object right)
    {
        int c;
        switch (left, right)
        {
            case (double a, double b):
                c = a.CompareTo(b);
                break;
            case (string a, string b):
                c = string.CompareOrdinal(a, b);
                break;
            default:
                return Missing;
        }

        return op switch { "<" => c < 0, "<=" => c <= 0, ">" => c > 0, _ => c >= 0 };
    }

    private static object Arithmetic(string op, object left, object right)
    {
        if (op == "+" && left is string s && right is string t)
        {
            return s + t;
        }

        if (left is not double a || right is not double b)
        {
            return Missing;
        }

        return op switch
        {
            "+" => a + b,
            "-" => a - b,
            "*" => a * b,
            "/" when b != 0 => a / b,
            "%" when b != 0 => a % b,
            _ => Missing,
        };
    }

    private static object Index(object target, object key) => (target, key) switch
    {
        (Dictionary<string, object?> map, string k) => map.TryGetValue(k, out var v) && v is not null ? v : Missing,
        (List<object?> list, double d) when d >= 0 && d < list.Count && d == Math.Floor(d) => list[(int)d] ?? Missing,
        _ => Missing,
    };

    private object Call(CallNode c)
    {
        var args = c.Target is null ? c.Args : [c.Target, .. c.Args];
        var values = args.Select(Eval).ToList();
        if (values.Any(v => v is MissingValue))
        {
            return c.Name == "size" ? Missing : Record(c, values[0], Missing);
        }

        object result = c.Name switch
        {
            "startsWith" when values[0] is string s && values[1] is string p => s.StartsWith(p, StringComparison.Ordinal),
            "endsWith" when values[0] is string s && values[1] is string p => s.EndsWith(p, StringComparison.Ordinal),
            "contains" when values[0] is string s && values[1] is string p => s.Contains(p, StringComparison.Ordinal),
            "matches" when values[0] is string s => IsMatch(c.Pattern!, s),
            "size" => values[0] switch
            {
                string s => (double)s.EnumerateRunes().Count(),
                List<object?> l => (double)l.Count,
                Dictionary<string, object?> m => (double)m.Count,
                _ => Missing,
            },
            _ => Missing,
        };
        return c.Name == "size" ? result : Record(c, values[0], result);
    }

    private static object IsMatch(Regex pattern, string input)
    {
        try
        {
            return pattern.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return Missing;
        }
    }

    private object Record(Node node, object? left, object result)
    {
        trace?.Add(new TraceEntry(source[node.Start..node.End], left is null or MissingValue ? null : Format(left), result is bool b ? b : null));
        return result;
    }

    private static string Format(object v)
    {
        var text = v switch
        {
            string s => $"\"{s}\"",
            bool b => b ? "true" : "false",
            double d => d.ToString("0.##", CultureInfo.InvariantCulture),
            List<object?> l => $"list({l.Count})",
            _ => "map",
        };
        return text.Length > 80 ? text[..80] + "…" : text;
    }
}
