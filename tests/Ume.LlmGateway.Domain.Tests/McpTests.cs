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

public class McpToolSyncTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);

    private static string Definition(string description) =>
        """{"description":""" + JsonSerializer.Serialize(description) + ""","inputSchema":{"type":"object","properties":{"q":{"type":"string"}}}}""";

    private static McpServer Server(bool autoApprove = false) => new() { Slug = "diarium", DisplayName = "Diarium", Url = "https://d/mcp", AutoApproveNewTools = autoApprove };

    private static McpTool Approved(McpServer server, string name, string definition)
    {
        var tool = new McpTool
        {
            McpServerId = server.Id, Name = name, Definition = definition, DefinitionHash = McpToolDefinition.Hash(definition),
            ExposedName = McpToolNames.Exposed(server.Slug, name),
        };
        McpToolSync.Approve(tool, "admin", Now);
        server.Tools.Add(tool);
        return tool;
    }

    [Fact]
    public void The_hash_is_canonical_and_ignores_member_order_and_whitespace()
    {
        var hash = McpToolDefinition.Hash("""{"description":"d","inputSchema":{"type":"object","properties":{"a":{"type":"string"}}}}""");
        McpToolDefinition.Hash("""{ "inputSchema": { "properties": { "a": { "type": "string" } }, "type": "object" }, "description": "d" }""").ShouldBe(hash);
        McpToolDefinition.Hash("""{"description":"d","inputSchema":{"type":"object","properties":{"a":{"type":"number"}}}}""").ShouldNotBe(hash);
        McpToolDefinition.Hash("""{"inputSchema":{"required":["b","a"]}}""").ShouldNotBe(McpToolDefinition.Hash("""{"inputSchema":{"required":["a","b"]}}"""));
        hash.ShouldMatch("^[0-9a-f]{64}$");
    }

    [Fact]
    public void A_definition_holds_only_the_pinned_members_of_a_listed_tool()
    {
        var tool = JsonNode.Parse("""{"name":"search","title":"Sök","description":"d","inputSchema":{"type":"object"},"_meta":{"x":1},"icons":[],"annotations":{"readOnlyHint":true}}""")!.AsObject();
        JsonNode.Parse(McpToolDefinition.FromTool(tool))!.AsObject().Select(m => m.Key)
            .ShouldBe(["title", "description", "inputSchema", "annotations"]);
        McpToolSync.Title(McpToolDefinition.FromTool(tool)).ShouldBe("Sök");
    }

    [Fact]
    public void New_tools_wait_for_approval_unless_the_server_auto_approves()
    {
        var server = Server();
        var result = McpToolSync.Apply(server, [new McpDiscoveredTool("search", Definition("d"))], new HashSet<string>(), Now);
        var tool = result.Added.ShouldHaveSingleItem();
        tool.ApprovalState.ShouldBe(McpToolApprovalState.Pending);
        tool.IsExposed.ShouldBeFalse();
        tool.ExposedName.ShouldBe("diarium__search");
        tool.DefinitionHash.ShouldBe(McpToolDefinition.Hash(Definition("d")));
        tool.DiscoveredAt.ShouldBe(Now);
        server.Tools.ShouldContain(tool);

        var trusted = Server(autoApprove: true);
        var approved = McpToolSync.Apply(trusted, [new McpDiscoveredTool("search", Definition("d"))], new HashSet<string>(), Now).Added.ShouldHaveSingleItem();
        approved.ApprovalState.ShouldBe(McpToolApprovalState.Approved);
        approved.ApprovedDefinition.ShouldBe(Definition("d"));
        approved.ApprovedBy.ShouldBe(McpToolSync.AutoApprover);
    }

    [Fact]
    public void A_changed_definition_is_quarantined_and_a_reverted_one_approved_again()
    {
        var server = Server();
        var tool = Approved(server, "search", Definition("d"));

        var changed = McpToolSync.Apply(server, [new McpDiscoveredTool("search", Definition("now deletes cases"))], new HashSet<string>(), Now);
        changed.Changed.ShouldBe(["search"]);
        tool.ApprovalState.ShouldBe(McpToolApprovalState.Changed);
        tool.ApprovedDefinition.ShouldBe(Definition("d"));
        tool.Definition.ShouldBe(Definition("now deletes cases"));
        tool.IsExposed.ShouldBeTrue();

        var reverted = McpToolSync.Apply(server, [new McpDiscoveredTool("search", """{ "inputSchema": { "properties": { "q": { "type": "string" } }, "type": "object" }, "description": "d" }""")],
            new HashSet<string>(), Now);
        reverted.Reverted.ShouldBe(["search"]);
        tool.ApprovalState.ShouldBe(McpToolApprovalState.Approved);
    }

    [Fact]
    public void An_unchanged_definition_changes_nothing_even_when_its_stored_hash_is_not_canonical()
    {
        var server = Server();
        var tool = Approved(server, "search", Definition("d"));
        tool.DefinitionHash = new string('0', 64);
        var result = McpToolSync.Apply(server, [new McpDiscoveredTool("search", Definition("d"))], new HashSet<string>(), Now);
        result.ToolsChanged.ShouldBeFalse();
        tool.ApprovalState.ShouldBe(McpToolApprovalState.Approved);
        tool.DefinitionHash.ShouldBe(McpToolDefinition.Hash(Definition("d")));
    }

    [Fact]
    public void Pending_and_rejected_tools_take_the_new_definition_without_being_approved()
    {
        var server = Server();
        var pending = McpToolSync.Apply(server, [new McpDiscoveredTool("a", Definition("d"))], new HashSet<string>(), Now).Added.Single();
        var rejected = Approved(server, "b", Definition("d"));
        rejected.ApprovalState = McpToolApprovalState.Rejected;
        rejected.ApprovedDefinition = null;

        var result = McpToolSync.Apply(server, [new McpDiscoveredTool("a", Definition("e")), new McpDiscoveredTool("b", Definition("e"))], new HashSet<string>(), Now);
        result.Updated.ShouldBe(["a", "b"]);
        pending.ApprovalState.ShouldBe(McpToolApprovalState.Pending);
        rejected.ApprovalState.ShouldBe(McpToolApprovalState.Rejected);
        rejected.Definition.ShouldBe(Definition("e"));
    }

    [Fact]
    public void Removed_tools_are_deleted()
    {
        var server = Server();
        var gone = Approved(server, "gone", Definition("d"));
        var result = McpToolSync.Apply(server, [], new HashSet<string>(), Now);
        result.Removed.ShouldBe([gone]);
        server.Tools.ShouldBeEmpty();
    }

    [Fact]
    public void Tools_whose_exposed_names_collide_are_left_out_with_an_error()
    {
        var server = Server();
        _ = Approved(server, "case_search", Definition("d"));
        var result = McpToolSync.Apply(server,
            [new McpDiscoveredTool("case_search", Definition("d")), new McpDiscoveredTool("case.search", Definition("other")), new McpDiscoveredTool("x", Definition("d"))],
            new HashSet<string> { "diarium__x" }, Now);
        result.Added.ShouldBeEmpty();
        result.Errors.Count.ShouldBe(2);
        result.Errors[0].ShouldContain("case.search");
        result.Errors[1].ShouldContain("annan server");
        server.Tools.Select(t => t.Name).ShouldBe(["case_search"]);
    }

    [Fact]
    public void Invalid_and_duplicate_tools_are_reported()
    {
        var server = Server();
        var result = McpToolSync.Apply(server,
            [new McpDiscoveredTool("a", "not json"), new McpDiscoveredTool("b", Definition("d")), new McpDiscoveredTool("b", Definition("e")), new McpDiscoveredTool(new string('n', 129), Definition("d"))],
            new HashSet<string>(), Now);
        result.Added.Select(t => t.Name).ShouldBe(["b"]);
        result.Added[0].Definition.ShouldBe(Definition("d"));
        result.Errors.Count.ShouldBe(3);
    }

    [Fact]
    public void Rotation_copies_mcp_grants_to_the_replacement_key()
    {
        var replacement = new VirtualKey { Name = "k", Prefix = "p", KeyHash = "h" };
        var grant = new VirtualKeyMcpGrant
        {
            VirtualKeyId = Guid.NewGuid(), McpServerId = Guid.NewGuid(), AllowedTools = ["a"], ApprovalPolicy = McpApprovalPolicy.Never, AllowResources = true,
        };
        var copy = KeyRotation.CopyMcpGrants([grant], replacement, Now).ShouldHaveSingleItem();
        copy.VirtualKeyId.ShouldBe(replacement.Id);
        copy.McpServerId.ShouldBe(grant.McpServerId);
        copy.AllowedTools.ShouldBe(["a"]);
        copy.AllowedTools.ShouldNotBeSameAs(grant.AllowedTools);
        copy.ApprovalPolicy.ShouldBe(McpApprovalPolicy.Never);
        copy.AllowResources.ShouldBeTrue();
        copy.CreatedAt.ShouldBe(Now);
        copy.Id.ShouldNotBe(grant.Id);
    }
}

public class McpDefinitionDiffTests
{
    [Fact]
    public void Differences_are_reported_by_json_pointer()
    {
        var changes = McpDefinitionDiff.Compare(
            """{"description":"old","inputSchema":{"properties":{"a/b":{"type":"string"},"gone":{}},"required":["a/b"]},"annotations":{"readOnlyHint":true}}""",
            """{"description":"new","inputSchema":{"properties":{"a/b":{"type":"number"}},"required":["a/b","c"]},"annotations":{"readOnlyHint":true},"outputSchema":{}}""");
        changes.Select(c => (c.Path, c.Kind)).ShouldBe(
        [
            ("/description", McpDefinitionChangeKind.Changed),
            ("/inputSchema/properties/a~1b/type", McpDefinitionChangeKind.Changed),
            ("/inputSchema/properties/gone", McpDefinitionChangeKind.Removed),
            ("/inputSchema/required/1", McpDefinitionChangeKind.Added),
            ("/outputSchema", McpDefinitionChangeKind.Added),
        ]);
        changes[0].Before!.GetValue<string>().ShouldBe("old");
        changes[0].After!.GetValue<string>().ShouldBe("new");
        changes[3].After!.GetValue<string>().ShouldBe("c");
    }

    [Fact]
    public void Equal_definitions_have_no_differences_and_a_missing_one_differs_as_a_whole()
    {
        McpDefinitionDiff.Compare("""{"a":1,"b":[1,2]}""", """{"b":[1,2],"a":1}""").ShouldBeEmpty();
        McpDefinitionDiff.Compare(null, """{"a":1}""").ShouldHaveSingleItem().Path.ShouldBe("");
    }
}

public class McpToolFormatsTests
{
    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    [Theory]
    [InlineData(null, LlmToolFormat.OpenAi)]
    [InlineData("openai", LlmToolFormat.OpenAi)]
    [InlineData("Responses", LlmToolFormat.Responses)]
    [InlineData("anthropic", LlmToolFormat.Anthropic)]
    public void Format_names_are_parsed(string? value, LlmToolFormat expected)
    {
        McpToolFormats.TryParseFormat(value, out var format).ShouldBeTrue();
        format.ShouldBe(expected);
    }

    [Fact]
    public void An_unknown_format_is_refused() => McpToolFormats.TryParseFormat("gemini", out _).ShouldBeFalse();

    [Fact]
    public void A_definition_without_input_schema_or_description_gets_an_empty_object_schema_and_the_title()
    {
        var definition = McpToolDefinition.Parse("""{"title":"Time"}""")!;

        McpToolFormats.Definition(LlmToolFormat.Anthropic, "demo__time", definition).ToJsonString()
            .ShouldBe("""{"name":"demo__time","description":"Time","input_schema":{"type":"object","properties":{}}}""");
    }

    [Fact]
    public void Each_tool_call_shape_is_recognised_and_its_arguments_parsed()
    {
        var chat = McpToolFormats.ParseCall(Parse("""{"id":"c1","type":"function","function":{"name":"a__b","arguments":"{\"x\":1}"}}"""))!;
        (chat.Format, chat.CallId, chat.Name).ShouldBe((LlmToolFormat.OpenAi, "c1", "a__b"));
        chat.Arguments!["x"]!.GetValue<int>().ShouldBe(1);

        var untyped = McpToolFormats.ParseCall(Parse("""{"id":"c2","function":{"name":"a__b","arguments":""}}"""))!;
        untyped.Format.ShouldBe(LlmToolFormat.OpenAi);
        untyped.Arguments.ShouldBeNull();

        var responses = McpToolFormats.ParseCall(Parse("""{"type":"function_call","id":"fc_1","call_id":"c3","name":"a__b","arguments":"{}"}"""))!;
        (responses.Format, responses.CallId).ShouldBe((LlmToolFormat.Responses, "c3"));

        var body = Parse("""{"type":"tool_use","id":"toolu_1","name":"a__b","input":{"x":2}}""");
        var anthropic = McpToolFormats.ParseCall(body)!;
        (anthropic.Format, anthropic.CallId).ShouldBe((LlmToolFormat.Anthropic, "toolu_1"));
        anthropic.Arguments!.Parent.ShouldBeNull();

        McpToolFormats.ParseCall(Parse("""{"type":"function_call","name":"a__b"}""")).ShouldBeNull();
        McpToolFormats.ParseCall(Parse("""{"type":"tool_use","id":"t","input":{}}""")).ShouldBeNull();
        McpToolFormats.ParseCall(Parse("""{"type":"message","content":"hi"}""")).ShouldBeNull();
    }

    [Fact]
    public void An_image_first_tool_error_gets_a_leading_error_text_in_responses()
    {
        var call = new LlmToolCall(LlmToolFormat.Responses, "c", "a__b", null);
        var result = Parse("""{"content":[{"type":"image","mimeType":"image/png","data":"AAAA"}],"isError":true}""");

        McpToolFormats.Result(call, result)["output"]!.ToJsonString()
            .ShouldBe("""[{"type":"input_text","text":"Error:"},{"type":"input_image","image_url":"data:image/png;base64,AAAA"}]""");
    }

    [Fact]
    public void An_empty_result_has_empty_text_and_an_anthropic_result_without_content()
    {
        var result = Parse("""{"content":[]}""");

        McpToolFormats.Result(new LlmToolCall(LlmToolFormat.OpenAi, "c", "a__b", null), result)["content"]!.GetValue<string>().ShouldBeEmpty();
        McpToolFormats.Result(new LlmToolCall(LlmToolFormat.Anthropic, "c", "a__b", null), result).ToJsonString()
            .ShouldBe("""{"type":"tool_result","tool_use_id":"c"}""");
    }

    [Fact]
    public void An_image_type_the_apis_do_not_take_is_a_placeholder_even_where_images_are_native()
    {
        var result = Parse("""{"content":[{"type":"image","mimeType":"image/svg+xml","data":"PHN2Zz4="}]}""");

        McpToolFormats.Result(new LlmToolCall(LlmToolFormat.Responses, "c", "a__b", null), result)["output"]!.GetValue<string>()
            .ShouldBe("[image: image/svg+xml, 5 bytes]");
    }
}
