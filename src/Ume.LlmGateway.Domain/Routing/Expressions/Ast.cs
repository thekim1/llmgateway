namespace Ume.LlmGateway.Domain.Routing.Expressions;

internal abstract record Node(int Start, int End);

internal sealed record LiteralNode(object Value, int Start, int End) : Node(Start, End);

internal sealed record IdentNode(string Name, int Start, int End) : Node(Start, End);

internal sealed record ListNode(IReadOnlyList<Node> Items, int Start, int End) : Node(Start, End);

internal sealed record UnaryNode(string Op, Node Operand, int Start, int End) : Node(Start, End);

internal sealed record BinaryNode(string Op, Node Left, Node Right, int Start, int End) : Node(Start, End);

internal sealed record IndexNode(Node Target, Node Key, int Start, int End) : Node(Start, End);

/// <summary><c>target.name</c> on a map, equivalent to <c>target["name"]</c>.</summary>
internal sealed record MemberNode(Node Target, string Name, int Start, int End) : Node(Start, End);

/// <summary>Global call (<c>Target</c> null) or member call (<c>target.name(args)</c>). <c>Pattern</c> is the precompiled regex for matches().</summary>
internal sealed record CallNode(Node? Target, string Name, IReadOnlyList<Node> Args, int Start, int End) : Node(Start, End)
{
    public System.Text.RegularExpressions.Regex? Pattern { get; set; }
}
