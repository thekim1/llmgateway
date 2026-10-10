using System.Globalization;

namespace Ume.LlmGateway.Domain.Services;

/// <summary>
/// One CSV cell (RFC 4180) for files that end up in spreadsheets. A cell that a spreadsheet would run as a formula
/// (it starts with = + - @, also after leading spaces, or with a tab or line break) is prefixed with <c>'</c>; numbers such
/// as <c>-1.5</c> are left alone so they stay numbers. Cells are quoted only when they contain the delimiter, a quote or
/// a line break.
/// </summary>
public static class CsvCell
{
    public static string Escape(string? value, char delimiter = ',')
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (IsFormula(value))
        {
            value = "'" + value;
        }

        return value.AsSpan().IndexOfAny(['"', '\r', '\n', delimiter]) >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    /// <summary>True when a spreadsheet could treat <paramref name="value"/> as a formula.</summary>
    public static bool IsFormula(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0 || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        if (value[0] is '\t' or '\r' or '\n')
        {
            return true;
        }

        var trimmed = value.AsSpan().TrimStart();
        return trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@';
    }
}
