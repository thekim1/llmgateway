using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.Domain.Tests;

public class McpToolNamesTests
{
    [Theory]
    [InlineData("diarium", "search_cases", "diarium__search_cases")]
    [InlineData("demo", "get-time", "demo__get-time")]
    [InlineData("demo", "files.read", "demo__files_read")]
    [InlineData("demo", "läs ärende/v2", "demo__l_s__rende_v2")]
    [InlineData("demo", "Echo", "demo__Echo")]
    public void Exposed_name_is_slug_and_tool_with_disallowed_characters_replaced(string slug, string tool, string expected) =>
        McpToolNames.Exposed(slug, tool).ShouldBe(expected);

    [Fact]
    public void Exposed_name_of_exactly_64_characters_is_kept()
    {
        var tool = new string('t', McpToolNames.MaxLength - "demo__".Length);
        McpToolNames.Exposed("demo", tool).ShouldBe("demo__" + tool);
    }

    [Fact]
    public void Long_names_are_cut_to_64_characters_with_a_stable_hash_suffix()
    {
        var common = new string('x', 80);
        var first = McpToolNames.Exposed("diarium", common + "_first");
        var second = McpToolNames.Exposed("diarium", common + "_second");

        first.Length.ShouldBe(McpToolNames.MaxLength);
        second.Length.ShouldBe(McpToolNames.MaxLength);
        first.ShouldStartWith("diarium__xxx");
        first.ShouldNotBe(second);
        first[..55].ShouldBe(second[..55]);
        first.ShouldMatch("^[a-z_]{55}_[0-9a-f]{8}$");
        McpToolNames.Exposed("diarium", common + "_first").ShouldBe(first);
    }

    [Fact]
    public void Hash_suffix_separates_long_names_that_sanitise_alike()
    {
        var common = new string('x', 70);
        McpToolNames.Exposed("demo", common + ".a").ShouldNotBe(McpToolNames.Exposed("demo", common + "_a"));
    }

    [Fact]
    public void Exposed_names_are_valid_function_names_for_every_provider()
    {
        string[] tools = ["a", "with space", "ünïcödé", "emoji😀", new string('ä', 200), "dots.and/slashes:colons"];
        foreach (var tool in tools)
        {
            McpToolNames.Exposed("s", tool).ShouldMatch("^[A-Za-z0-9_-]{1,64}$");
        }
    }

    [Theory]
    [InlineData("demo", true)]
    [InlineData("d", true)]
    [InlineData("diarium_v2", true)]
    [InlineData("a_b_c2", true)]
    [InlineData("abcdefghijklmnopqrstuvwx", true)]
    [InlineData("abcdefghijklmnopqrstuvwxy", false)]
    [InlineData("abcdefghij_klmnopqrstu_wx", false)]
    [InlineData("abcdefghij_klmnopqrstu_w", true)]
    [InlineData("a__b", false)]
    [InlineData("ab_", false)]
    [InlineData("Demo", false)]
    [InlineData("2demo", false)]
    [InlineData("_demo", false)]
    [InlineData("de-mo", false)]
    [InlineData("demo\n", false)]
    [InlineData("", false)]
    public void Slugs_are_short_lower_case_identifiers(string slug, bool valid) => McpToolNames.IsValidSlug(slug).ShouldBe(valid);
}

public class McpEntityTests
{
    [Theory]
    [InlineData(McpToolApprovalState.Approved, true)]
    [InlineData(McpToolApprovalState.Changed, true)]
    [InlineData(McpToolApprovalState.Pending, false)]
    [InlineData(McpToolApprovalState.Rejected, false)]
    public void Only_approved_or_changed_tools_with_an_approved_definition_are_exposed(McpToolApprovalState state, bool exposed)
    {
        var tool = new McpTool { Name = "t", Definition = "{}", DefinitionHash = "h", ExposedName = "s__t", ApprovalState = state, ApprovedDefinition = "{}" };
        tool.IsExposed.ShouldBe(exposed);

        tool.ApprovedDefinition = null;
        tool.IsExposed.ShouldBeFalse();
    }

    [Fact]
    public void A_grant_allows_listed_tools_or_all_with_a_wildcard_and_nothing_when_empty()
    {
        new VirtualKeyMcpGrant { AllowedTools = ["*"] }.Allows("anything").ShouldBeTrue();
        new VirtualKeyMcpGrant { AllowedTools = ["search", "get"] }.Allows("get").ShouldBeTrue();
        new VirtualKeyMcpGrant { AllowedTools = ["search"] }.Allows("Search").ShouldBeFalse();
        new VirtualKeyMcpGrant { AllowedTools = ["search"] }.Allows("delete").ShouldBeFalse();
        new VirtualKeyMcpGrant().Allows("search").ShouldBeFalse();
    }
}
