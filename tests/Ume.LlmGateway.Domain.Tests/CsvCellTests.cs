using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.Domain.Tests;

public sealed class CsvCellTests
{
    [Theory]
    [InlineData("=HYPERLINK(\"evil\")", "\"'=HYPERLINK(\"\"evil\"\")\"")]
    [InlineData(" =HYPERLINK(1)", "' =HYPERLINK(1)")]
    [InlineData("  @SUM(1)", "'  @SUM(1)")]
    [InlineData("+cmd|' /C calc'!A0", "'+cmd|' /C calc'!A0")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("\tvalue", "'\tvalue")]
    [InlineData("\rvalue", "\"'\rvalue\"")]
    [InlineData("\nvalue", "\"'\nvalue\"")]
    public void Formula_cells_are_defused(string input, string expected) =>
        CsvCell.Escape(input).ShouldBe(expected);

    [Theory]
    [InlineData("-1", "-1")]
    [InlineData("+1", "+1")]
    [InlineData("-1.5e3", "-1.5e3")]
    [InlineData("42", "42")]
    public void Numbers_stay_numbers(string input, string expected) =>
        CsvCell.Escape(input).ShouldBe(expected);

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\r\nlines", "\"two\r\nlines\"")]
    public void Cells_are_quoted_only_when_needed(string? input, string expected) =>
        CsvCell.Escape(input).ShouldBe(expected);

    [Fact]
    public void The_delimiter_decides_what_needs_quotes()
    {
        CsvCell.Escape("a;b", ';').ShouldBe("\"a;b\"");
        CsvCell.Escape("a,b", ';').ShouldBe("a,b");
    }
}
