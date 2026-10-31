using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ume.LlmGateway.Domain.Pii;

public readonly record struct PiiMatch(PiiCategory Category, int Start, int Length);

/// <summary>
/// Lightweight, deterministic PII detector tuned for Swedish public-sector data. Matches are validated
/// (real calendar dates and, for bare 10-digit personnummer, Luhn; mod-97 for IBAN) to keep false positives low.
/// Pluggable: a future NER/Presidio based detector can implement the same contract.
/// </summary>
public static partial class PiiDetector
{
    [GeneratedRegex(@"(?<![\d])(?<century>(?:18|19|20))?(?<yy>\d{2})(?<mm>\d{2})(?<dd>\d{2})(?<sep>[-+ ]?)(?<num>\d{3})(?<check>\d)(?![\d])", RegexOptions.CultureInvariant, 250)]
    private static partial Regex PersonnummerRegex();

    [GeneratedRegex(@"(?<![\w.%+-])[A-Za-z0-9._%+-]{1,64}@[A-Za-z0-9.-]{1,253}\.[A-Za-z]{2,24}(?![\w])", RegexOptions.CultureInvariant, 250)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?<![\d+])(?:(?:\+46|0046)[\s-]?\(?0?\)?[\s-]?[1-9]\d{0,2}|0[1-9]\d{0,2})[\s-]?\d{2,3}[\s-]?\d{2}[\s-]?\d{2,3}(?![\d])", RegexOptions.CultureInvariant, 250)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Z]{2}\d{2}(?:[ ]?[A-Z0-9]{4}){2,7}(?:[ ]?[A-Z0-9]{1,3})?(?![A-Za-z0-9])", RegexOptions.CultureInvariant, 250)]
    private static partial Regex IbanRegex();

    /// <summary>
    /// The form of <paramref name="text"/> the patterns run on: Unicode compatibility forms folded (NFKC: full-width digits,
    /// no-break spaces), other decimal digits (Arabic-Indic, Devanagari, ...) made ASCII, invisible format characters
    /// (zero-width space, soft hyphen, word joiner) removed and dashes made '-'. Without it a personnummer in full-width
    /// digits or with a zero-width space inside would pass unnoticed. Match positions and redaction refer to this form.
    /// Text that needs none of it (ASCII, or ordinary Swedish prose) is returned as it is, without allocating.
    /// </summary>
    public static string Canonical(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        // Two vectorised scans decide; normalising (an ICU call) runs only when a character could fold into a digit, '@', '-'
        // or a space. In Latin-1 only a few can (Swedish å, ä, ö never do); above it each candidate is classified.
        var span = text.AsSpan();
        var fold = span.ContainsAny(Latin1MayFold);
        for (var next = span.IndexOfAnyExceptInRange('\0', '\u00FF'); !fold && next >= 0;)
        {
            fold = MayFold(span[next]);
            var rest = span[(next + 1)..].IndexOfAnyExceptInRange('\0', '\u00FF');
            next = rest < 0 ? -1 : next + 1 + rest;
        }

        if (!fold)
        {
            return text;
        }

        var folded = text.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(folded.Length);
        foreach (var c in folded)
        {
            switch (CharUnicodeInfo.GetUnicodeCategory(c))
            {
                case UnicodeCategory.Format:
                    break;
                case UnicodeCategory.DashPunctuation:
                    sb.Append('-');
                    break;
                case UnicodeCategory.DecimalDigitNumber when c > '9':
                    sb.Append((char)('0' + CharUnicodeInfo.GetDecimalDigitValue(c)));
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>No-break space, soft hyphen, superscript digits and vulgar fractions: the Latin-1 characters that fold.</summary>
    private static readonly SearchValues<char> Latin1MayFold = SearchValues.Create("\u00A0\u00AD\u00B2\u00B3\u00B9\u00BC\u00BD\u00BE");

    private static bool MayFold(char c) => c is >= '\uFF00' and <= '\uFFEF' || CharUnicodeInfo.GetUnicodeCategory(c) is
        UnicodeCategory.Format or UnicodeCategory.DashPunctuation or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.OtherNumber or
        UnicodeCategory.SpaceSeparator or UnicodeCategory.Surrogate;

    /// <summary>Matches in <paramref name="text"/> as given; pass <see cref="Canonical"/> text so look-alike characters are caught.</summary>
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

        // A century (12 digits) or a separator makes the shape unmistakable, so the check digit is not required: people
        // paste made-up or mistyped numbers and those must be caught too. A bare 10-digit number without separator is
        // ambiguous (0701234567 is a mobile number), so it also needs a valid Luhn check digit.
        if (!hasCentury && m.Groups["sep"].Length == 0)
        {
            Span<char> tenDigits = stackalloc char[10];
            m.Groups["yy"].ValueSpan.CopyTo(tenDigits);
            m.Groups["mm"].ValueSpan.CopyTo(tenDigits[2..]);
            m.Groups["dd"].ValueSpan.CopyTo(tenDigits[4..]);
            m.Groups["num"].ValueSpan.CopyTo(tenDigits[6..]);
            m.Groups["check"].ValueSpan.CopyTo(tenDigits[9..]);
            if (!LuhnValid(tenDigits))
            {
                return null;
            }
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

        var validDate = hasCentury
            ? dd <= DateTime.DaysInMonth((int.Parse(m.Groups["century"].ValueSpan, CultureInfo.InvariantCulture) * 100) + yy, mm)
            : dd <= DateTime.DaysInMonth(1900 + yy, mm) || dd <= DateTime.DaysInMonth(2000 + yy, mm);
        return validDate ? category : null;
    }

    internal static bool LuhnValid(ReadOnlySpan<char> digits)
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
