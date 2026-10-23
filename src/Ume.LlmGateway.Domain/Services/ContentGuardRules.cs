using System.Text.RegularExpressions;

namespace Ume.LlmGateway.Domain.Services;

/// <summary>The output format of the guard model.</summary>
public enum GuardModelFormat
{
    /// <summary>Meta Llama Guard: <c>safe</c>, or <c>unsafe</c> and a line of categories (<c>S1,S10</c>).</summary>
    LlamaGuard = 0,

    /// <summary>IBM Granite Guardian: <c>Yes</c> (a risk) or <c>No</c>.</summary>
    GraniteGuardian = 1,
}

/// <summary>What a guard model said: flagged or not, and its categories when it gives them.</summary>
public readonly record struct GuardVerdict(bool Flagged, string? Categories)
{
    public static GuardVerdict Safe { get; } = new(false, null);

    /// <summary>Reads the model's answer; anything it cannot read counts as not flagged (the caller logs it).</summary>
    public static GuardVerdict? Parse(string? answer, GuardModelFormat format)
    {
        var lines = (answer ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            return null;
        }

        var first = lines[0].ToUpperInvariant();
        return format switch
        {
            GuardModelFormat.GraniteGuardian when first.StartsWith("YES", StringComparison.Ordinal) => new GuardVerdict(true, "risk"),
            GuardModelFormat.GraniteGuardian when first.StartsWith("NO", StringComparison.Ordinal) => Safe,
            GuardModelFormat.LlamaGuard when first == "SAFE" => Safe,
            GuardModelFormat.LlamaGuard when first == "UNSAFE" => new GuardVerdict(true,
                lines.Length > 1 ? Regex.Replace(lines[1], @"[^A-Za-z0-9,]", string.Empty, RegexOptions.None, TimeSpan.FromMilliseconds(50)) : "unsafe"),
            _ => null,
        };
    }
}

/// <summary>
/// In-process content rules: well-known prompt-injection phrasings (English and Swedish) and an administrator's own
/// patterns. Cheap (a few regular expressions over the request text) and deterministic, but only a first line of defence:
/// a determined attacker rewords. The guard model is the stronger check.
/// </summary>
public sealed partial class ContentGuardRules
{
    // Instructions to drop the system prompt or reveal it, as attacks phrase them; matched case-insensitively.
    [GeneratedRegex("""
        \b(ignore|disregard|forget|override)\b[^.\n]{0,40}\b(previous|prior|above|earlier|all|system)\b[^.\n]{0,20}\b(instructions?|prompts?|rules)\b
        |\b(reveal|print|show|repeat)\b[^.\n]{0,20}\b(your|the)\b[^.\n]{0,10}\b(system\s+prompt|instructions|hidden\s+prompt)\b
        |\byou\s+are\s+now\s+(dan|in\s+developer\s+mode|jailbroken)\b
        |\b(ignorera|glöm|strunta\s+i)\b[^.\n]{0,40}\b(tidigare|föregående|alla|ovanstående)\b[^.\n]{0,20}\b(instruktioner(na)?|regler(na)?)\b
        |\b(visa|avslöja|skriv\s+ut)\b[^.\n]{0,20}\b(din|dina|systemprompten|systemprompt)\b[^.\n]{0,20}\b(systemprompt(en)?|instruktioner(na)?)\b
        """, RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant, 250)]
    private static partial Regex PromptInjection();

    private readonly bool _promptInjection;
    private readonly IReadOnlyList<Regex> _blocked;

    /// <param name="promptInjection">Check the built-in prompt-injection phrasings.</param>
    /// <param name="blockedPatterns">An administrator's regular expressions (case-insensitive); a match is <c>blocked_pattern</c>.</param>
    public ContentGuardRules(bool promptInjection, IEnumerable<string> blockedPatterns)
    {
        ArgumentNullException.ThrowIfNull(blockedPatterns);
        _promptInjection = promptInjection;
        _blocked = [.. blockedPatterns.Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)))];
    }

    /// <summary>The categories the text matches (<c>prompt_injection</c>, <c>blocked_pattern</c>); empty when none.</summary>
    public IReadOnlyList<string> Check(string text, bool includeInjection = true)
    {
        ArgumentNullException.ThrowIfNull(text);
        var found = new List<string>(2);
        try
        {
            if (includeInjection && _promptInjection && PromptInjection().IsMatch(text))
            {
                found.Add("prompt_injection");
            }

            if (_blocked.Any(r => r.IsMatch(text)))
            {
                found.Add("blocked_pattern");
            }
        }
        catch (RegexMatchTimeoutException)
        {
            found.Add("rule_timeout"); // pathological input: treat as flagged rather than let it through unchecked
        }

        return found;
    }
}
