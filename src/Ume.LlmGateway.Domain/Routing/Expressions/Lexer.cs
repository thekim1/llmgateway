using System.Globalization;
using System.Text;

namespace Ume.LlmGateway.Domain.Routing.Expressions;

internal sealed class ExpressionException(int position, int length, string message) : Exception(message)
{
    public int Position { get; } = position;
    public int Length { get; } = length;
}

internal enum TokenKind { Number, String, Ident, Punct, End }

internal readonly record struct Token(TokenKind Kind, string Text, object? Value, int Start, int End);

internal static class Lexer
{
    private const int MaxTokens = 500;

    public static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (true)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i]))
            {
                i++;
            }

            if (i >= s.Length)
            {
                tokens.Add(new Token(TokenKind.End, "", null, s.Length, s.Length));
                return tokens;
            }

            if (tokens.Count >= MaxTokens)
            {
                throw new ExpressionException(i, 1, "Expression is too long.");
            }

            var c = s[i];
            var start = i;
            if (char.IsAsciiDigit(c))
            {
                while (i < s.Length && char.IsAsciiDigit(s[i]))
                {
                    i++;
                }

                if (i + 1 < s.Length && s[i] == '.' && char.IsAsciiDigit(s[i + 1]))
                {
                    i++;
                    while (i < s.Length && char.IsAsciiDigit(s[i]))
                    {
                        i++;
                    }
                }

                var text = s[start..i];
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    throw new ExpressionException(start, i - start, $"Invalid number '{text}'.");
                }

                tokens.Add(new Token(TokenKind.Number, text, number, start, i));
            }
            else if (char.IsAsciiLetter(c) || c == '_')
            {
                while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] == '_'))
                {
                    i++;
                }

                var text = s[start..i];
                if ((text is "r" or "R") && i < s.Length && s[i] is '"' or '\'')
                {
                    tokens.Add(ReadString(s, i, raw: true, start));
                    i = tokens[^1].End;
                }
                else
                {
                    tokens.Add(new Token(TokenKind.Ident, text, null, start, i));
                }
            }
            else if (c is '"' or '\'')
            {
                tokens.Add(ReadString(s, i, raw: false, start));
                i = tokens[^1].End;
            }
            else
            {
                var two = i + 1 < s.Length ? s.Substring(i, 2) : "";
                if (two is "&&" or "||" or "==" or "!=" or "<=" or ">=")
                {
                    tokens.Add(new Token(TokenKind.Punct, two, null, i, i + 2));
                    i += 2;
                }
                else if ("<>!+-*/%()[],.".Contains(c, StringComparison.Ordinal))
                {
                    tokens.Add(new Token(TokenKind.Punct, c.ToString(), null, i, i + 1));
                    i++;
                }
                else
                {
                    throw new ExpressionException(i, 1, $"Unexpected character '{c}'.");
                }
            }
        }
    }

    private static Token ReadString(string s, int quotePos, bool raw, int tokenStart)
    {
        var quote = s[quotePos];
        var sb = new StringBuilder();
        var i = quotePos + 1;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == quote)
            {
                return new Token(TokenKind.String, s[tokenStart..(i + 1)], sb.ToString(), tokenStart, i + 1);
            }

            if (c == '\n')
            {
                break;
            }

            if (c == '\\' && !raw)
            {
                if (i + 1 >= s.Length)
                {
                    break;
                }

                var e = s[i + 1];
                sb.Append(e switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '\\' => '\\',
                    '"' => '"',
                    '\'' => '\'',
                    _ => throw new ExpressionException(i, 2, $"Unsupported escape '\\{e}'."),
                });
                i += 2;
                continue;
            }

            sb.Append(c);
            i++;
        }

        throw new ExpressionException(tokenStart, s.Length - tokenStart, "Unterminated string.");
    }
}
