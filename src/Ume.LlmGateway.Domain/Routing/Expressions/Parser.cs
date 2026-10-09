namespace Ume.LlmGateway.Domain.Routing.Expressions;

/// <summary>
/// Recursive-descent parser for a CEL subset: literals, variables, lists, indexing and field access,
/// calls, <c>! - * / % + -</c>, comparisons, <c>in</c>, <c>&amp;&amp;</c> and <c>||</c>.
/// </summary>
internal sealed class Parser(List<Token> tokens)
{
    private const int MaxDepth = 32;
    private int _pos;
    private int _depth;

    public static Node Parse(string source)
    {
        var parser = new Parser(Lexer.Tokenize(source));
        var node = parser.Or();
        if (parser.Peek.Kind != TokenKind.End)
        {
            throw parser.Error($"Unexpected '{parser.Peek.Text}'.");
        }

        return node;
    }

    private Token Peek => tokens[_pos];

    private Token Next() => tokens[_pos++];

    private bool IsPunct(string text) => Peek.Kind == TokenKind.Punct && Peek.Text == text;

    private bool IsIdent(string text) => Peek.Kind == TokenKind.Ident && Peek.Text == text;

    private ExpressionException Error(string message) => new(Peek.Start, Math.Max(1, Peek.End - Peek.Start), message);

    private Token Expect(string punct)
    {
        if (!IsPunct(punct))
        {
            throw Error(Peek.Kind == TokenKind.End ? $"Expected '{punct}' but the expression ended." : $"Expected '{punct}' but found '{Peek.Text}'.");
        }

        return Next();
    }

    private Node Or()
    {
        var left = And();
        while (IsPunct("||"))
        {
            Next();
            var right = And();
            left = new BinaryNode("||", left, right, left.Start, right.End);
        }

        return left;
    }

    private Node And()
    {
        var left = Relation();
        while (IsPunct("&&"))
        {
            Next();
            var right = Relation();
            left = new BinaryNode("&&", left, right, left.Start, right.End);
        }

        return left;
    }

    private Node Relation()
    {
        var left = Add();
        while (true)
        {
            string? op = null;
            if (Peek.Kind == TokenKind.Punct && Peek.Text is "==" or "!=" or "<" or "<=" or ">" or ">=")
            {
                op = Peek.Text;
            }
            else if (IsIdent("in"))
            {
                op = "in";
            }

            if (op is null)
            {
                return left;
            }

            Next();
            var right = Add();
            left = new BinaryNode(op, left, right, left.Start, right.End);
        }
    }

    private Node Add()
    {
        var left = Mul();
        while (IsPunct("+") || IsPunct("-"))
        {
            var op = Next().Text;
            var right = Mul();
            left = new BinaryNode(op, left, right, left.Start, right.End);
        }

        return left;
    }

    private Node Mul()
    {
        var left = Unary();
        while (IsPunct("*") || IsPunct("/") || IsPunct("%"))
        {
            var op = Next().Text;
            var right = Unary();
            left = new BinaryNode(op, left, right, left.Start, right.End);
        }

        return left;
    }

    private Node Unary()
    {
        if (IsPunct("!") || IsPunct("-"))
        {
            var op = Next();
            Enter();
            var operand = Unary();
            _depth--;
            return new UnaryNode(op.Text, operand, op.Start, operand.End);
        }

        return Postfix();
    }

    private void Enter()
    {
        if (++_depth > MaxDepth)
        {
            throw Error("Expression is nested too deeply.");
        }
    }

    private Node Postfix()
    {
        var node = Primary();
        while (true)
        {
            if (IsPunct("."))
            {
                Next();
                if (Peek.Kind != TokenKind.Ident)
                {
                    throw Error("Expected a name after '.'.");
                }

                var name = Next();
                if (IsPunct("("))
                {
                    var (args, end) = Arguments();
                    node = new CallNode(node, name.Text, args, node.Start, end);
                }
                else
                {
                    node = new MemberNode(node, name.Text, node.Start, name.End);
                }
            }
            else if (IsPunct("["))
            {
                Next();
                Enter();
                var key = Or();
                _depth--;
                var close = Expect("]");
                node = new IndexNode(node, key, node.Start, close.End);
            }
            else
            {
                return node;
            }
        }
    }

    private (List<Node> Args, int End) Arguments()
    {
        Expect("(");
        Enter();
        var args = new List<Node>();
        if (!IsPunct(")"))
        {
            do
            {
                args.Add(Or());
            }
            while (IsPunct(",") && Next().Kind == TokenKind.Punct);
        }

        _depth--;
        return (args, Expect(")").End);
    }

    private Node Primary()
    {
        var t = Peek;
        switch (t.Kind)
        {
            case TokenKind.Number:
            case TokenKind.String:
                Next();
                return new LiteralNode(t.Value!, t.Start, t.End);

            case TokenKind.Ident when t.Text is "true" or "false":
                Next();
                return new LiteralNode(t.Text == "true", t.Start, t.End);

            case TokenKind.Ident when t.Text == "in":
                throw Error("Unexpected 'in'.");

            case TokenKind.Ident:
                Next();
                if (IsPunct("("))
                {
                    var (args, end) = Arguments();
                    return new CallNode(null, t.Text, args, t.Start, end);
                }

                return new IdentNode(t.Text, t.Start, t.End);

            case TokenKind.Punct when t.Text == "(":
            {
                Next();
                Enter();
                var inner = Or();
                _depth--;
                Expect(")");
                return inner;
            }

            case TokenKind.Punct when t.Text == "[":
            {
                Next();
                Enter();
                var items = new List<Node>();
                if (!IsPunct("]"))
                {
                    do
                    {
                        items.Add(Or());
                    }
                    while (IsPunct(",") && Next().Kind == TokenKind.Punct);
                }

                _depth--;
                var close = Expect("]");
                return new ListNode(items, t.Start, close.End);
            }

            case TokenKind.End:
                throw Error("The expression ended unexpectedly.");

            default:
                throw Error($"Unexpected '{t.Text}'.");
        }
    }
}
