using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Ume.LlmGateway.Domain.Services;

/// <summary>
/// Names of MCP tools on the aggregated <c>/mcp</c> and on the LLM path: <c>{slug}__{tool}</c> with every character
/// outside <c>[A-Za-z0-9_-]</c> replaced by <c>_</c>, at most <see cref="MaxLength"/> characters, so every provider
/// accepts it as a function name. <c>/mcp/{slug}</c> keeps the server's own names.
/// </summary>
public static partial class McpToolNames
{
    public const int MaxLength = 64;
    public const string Separator = "__";
    private const int HashLength = 8;

    public const int MaxSlugLength = 24;

    /// <summary>
    /// A server slug: lower-case letters and digits, starting with a letter, with single underscores between them, at most
    /// <see cref="MaxSlugLength"/> characters. No <c>__</c> and no trailing <c>_</c>, so the first <c>__</c> in an exposed name ends the slug.
    /// </summary>
    public static bool IsValidSlug(string slug)
    {
        ArgumentNullException.ThrowIfNull(slug);
        return slug.Length <= MaxSlugLength && SlugPattern().IsMatch(slug);
    }

    /// <summary>
    /// The exposed name of <paramref name="toolName"/> on the server <paramref name="slug"/>. A name over
    /// <see cref="MaxLength"/> is cut and ends in <c>_</c> plus 8 hex characters of the SHA-256 of the unsanitised name,
    /// so tools sharing a long prefix stay apart. Two tools can still sanitise to the same name; callers must detect that.
    /// </summary>
    public static string Exposed(string slug, string toolName)
    {
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentNullException.ThrowIfNull(toolName);
        var raw = slug + Separator + toolName;
        var sanitised = string.Create(raw.Length, raw, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = IsAllowed(source[i]) ? source[i] : '_';
            }
        });
        if (sanitised.Length <= MaxLength)
        {
            return sanitised;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..HashLength];
        return string.Concat(sanitised.AsSpan(0, MaxLength - HashLength - 1), "_", hash);
    }

    private static bool IsAllowed(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-';

    [GeneratedRegex(@"^[a-z][a-z0-9]*(?:_[a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
}
