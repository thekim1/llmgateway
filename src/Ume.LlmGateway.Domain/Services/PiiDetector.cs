using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ume.LlmGateway.Domain.Services;

public readonly record struct PiiMatch(PiiCategory Category, int Start, int Length);

/// <summary>
/// Lightweight, deterministic PII detector tuned for Swedish public-sector data. Matches are validated
/// (Luhn for personnummer, real calendar dates, mod-97 for IBAN) to keep false positives low.
/// Pluggable: a future NER/Presidio based detector can implement the same contract.
/// </summary>
public static partial class PiiDetector
{
    [GeneratedRegex(@"(?<![\d])(?<century>(?:18|19|20))?(?<yy>\d{2})(?<mm>\d{2})(?<dd>\d{2})(?<sep>[-+]?)(?<num>\d{3})(?<check>\d)(?![\d])", RegexOptions.CultureInvariant, 250)]
    private static partial Regex PersonnummerRegex();

    [GeneratedRegex(@"(?<![\w.%+-])[A-Za-z0-9._%+-]{1,64}@[A-Za-z0-9.-]{1,253}\.[A-Za-z]{2,24}(?![\w])", RegexOptions.CultureInvariant, 250)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?<![\d+])(?:(?:\+46|0046)[\s-]?\(?0?\)?[\s-]?[1-9]\d{0,2}|0[1-9]\d{0,2})[\s-]?\d{2,3}[\s-]?\d{2}[\s-]?\d{2,3}(?![\d])", RegexOptions.CultureInvariant, 250)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Z]{2}\d{2}(?:[ ]?[A-Z0-9]{4}){2,7}(?:[ ]?[A-Z0-9]{1,3})?(?![A-Za-z0-9])", RegexOptions.CultureInvariant, 250)]
    private static partial Regex IbanRegex();

    public static IReadOnlyList<PiiMatch> Detect(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        // Personnummer, IBAN and phone numbers all need an ASCII digit and an e-mail address needs '@': one vectorised
        // scan lets most prose skip the regexes entirely. The result is identical.
        var hasDigit = text.AsSpan().IndexOfAnyInRange('0', '9') >= 0;
        var hasAt = text.Contains('@', StringComparison.Ordinal);
        if (!hasDigit && !hasAt)
        {
            return [];
        }

        var matches = new List<PiiMatch>();
        try
        {
            if (hasDigit)
            {
                foreach (Match m in PersonnummerRegex().Matches(text))
                {
                    if (TryClassifyPersonnummer(m) is { } category)
                    {
                        Add(matches, new PiiMatch(category, m.Index, m.Length));
                    }
                }

                foreach (Match m in IbanRegex().Matches(text))
                {
                    if (IsValidIban(m.Value))
                    {
                        Add(matches, new PiiMatch(PiiCategory.Iban, m.Index, m.Length));
                    }
                }
            }

            if (hasAt)
            {
                foreach (Match m in EmailRegex().Matches(text))
                {
                    Add(matches, new PiiMatch(PiiCategory.Email, m.Index, m.Length));
                }
            }

            if (hasDigit)
            {
                foreach (Match m in PhoneRegex().Matches(text))
                {
                    var digits = m.Value.Count(char.IsAsciiDigit);
                    if (digits is >= 8 and <= 13)
                    {
                        Add(matches, new PiiMatch(PiiCategory.Phone, m.Index, m.Length));
                    }
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Fail closed: treat pathological input as containing PII so policies (block/reroute) apply.
            return [new PiiMatch(PiiCategory.Personnummer, 0, 0)];
        }

        matches.Sort((a, b) => a.Start.CompareTo(b.Start));
        return matches;
    }

    public static string Redact(string text, IReadOnlyList<PiiMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(matches);
        if (matches.Count == 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        var pos = 0;
        foreach (var m in matches.OrderBy(m => m.Start))
        {
            if (m.Start < pos || m.Length == 0)
            {
                continue;
            }

            sb.Append(text, pos, m.Start - pos).Append(Placeholder(m.Category));
            pos = m.Start + m.Length;
        }

        sb.Append(text, pos, text.Length - pos);
        return sb.ToString();
    }

    public static string Placeholder(PiiCategory category) => category switch
    {
        PiiCategory.Personnummer => "[PERSONNUMMER]",
        PiiCategory.Samordningsnummer => "[SAMORDNINGSNUMMER]",
        PiiCategory.Email => "[E-POST]",
        PiiCategory.Phone => "[TELEFON]",
        PiiCategory.Iban => "[IBAN]",
        _ => "[PII]",
    };

    private static void Add(List<PiiMatch> matches, PiiMatch candidate)
    {
        foreach (var existing in matches)
        {
            if (candidate.Start < existing.Start + existing.Length && existing.Start < candidate.Start + candidate.Length)
            {
                return; // overlaps a higher-confidence match already found
            }
        }

        matches.Add(candidate);
    }

    private static PiiCategory? TryClassifyPersonnummer(Match m)
    {
        var yy = int.Parse(m.Groups["yy"].ValueSpan, CultureInfo.InvariantCulture);
        var mm = int.Parse(m.Groups["mm"].ValueSpan, CultureInfo.InvariantCulture);
        var dd = int.Parse(m.Groups["dd"].ValueSpan, CultureInfo.InvariantCulture);
        var hasCentury = m.Groups["century"].Success;

        // A bare 10-digit number without separator is ambiguous (could be a phone number) – still accept
        // when Luhn and date validate; the combination makes accidental matches unlikely (~1/10 * date odds).
        var tenDigits = string.Concat(m.Groups["yy"].Value, m.Groups["mm"].Value, m.Groups["dd"].Value, m.Groups["num"].Value, m.Groups["check"].Value);
        if (!LuhnValid(tenDigits))
        {
            return null;
        }

        var category = PiiCategory.Personnummer;
        if (dd > 60)
        {
            dd -= 60;
            category = PiiCategory.Samordningsnummer;
        }

        if (mm is < 1 or > 12 || dd < 1)
        {
            return null;
        }

        int[] centuries = hasCentury
            ? [int.Parse(m.Groups["century"].ValueSpan, CultureInfo.InvariantCulture) * 100]
            : [1900, 2000];
        var validDate = centuries.Any(c => dd <= DateTime.DaysInMonth(c + yy, mm));
        if (!validDate)
        {
            return null;
        }
        return category;
    }

    internal static bool LuhnValid(string digits)
    {
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[i] - '0';
            if (i % 2 == digits.Length % 2)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
        }

        return sum % 10 == 0;
    }

    internal static bool IsValidIban(string value)
    {
        var iban = value.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (iban.Length is < 15 or > 34)
        {
            return false;
        }

        var rearranged = string.Concat(iban.AsSpan(4), iban.AsSpan(0, 4));
        var remainder = 0;
        foreach (var ch in rearranged)
        {
            int value2;
            if (char.IsAsciiDigit(ch))
            {
                value2 = ch - '0';
                remainder = ((remainder * 10) + value2) % 97;
            }
            else if (char.IsAsciiLetterUpper(ch))
            {
                value2 = ch - 'A' + 10;
                remainder = ((remainder * 100) + value2) % 97;
            }
            else
            {
                return false;
            }
        }

        return remainder == 1;
    }
}
