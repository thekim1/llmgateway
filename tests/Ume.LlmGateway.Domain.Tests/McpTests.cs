using System.Text.Json;
using System.Text.Json.Nodes;
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

public class McpSchemaValidatorTests
{
    private static readonly JsonElement Schema = JsonElement.Parse("""
        {"type":"object","required":["number"],"additionalProperties":false,
         "properties":{
           "number":{"type":"string","minLength":2,"maxLength":10},
           "limit":{"type":"integer","minimum":1,"maximum":50},
           "mode":{"enum":["fast","full"]},
           "tags":{"type":"array","items":{"type":"string"},"maxItems":2},
           "filter":{"type":"object","properties":{"open":{"type":"boolean"}}},
           "maybe":{"type":["string","null"]}}}
        """);

    [Theory]
    [InlineData("""{"number":"KS-1"}""")]
    [InlineData("""{"number":"KS-1","limit":5,"mode":"full","tags":["a","b"],"filter":{"open":true},"maybe":null}""")]
    [InlineData("""{"number":"KS-1","limit":5.0}""")]
    public void Valid_arguments_pass(string arguments) =>
        McpSchemaValidator.Validate(Schema, JsonNode.Parse(arguments)).ShouldBeNull();

    [Theory]
    [InlineData("""{}""", "Argument '/number' is required")]
    [InlineData("""{"number":5}""", "Argument '/number' must be of type string")]
    [InlineData("""{"number":"K"}""", "Argument '/number' must be at least 2 characters")]
    [InlineData("""{"number":"KS-1","limit":0}""", "Argument '/limit' must be at least 1")]
    [InlineData("""{"number":"KS-1","limit":1.5}""", "Argument '/limit' must be of type integer")]
    [InlineData("""{"number":"KS-1","mode":"slow"}""", "Argument '/mode' must be one of [\"fast\",\"full\"]")]
    [InlineData("""{"number":"KS-1","tags":["a",1]}""", "Argument '/tags/1' must be of type string")]
    [InlineData("""{"number":"KS-1","tags":["a","b","c"]}""", "Argument '/tags' must have at most 2 items")]
    [InlineData("""{"number":"KS-1","filter":{"open":"yes"}}""", "Argument '/filter/open' must be of type boolean")]
    [InlineData("""{"number":"KS-1","other":1}""", "Argument '/other' is not allowed")]
    [InlineData("""{"number":"KS-1","maybe":1}""", "Argument '/maybe' must be string or null")]
    public void Invalid_arguments_are_reported_with_their_path(string arguments, string error) =>
        McpSchemaValidator.Validate(Schema, JsonNode.Parse(arguments)).ShouldBe(error);

    [Fact]
    public void Unknown_keywords_and_schemas_that_are_not_objects_are_ignored()
    {
        McpSchemaValidator.Validate(JsonElement.Parse("""{"$ref":"#/defs/x","oneOf":[]}"""), new JsonObject()).ShouldBeNull();
        McpSchemaValidator.Validate(JsonElement.Parse("true"), new JsonObject()).ShouldBeNull();
    }
}

public class McpAccessTests
{
    private static readonly VirtualKey Key = new() { Name = "k", Prefix = "p", KeyHash = "h" };

    private static (McpServer Server, VirtualKeyMcpGrant Grant, McpTool Tool) Setup(params string[] allowed)
    {
        var server = new McpServer { Slug = "s", DisplayName = "s", Url = "https://s", Residency = DataResidency.Eu };
        var tool = new McpTool
        {
            McpServerId = server.Id, Name = "t", Definition = "{}", DefinitionHash = "h", ExposedName = "s__t",
            ApprovalState = McpToolApprovalState.Approved, ApprovedDefinition = "{}",
        };
        return (server, new VirtualKeyMcpGrant { VirtualKeyId = Key.Id, McpServerId = server.Id, AllowedTools = [.. allowed] }, tool);
    }

    [Fact]
    public void A_granted_approved_tool_on_an_enabled_server_is_allowed()
    {
        var (server, grant, tool) = Setup("*");
        McpAccess.Check(Key, server, grant, tool, circuitOpen: false).ShouldBe(McpDenial.None);
    }

    [Fact]
    public void Every_check_can_refuse()
    {
        var (server, grant, tool) = Setup("*");
        McpAccess.Check(Key, server, null, tool, false).ShouldBe(McpDenial.NotGranted);
        McpAccess.Check(Key, server, new VirtualKeyMcpGrant { VirtualKeyId = Guid.NewGuid(), McpServerId = server.Id, AllowedTools = ["*"] }, tool, false)
            .ShouldBe(McpDenial.NotGranted);
        McpAccess.Check(Key, server, new VirtualKeyMcpGrant { VirtualKeyId = Key.Id, McpServerId = server.Id, AllowedTools = ["other"] }, tool, false)
            .ShouldBe(McpDenial.ToolNotGranted);
        McpAccess.Check(Key, server, grant, tool, circuitOpen: true).ShouldBe(McpDenial.CircuitOpen);
        McpAccess.Check(Key, server, grant, tool, false, restrictTo: DataResidency.OnPrem).ShouldBe(McpDenial.OnPremOnly);

        server.IsEnabled = false;
        McpAccess.Check(Key, server, grant, tool, false).ShouldBe(McpDenial.ServerDisabled);
        server.IsEnabled = true;

        tool.ApprovalState = McpToolApprovalState.Pending;
        McpAccess.Check(Key, server, grant, tool, false).ShouldBe(McpDenial.NotApproved);
    }

    [Fact]
    public void Residency_follows_the_keys_allow_list_and_empty_means_all()
    {
        var (server, _, tool) = Setup("*");
        var onPremOnly = new VirtualKey { Name = "k", Prefix = "p", KeyHash = "h", AllowedResidencies = [DataResidency.OnPrem] };
        var grant = new VirtualKeyMcpGrant { VirtualKeyId = onPremOnly.Id, McpServerId = server.Id, AllowedTools = ["*"] };

        McpAccess.Check(onPremOnly, server, grant, tool, false).ShouldBe(McpDenial.Residency);
        server.Residency = DataResidency.OnPrem;
        McpAccess.Check(onPremOnly, server, grant, tool, false).ShouldBe(McpDenial.None);
    }
}

public class McpToolDefinitionTests
{
    [Theory]
    [InlineData("""{"annotations":{"readOnlyHint":true}}""", true)]
    [InlineData("""{"annotations":{"destructiveHint":false}}""", true)]
    [InlineData("""{"annotations":{"destructiveHint":true,"idempotentHint":true}}""", true)]
    [InlineData("""{"annotations":{"destructiveHint":true}}""", false)]
    [InlineData("""{"annotations":{}}""", false)]
    [InlineData("""{}""", false)]
    public void Only_tools_that_cannot_do_harm_twice_may_be_retried(string json, bool mayRetry) =>
        McpToolDefinition.Parse(json)!.MayRetry.ShouldBe(mayRetry);

    [Fact]
    public void Definitions_that_are_not_objects_are_refused()
    {
        McpToolDefinition.Parse("[]").ShouldBeNull();
        McpToolDefinition.Parse("{").ShouldBeNull();
        McpToolDefinition.Parse(null).ShouldBeNull();
        McpToolDefinition.Parse("""{"inputSchema":{"type":"object"}}""")!.InputSchema.ShouldNotBeNull();
    }
}

public class McpPiiTests
{
    [Fact]
    public void Arguments_are_scanned_in_full_including_names_ids_and_types()
    {
        var args = (JsonObject)JsonNode.Parse("""{"name":"anna@example.se","id":"070-123 45 67","nested":[{"type":"x@example.se"}]}""")!;
        var scan = McpPii.ScanArguments(args, redact: true);

        scan.Summary.ShouldBe("Email:2,Phone:1");
        args.ToJsonString().ShouldNotContain("@example.se");
    }

    [Fact]
    public void Results_are_scanned_in_text_and_structured_content_but_not_in_payloads()
    {
        var result = JsonNode.Parse("""
            {"content":[{"type":"text","text":"Mejla anna@example.se"},{"type":"image","data":"anna@example.se","mimeType":"image/png"},
                        {"type":"resource","resource":{"uri":"mailto:anna@example.se","text":"bo@example.se","blob":"x@example.se"}}],
             "structuredContent":{"data":"c@example.se"},"_meta":{"m":"d@example.se"}}
            """);
        var scan = McpPii.ScanResult(result, redact: true);

        scan.Summary.ShouldBe("Email:3");
        result!["content"]![0]!["text"]!.GetValue<string>().ShouldBe("Mejla [E-POST]");
        result["content"]![1]!["data"]!.GetValue<string>().ShouldBe("anna@example.se");
        result["content"]![2]!["resource"]!["text"]!.GetValue<string>().ShouldBe("[E-POST]");
        result["structuredContent"]!["data"]!.GetValue<string>().ShouldBe("[E-POST]");
        result["_meta"]!["m"]!.GetValue<string>().ShouldBe("d@example.se");
    }
}

public class McpResourceUriTests
{
    [Theory]
    [InlineData("demo", "file:///notes/a.txt")]
    [InlineData("diarium_v2", "https://x.example/doc?id=1")]
    [InlineData("demo", "mcp+ume://other/file:///x")]
    public void Namespaced_uris_round_trip(string slug, string uri)
    {
        var namespaced = McpResourceUris.Namespace(slug, uri);
        namespaced.ShouldBe($"mcp+ume://{slug}/{uri}");
        McpResourceUris.TryParse(namespaced, out var parsedSlug, out var parsedUri).ShouldBeTrue();
        parsedSlug.ShouldBe(slug);
        parsedUri.ShouldBe(uri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("file:///notes/a.txt")]
    [InlineData("mcp+ume://")]
    [InlineData("mcp+ume://demo")]
    [InlineData("mcp+ume://demo/")]
    [InlineData("mcp+ume://Demo/file:///a")]
    [InlineData("mcp+ume:///file:///a")]
    public void Other_uris_are_not_namespaced(string? uri) => McpResourceUris.TryParse(uri, out _, out _).ShouldBeFalse();

    [Theory]
    [InlineData("demo__summarize_case", "demo")]
    [InlineData("a_b__x__y", "a_b")]
    [InlineData("nounderscore", null)]
    [InlineData("__x", null)]
    [InlineData("Demo__x", null)]
    public void The_slug_of_an_exposed_name_ends_at_the_first_double_underscore(string exposed, string? slug) =>
        McpResourceUris.SlugOfExposedName(exposed).ShouldBe(slug);
}

public class McpDocumentAccessTests
{
    [Fact]
    public void Resources_and_prompts_need_their_grant_flag_and_the_servers_capability()
    {
        var key = new VirtualKey { Name = "k", Prefix = "p", KeyHash = "h" };
        var server = new McpServer { Slug = "s", DisplayName = "s", Url = "https://s", Capabilities = McpServerCapabilities.Tools | McpServerCapabilities.Resources };
        var grant = new VirtualKeyMcpGrant { VirtualKeyId = key.Id, McpServerId = server.Id, AllowedTools = ["*"] };

        McpAccess.CheckResources(key, server, grant, false).ShouldBe(McpDenial.ResourcesNotGranted);
        grant.AllowResources = true;
        McpAccess.CheckResources(key, server, grant, false).ShouldBe(McpDenial.None);
        McpAccess.CheckResources(key, server, grant, circuitOpen: true).ShouldBe(McpDenial.CircuitOpen);

        grant.AllowPrompts = true;
        McpAccess.CheckPrompts(key, server, grant, false).ShouldBe(McpDenial.PromptsNotGranted); // the server offers no prompts
        server.Capabilities |= McpServerCapabilities.Prompts;
        McpAccess.CheckPrompts(key, server, grant, false).ShouldBe(McpDenial.None);
        McpAccess.CheckPrompts(key, server, null, false).ShouldBe(McpDenial.NotGranted);
    }
}

public class McpDocumentPiiTests
{
    [Fact]
    public void Resource_text_is_scanned_but_blobs_and_uris_are_not()
    {
        var result = JsonNode.Parse("""
            {"contents":[{"uri":"mailto:a@example.se","mimeType":"text/plain","text":"Mejla b@example.se"},
                         {"uri":"file:///x","mimeType":"image/png","blob":"c@example.se"}]}
            """);
        var scan = McpPii.ScanResourceContents(result, redact: true);

        scan.Summary.ShouldBe("Email:1");
        result!["contents"]![0]!["text"]!.GetValue<string>().ShouldBe("Mejla [E-POST]");
        result["contents"]![0]!["uri"]!.GetValue<string>().ShouldBe("mailto:a@example.se");
        result["contents"]![1]!["blob"]!.GetValue<string>().ShouldBe("c@example.se");
    }

    [Fact]
    public void Prompt_messages_and_description_are_scanned()
    {
        var result = JsonNode.Parse("""
            {"description":"För a@example.se","messages":[{"role":"user","content":{"type":"text","text":"Ring 070-123 45 67"}},
                {"role":"user","content":{"type":"image","data":"b@example.se","mimeType":"image/png"}}]}
            """);
        var scan = McpPii.ScanPrompt(result, redact: true);

        scan.Summary.ShouldBe("Email:1,Phone:1");
        result!["messages"]![0]!["content"]!["text"]!.GetValue<string>().ShouldBe("Ring [TELEFON]");
        result["messages"]![1]!["content"]!["data"]!.GetValue<string>().ShouldBe("b@example.se");
    }
}
