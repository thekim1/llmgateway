using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Mcp;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Security;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi.Tests;

public sealed class McpAdminTests(AdminFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewSlug() => "t" + Guid.NewGuid().ToString("N")[..12];

    private JsonObject Server(string slug, string? url = null, string residency = "OnPrem") => new()
    {
        ["slug"] = slug, ["displayName"] = "Server " + slug, ["url"] = url ?? fixture.Mcp.Endpoint(slug), ["residency"] = residency,
    };

    private static async Task<JsonNode> CreateAsync(HttpClient client, JsonObject body, HttpStatusCode status = HttpStatusCode.Created)
    {
        using var response = await client.PostAsJsonAsync("/api/mcp/servers", body, Ct);
        return await AdminFixture.ReadAsync(response, status);
    }

    private static async Task<JsonNode> SyncAsync(HttpClient client, Guid id)
    {
        using var response = await client.PostAsync($"/api/mcp/servers/{id}/sync", null, Ct);
        return await AdminFixture.ReadAsync(response);
    }

    private static async Task<Dictionary<string, JsonNode>> ToolsAsync(HttpClient client, Guid id)
    {
        using var response = await client.GetAsync($"/api/mcp/servers/{id}/tools", Ct);
        return (await AdminFixture.ReadAsync(response)).AsArray().ToDictionary(t => t!["name"]!.GetValue<string>(), t => t!);
    }

    private static async Task<JsonNode> ApproveAsync(HttpClient client, Guid server, JsonNode tool, HttpStatusCode status = HttpStatusCode.OK, string? hash = null)
    {
        using var response = await client.PostAsJsonAsync($"/api/mcp/servers/{server}/tools/{tool["id"]}/approve",
            new { definitionHash = hash ?? tool["definitionHash"]!.GetValue<string>() }, Ct);
        return await AdminFixture.ReadAsync(response, status);
    }

    private static string[] Strings(JsonNode? node) => [.. node!.AsArray().Select(n => n!.GetValue<string>())];

    private static Guid Id(JsonNode node) => node["id"]!.GetValue<Guid>();

    [Theory]
    [InlineData("""{"slug":"Bad"}""", "slug")]
    [InlineData("""{"slug":"a__b"}""", "slug")]
    [InlineData("""{"slug":"a_"}""", "slug")]
    [InlineData("""{"url":"http://mcp.public.test/mcp","residency":"Eu"}""", "url")]
    [InlineData("""{"url":"https://user:pw@mcp.public.test/mcp"}""", "url")]
    [InlineData("""{"url":"https://mcp.public.test/mcp?api_key=x"}""", "url")]
    [InlineData("""{"forwardHeaders":["Authorization"]}""", "forwardHeaders")]
    [InlineData("""{"forwardHeaders":["mcp-session-id"]}""", "forwardHeaders")]
    [InlineData("""{"forwardHeaders":["X-Trace","x-trace"]}""", "forwardHeaders")]
    [InlineData("""{"forwardHeaders":["Bad Header"]}""", "forwardHeaders")]
    [InlineData("""{"authType":"StaticHeaders"}""", "credential")]
    [InlineData("""{"credential":{"headers":{"X-Api-Key":"k"}}}""", "credential")]
    [InlineData("""{"authType":"StaticHeaders","credential":{"headers":{"Host":"evil"}}}""", "credential")]
    [InlineData("""{"authType":"StaticHeaders","credential":{"headers":{"X-Api-Key":"a\nb"}}}""", "credential")]
    [InlineData("""{"authType":"StaticHeaders","credential":{"headers":{}}}""", "credential")]
    [InlineData("""{"authType":"OAuthClientCredentials","credential":{"tokenUrl":"https://id.public.test/token","clientId":"c"}}""", "credential")]
    [InlineData("""{"authType":"OAuthClientCredentials","credential":{"tokenUrl":"ftp://id.public.test","clientId":"c","clientSecret":"s"}}""", "credential")]
    [InlineData("""{"timeoutSeconds":0}""", "timeoutSeconds")]
    public async Task Server_requests_are_validated(string overrides, string field)
    {
        using var client = await fixture.ClientAsync();
        var body = Server(NewSlug(), "https://mcp.public.test/mcp", "Eu");
        foreach (var (name, value) in JsonNode.Parse(overrides)!.AsObject())
        {
            body[name] = value?.DeepClone();
        }

        var problem = await CreateAsync(client, body, HttpStatusCode.BadRequest);
        problem["errors"]!.AsObject().ContainsKey(field).ShouldBeTrue(problem.ToJsonString());
    }

    [Fact]
    public async Task Server_addresses_follow_the_ssrf_rules()
    {
        using var admin = await fixture.ClientAsync();
        foreach (var url in new[] { "http://169.254.169.254/mcp", "http://x.metadata.test/mcp", "http://x.mixed.test/mcp", "http://[fe80::1]/mcp", "http://0.0.0.0/mcp" })
        {
            var refused = await CreateAsync(admin, Server(NewSlug(), url), HttpStatusCode.BadRequest);
            Strings(refused["errors"]!["url"]).ShouldBe([McpUrlGuard.ForbiddenMessage], url);
        }

        var unresolved = await CreateAsync(admin, Server(NewSlug(), "https://mcp.invalid/mcp", "Eu"), HttpStatusCode.BadRequest);
        Strings(unresolved["errors"]!["url"]).ShouldBe([McpUrlGuard.UnresolvedMessage]);
        var token = Server(NewSlug(), "https://mcp.public.test/mcp", "Eu");
        token["authType"] = "OAuthClientCredentials";
        token["credential"] = new JsonObject { ["tokenUrl"] = "https://id.metadata.test/token", ["clientId"] = "c", ["clientSecret"] = "s" };
        Strings((await CreateAsync(admin, token, HttpStatusCode.BadRequest))["errors"]!["credential"]).ShouldBe([McpUrlGuard.ForbiddenMessage]);

        var org = await fixture.OrganisationAsync(admin);
        _ = await CreateAsync(admin, Server(NewSlug(), "http://mcp.private.test/mcp"));
        using var departmentAdmin = await fixture.ClientAsync("department-admin", org.Code);
        var internalServer = Server(NewSlug(), "http://mcp.private.test/mcp");
        internalServer["ownerDepartmentId"] = org.Department;
        Strings((await CreateAsync(departmentAdmin, internalServer, HttpStatusCode.BadRequest))["errors"]!["url"]).ShouldBe([McpUrlGuard.PrivateMessage]);
        var publicServer = Server(NewSlug(), "https://mcp.public.test/mcp", "Eu");
        publicServer["ownerDepartmentId"] = org.Department;
        _ = await CreateAsync(departmentAdmin, publicServer);
    }

    [Theory]
    [InlineData("10.0.0.1", McpAddressClass.Private)]
    [InlineData("172.20.1.1", McpAddressClass.Private)]
    [InlineData("192.168.1.1", McpAddressClass.Private)]
    [InlineData("100.64.0.1", McpAddressClass.Private)]
    [InlineData("127.0.0.1", McpAddressClass.Private)]
    [InlineData("::1", McpAddressClass.Private)]
    [InlineData("fd12::1", McpAddressClass.Private)]
    [InlineData("169.254.169.254", McpAddressClass.Forbidden)]
    [InlineData("::ffff:169.254.169.254", McpAddressClass.Forbidden)]
    [InlineData("fe80::1", McpAddressClass.Forbidden)]
    [InlineData("fd00:ec2::254", McpAddressClass.Forbidden)]
    [InlineData("168.63.129.16", McpAddressClass.Forbidden)]
    [InlineData("100.100.100.200", McpAddressClass.Forbidden)]
    [InlineData("224.0.0.1", McpAddressClass.Forbidden)]
    [InlineData("0.0.0.0", McpAddressClass.Forbidden)]
    [InlineData("::", McpAddressClass.Forbidden)]
    [InlineData("203.0.113.7", McpAddressClass.Public)]
    [InlineData("172.32.0.1", McpAddressClass.Public)]
    [InlineData("2001:db8::1", McpAddressClass.Public)]
    [InlineData("64:ff9b::a9fe:a9fe", McpAddressClass.Forbidden)]
    [InlineData("64:ff9b::a00:1", McpAddressClass.Private)]
    [InlineData("64:ff9b::cb00:7107", McpAddressClass.Public)]
    [InlineData("2002:a9fe:a9fe::1", McpAddressClass.Forbidden)]
    [InlineData("2002:c0a8:0101::1", McpAddressClass.Private)]
    [InlineData("::169.254.169.254", McpAddressClass.Forbidden)]
    [InlineData("::10.0.0.1", McpAddressClass.Private)]
    [InlineData("::ffff:10.0.0.1", McpAddressClass.Private)]
    [InlineData("fec0::1", McpAddressClass.Private)]
    [InlineData("100.127.255.255", McpAddressClass.Private)]
    [InlineData("100.128.0.1", McpAddressClass.Public)]
    public void Addresses_are_classified(string address, McpAddressClass expected) =>
        McpAddressGuard.Classify(IPAddress.Parse(address)).ShouldBe(expected);

    [Fact]
    public async Task Connections_to_forbidden_addresses_are_refused_after_dns()
    {
        using var http = new HttpClient(new SocketsHttpHandler { ConnectCallback = McpAddressGuard.ConnectAsync });
        var refused = await Should.ThrowAsync<HttpRequestException>(() => http.GetAsync("http://169.254.169.254/latest/meta-data/", Ct));
        refused.ToString().ShouldContain("not allowed");
    }

    [Fact]
    public async Task Department_admins_manage_only_their_departments_servers()
    {
        using var admin = await fixture.ClientAsync();
        var own = await fixture.OrganisationAsync(admin);
        var other = await fixture.OrganisationAsync(admin);
        var global = await CreateAsync(admin, Server(NewSlug(), "https://mcp.public.test/mcp", "Eu"));
        var mineBody = Server(NewSlug(), "https://mcp.public.test/mcp", "Eu");
        mineBody["ownerDepartmentId"] = own.Department;
        var mine = await CreateAsync(admin, mineBody);
        var theirsBody = Server(NewSlug(), "https://mcp.public.test/mcp", "Eu");
        theirsBody["ownerDepartmentId"] = other.Department;
        var theirs = await CreateAsync(admin, theirsBody);

        using var client = await fixture.ClientAsync("department-admin", own.Code);
        using (var list = await client.GetAsync("/api/mcp/servers", Ct))
        {
            var servers = (await AdminFixture.ReadAsync(list)).AsArray().ToDictionary(s => Id(s!), s => s!);
            servers.ShouldContainKey(Id(global));
            servers[Id(global)]["canManage"]!.GetValue<bool>().ShouldBeFalse();
            servers[Id(mine)]["canManage"]!.GetValue<bool>().ShouldBeTrue();
            servers[Id(mine)]["ownerDepartmentName"]!.GetValue<string>().ShouldStartWith("Department ");
            servers.ShouldNotContainKey(Id(theirs));
        }

        using (var hidden = await client.GetAsync($"/api/mcp/servers/{Id(theirs)}", Ct))
        {
            hidden.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        var globalBody = Server(global["slug"]!.GetValue<string>(), "https://mcp.public.test/mcp", "Eu");
        using (var update = await client.PutAsJsonAsync($"/api/mcp/servers/{Id(global)}", globalBody, Ct))
        {
            update.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var delete = await client.DeleteAsync($"/api/mcp/servers/{Id(global)}", Ct))
        {
            delete.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var sync = await client.PostAsync($"/api/mcp/servers/{Id(global)}/sync", null, Ct))
        {
            sync.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        _ = await CreateAsync(client, Server(NewSlug(), "https://mcp.public.test/mcp", "Eu"), HttpStatusCode.Forbidden);
        var elsewhere = Server(NewSlug(), "https://mcp.public.test/mcp", "Eu");
        elsewhere["ownerDepartmentId"] = other.Department;
        (await CreateAsync(client, elsewhere, HttpStatusCode.BadRequest))["errors"]!.AsObject().ContainsKey("ownerDepartmentId").ShouldBeTrue();
        var makeGlobal = Server(mine["slug"]!.GetValue<string>(), "https://mcp.public.test/mcp", "Eu");
        using (var update = await client.PutAsJsonAsync($"/api/mcp/servers/{Id(mine)}", makeGlobal, Ct))
        {
            update.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        mineBody["displayName"] = "Renamed";
        using (var update = await client.PutAsJsonAsync($"/api/mcp/servers/{Id(mine)}", mineBody, Ct))
        {
            (await AdminFixture.ReadAsync(update))["displayName"]!.GetValue<string>().ShouldBe("Renamed");
        }

        using var viewer = await fixture.ClientAsync("viewer", own.Code);
        using var denied = await viewer.GetAsync("/api/mcp/servers", Ct);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Credentials_are_write_only_and_encrypted()
    {
        using var client = await fixture.ClientAsync();
        var slug = NewSlug();
        var body = Server(slug, "https://mcp.public.test/mcp", "Eu");
        body["authType"] = "StaticHeaders";
        body["credential"] = new JsonObject { ["headers"] = new JsonObject { ["X-Api-Key"] = "s3cret-value-123" } };
        var created = await CreateAsync(client, body);
        created["hasCredential"]!.GetValue<bool>().ShouldBeTrue();
        created.ToJsonString().ShouldNotContain("s3cret");
        var id = Id(created);

        using (var list = await client.GetAsync("/api/mcp/servers", Ct))
        {
            (await list.Content.ReadAsStringAsync(Ct)).ShouldNotContain("s3cret");
        }

        using (var audit = await client.GetAsync("/api/audit?entityType=McpServer&pageSize=200", Ct))
        {
            var text = await audit.Content.ReadAsStringAsync(Ct);
            text.ShouldContain(id.ToString());
            text.ShouldNotContain("s3cret");
        }

        using (var scope = fixture.Services.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().McpServers.AsNoTracking().SingleAsync(s => s.Id == id, Ct);
            stored.EncryptedCredential.ShouldNotBeNull().ShouldNotContain("s3cret");
            var plaintext = scope.ServiceProvider.GetRequiredService<CredentialProtector>().Unprotect(stored.EncryptedCredential);
            JsonNode.Parse(plaintext!)!["X-Api-Key"]!.GetValue<string>().ShouldBe("s3cret-value-123");
        }

        body.Remove("credential");
        body["displayName"] = "Kept credential";
        using (var keep = await client.PutAsJsonAsync($"/api/mcp/servers/{id}", body, Ct))
        {
            (await AdminFixture.ReadAsync(keep))["hasCredential"]!.GetValue<bool>().ShouldBeTrue();
        }

        body["credential"] = new JsonObject { ["headers"] = new JsonObject { ["X-Api-Key"] = "s3cret-value-456" } };
        using (var replace = await client.PutAsJsonAsync($"/api/mcp/servers/{id}", body, Ct))
        {
            await AdminFixture.ReadAsync(replace);
        }

        using (var audit = await client.GetAsync("/api/audit?entityType=McpServer&pageSize=200", Ct))
        {
            var entry = (await AdminFixture.ReadAsync(audit))["items"]!.AsArray().First(e => e!["entityId"]!.GetValue<string>() == id.ToString())!;
            var details = JsonNode.Parse(entry["details"]!.GetValue<string>())!;
            details["before"]!["credentialReplaced"]!.GetValue<bool>().ShouldBeFalse();
            details["after"]!["credentialReplaced"]!.GetValue<bool>().ShouldBeTrue();
        }

        body.Remove("credential");
        body["url"] = "https://mcp.public.test/other-path";
        using (var samePlace = await client.PutAsJsonAsync($"/api/mcp/servers/{id}", body, Ct))
        {
            (await AdminFixture.ReadAsync(samePlace))["hasCredential"]!.GetValue<bool>().ShouldBeTrue();
        }

        // The stored secret never goes to a host the person who entered it did not choose, not even in an unsaved test.
        var moved = body.DeepClone().AsObject();
        moved["url"] = "https://elsewhere.public.test/mcp";
        using (var test = await client.PostAsJsonAsync($"/api/mcp/servers/{id}/test", moved, Ct))
        {
            (await AdminFixture.ReadAsync(test, HttpStatusCode.BadRequest))["errors"]!.AsObject().ContainsKey("credential").ShouldBeTrue();
        }

        using (var update = await client.PutAsJsonAsync($"/api/mcp/servers/{id}", moved, Ct))
        {
            (await AdminFixture.ReadAsync(update, HttpStatusCode.BadRequest))["errors"]!.AsObject().ContainsKey("credential").ShouldBeTrue();
        }

        body["authType"] = "OAuthClientCredentials";
        using (var switched = await client.PutAsJsonAsync($"/api/mcp/servers/{id}", body, Ct))
        {
            (await AdminFixture.ReadAsync(switched, HttpStatusCode.BadRequest))["errors"]!.AsObject().ContainsKey("credential").ShouldBeTrue();
        }

        body["authType"] = "None";
        using var none = await client.PutAsJsonAsync($"/api/mcp/servers/{id}", body, Ct);
        (await AdminFixture.ReadAsync(none))["hasCredential"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task Sync_pins_tools_until_an_admin_approves_them()
    {
        using var client = await fixture.ClientAsync();
        var slug = NewSlug();
        fixture.Mcp.SetTools(slug, McpTestServer.Tool("search"), McpTestServer.Tool("remove_me"));
        var id = Id(await CreateAsync(client, Server(slug)));

        var first = await SyncAsync(client, id);
        Strings(first["added"]).ShouldBe(["remove_me", "search"]);
        first["status"]!.GetValue<string>().ShouldBe("Healthy");
        using (var get = await client.GetAsync($"/api/mcp/servers/{id}", Ct))
        {
            var server = await AdminFixture.ReadAsync(get);
            Strings(server["capabilities"]).ShouldContain("Tools");
            server["instructions"]!.GetValue<string>().ShouldBe(McpTestServer.Instructions);
            server["lastCheckedAt"].ShouldNotBeNull();
            server["pendingToolCount"]!.GetValue<int>().ShouldBe(2);
        }

        fixture.Logs.Entries.Count(e => e.EventId.Name == "admin.mcp.tool_quarantined" && e.Message.Contains(slug, StringComparison.Ordinal)).ShouldBe(2);
        var tools = await ToolsAsync(client, id);
        var search = tools["search"];
        search["approvalState"]!.GetValue<string>().ShouldBe("Pending");
        search["isExposed"]!.GetValue<bool>().ShouldBeFalse();
        search["exposedName"]!.GetValue<string>().ShouldBe(slug + "__search");
        search["definition"]!["description"]!.GetValue<string>().ShouldBe("Söker ärenden.");
        search["definition"]!["annotations"]!["readOnlyHint"]!.GetValue<bool>().ShouldBeTrue();

        _ = await ApproveAsync(client, id, search, HttpStatusCode.Conflict, new string('0', 64));
        var approved = await ApproveAsync(client, id, search);
        approved["approvalState"]!.GetValue<string>().ShouldBe("Approved");
        approved["approvedBy"]!.GetValue<string>().ShouldBe("test-user");
        JsonNode.DeepEquals(approved["approvedDefinition"], approved["definition"]).ShouldBeTrue();

        fixture.Mcp.SetTools(slug, McpTestServer.Tool("search", "Söker och ÄNDRAR ärenden."));
        var changed = await SyncAsync(client, id);
        Strings(changed["changed"]).ShouldBe(["search"]);
        Strings(changed["removed"]).ShouldBe(["remove_me"]);
        tools = await ToolsAsync(client, id);
        tools.Keys.ShouldBe(["search"]);
        search = tools["search"];
        search["approvalState"]!.GetValue<string>().ShouldBe("Changed");
        search["isExposed"]!.GetValue<bool>().ShouldBeTrue();
        search["approvedDefinition"]!["description"]!.GetValue<string>().ShouldBe("Söker ärenden.");
        var change = search["changes"]!.AsArray().ShouldHaveSingleItem()!;
        change["path"]!.GetValue<string>().ShouldBe("/description");
        change["kind"]!.GetValue<string>().ShouldBe("Changed");
        change["before"]!.GetValue<string>().ShouldBe("Söker ärenden.");
        change["after"]!.GetValue<string>().ShouldBe("Söker och ÄNDRAR ärenden.");
        fixture.Logs.Entries.ShouldContain(e => e.EventId.Name == "admin.mcp.tool_quarantined" && e.Message.Contains(slug, StringComparison.Ordinal)
            && e.Message.Contains("changed", StringComparison.Ordinal));

        fixture.Mcp.SetTools(slug, McpTestServer.Tool("search"));
        var reverted = await SyncAsync(client, id);
        Strings(reverted["reverted"]).ShouldBe(["search"]);
        search = (await ToolsAsync(client, id))["search"];
        search["approvalState"]!.GetValue<string>().ShouldBe("Approved");
        search["changes"]!.AsArray().ShouldBeEmpty();

        var unchanged = await SyncAsync(client, id);
        foreach (var list in new[] { "added", "changed", "reverted", "updated", "removed", "errors" })
        {
            unchanged[list]!.AsArray().ShouldBeEmpty(list);
        }

        using var audit = await client.GetAsync("/api/audit?entityType=McpServer&pageSize=200", Ct);
        (await AdminFixture.ReadAsync(audit))["items"]!.AsArray().Count(e => e!["entityId"]!.GetValue<string>() == id.ToString() && e["action"]!.GetValue<string>() == "sync")
            .ShouldBe(4);
        using var toolAudit = await client.GetAsync("/api/audit?entityType=McpTool&pageSize=200", Ct);
        var approval = (await AdminFixture.ReadAsync(toolAudit))["items"]!.AsArray().First(e => e!["entityId"]!.GetValue<string>() == search["id"]!.GetValue<string>())!;
        approval["action"]!.GetValue<string>().ShouldBe("approve");
        JsonNode.Parse(approval["details"]!.GetValue<string>())!["after"]!["approvedDefinition"]!["description"]!.GetValue<string>().ShouldBe("Söker ärenden.");
    }

    [Fact]
    public async Task Rejected_and_pending_tools_follow_upstream_changes_without_being_exposed()
    {
        using var client = await fixture.ClientAsync();
        var slug = NewSlug();
        fixture.Mcp.SetTools(slug, McpTestServer.Tool("a"), McpTestServer.Tool("b"));
        var id = Id(await CreateAsync(client, Server(slug)));
        _ = await SyncAsync(client, id);
        var tools = await ToolsAsync(client, id);
        _ = await ApproveAsync(client, id, tools["a"]);
        using (var reject = await client.PostAsync($"/api/mcp/servers/{id}/tools/{tools["a"]["id"]}/reject", null, Ct))
        {
            var rejected = await AdminFixture.ReadAsync(reject);
            rejected["approvalState"]!.GetValue<string>().ShouldBe("Rejected");
            rejected["approvedDefinition"].ShouldBeNull();
        }

        fixture.Mcp.SetTools(slug, McpTestServer.Tool("a", "Ny."), McpTestServer.Tool("b", "Ny."));
        var sync = await SyncAsync(client, id);
        Strings(sync["updated"]).ShouldBe(["a", "b"]);
        tools = await ToolsAsync(client, id);
        tools["a"]["approvalState"]!.GetValue<string>().ShouldBe("Rejected");
        tools["b"]["approvalState"]!.GetValue<string>().ShouldBe("Pending");
        tools["b"]["definition"]!["description"]!.GetValue<string>().ShouldBe("Ny.");

        using var price = await client.PutAsJsonAsync($"/api/mcp/servers/{id}/tools/{tools["b"]["id"]}/price", new { priceSek = 0.25m }, Ct);
        (await AdminFixture.ReadAsync(price))["priceSek"]!.GetValue<decimal>().ShouldBe(0.25m);
    }

    [Fact]
    public async Task Exposed_name_collisions_are_sync_errors_never_overwrites()
    {
        using var client = await fixture.ClientAsync();
        var slug = NewSlug();
        fixture.Mcp.SetTools(slug, McpTestServer.Tool("case.search"), McpTestServer.Tool("case_search"));
        var id = Id(await CreateAsync(client, Server(slug)));
        var sync = await SyncAsync(client, id);
        Strings(sync["added"]).ShouldBe(["case.search"]);
        Strings(sync["errors"]).ShouldHaveSingleItem().ShouldContain("case_search");
        sync["status"]!.GetValue<string>().ShouldBe("Degraded");
        sync["lastError"]!.GetValue<string>().ShouldContain(slug + "__case_search");
        (await ToolsAsync(client, id)).Keys.ShouldBe(["case.search"]);
    }

    [Fact]
    public async Task Auto_approving_servers_approve_new_tools_but_not_changes()
    {
        using var client = await fixture.ClientAsync();
        var slug = NewSlug();
        fixture.Mcp.SetTools(slug, McpTestServer.Tool("lookup"));
        var body = Server(slug);
        body["autoApproveNewTools"] = true;
        var id = Id(await CreateAsync(client, body));
        _ = await SyncAsync(client, id);
        var lookup = (await ToolsAsync(client, id))["lookup"];
        lookup["approvalState"]!.GetValue<string>().ShouldBe("Approved");
        lookup["approvedBy"]!.GetValue<string>().ShouldBe("system:auto-approve");
        fixture.Logs.Entries.ShouldNotContain(e => e.EventId.Name == "admin.mcp.tool_quarantined" && e.Message.Contains(slug, StringComparison.Ordinal));

        fixture.Mcp.SetTools(slug, McpTestServer.Tool("lookup", "Ändrad."));
        Strings((await SyncAsync(client, id))["changed"]).ShouldBe(["lookup"]);
        (await ToolsAsync(client, id))["lookup"]["approvalState"]!.GetValue<string>().ShouldBe("Changed");
    }

    [Fact]
    public async Task Refused_credentials_mark_the_server_unavailable_and_raise_a_security_event()
    {
        using var client = await fixture.ClientAsync();
        var slug = NewSlug();
        fixture.Mcp.SetTools(slug, McpTestServer.Tool("search"));
        fixture.Mcp.RequireHeader(slug, "X-Api-Key", "right");
        var body = Server(slug);
        body["authType"] = "StaticHeaders";
        body["credential"] = new JsonObject { ["headers"] = new JsonObject { ["X-Api-Key"] = "wrong" } };
        var id = Id(await CreateAsync(client, body));

        var failed = await SyncAsync(client, id);
        failed["status"]!.GetValue<string>().ShouldBe("Unavailable");
        failed["lastError"]!.GetValue<string>().ShouldContain("HTTP 401");
        failed["lastError"]!.GetValue<string>().ShouldNotContain("wrong");
        var security = fixture.Logs.Entries.Where(e => e.EventId.Name == "admin.mcp.server_auth_failed" && e.Message.Contains(slug, StringComparison.Ordinal)).ShouldHaveSingleItem();
        security.Category.ShouldBe(SecurityEvents.Category);
        security.Message.ShouldNotContain("wrong");

        body["credential"] = new JsonObject { ["headers"] = new JsonObject { ["X-Api-Key"] = "right" } };
        using (var update = await client.PutAsJsonAsync($"/api/mcp/servers/{id}", body, Ct))
        {
            update.EnsureSuccessStatusCode();
        }

        var healthy = await SyncAsync(client, id);
        healthy["status"]!.GetValue<string>().ShouldBe("Healthy");
        Strings(healthy["added"]).ShouldBe(["search"]);
    }

    [Fact]
    public async Task Tests_connect_and_list_tools_without_saving()
    {
        using var client = await fixture.ClientAsync();
        var slug = NewSlug();
        fixture.Mcp.SetTools(slug, McpTestServer.Tool("search"));
        using (var unsaved = await client.PostAsJsonAsync("/api/mcp/servers/test", Server(slug), Ct))
        {
            var result = await AdminFixture.ReadAsync(unsaved);
            result["succeeded"]!.GetValue<bool>().ShouldBeTrue();
            Strings(result["capabilities"]).ShouldContain("Tools");
            result["instructions"]!.GetValue<string>().ShouldBe(McpTestServer.Instructions);
            var tool = result["tools"]!.AsArray().ShouldHaveSingleItem()!;
            tool["name"]!.GetValue<string>().ShouldBe("search");
            tool["exposedName"]!.GetValue<string>().ShouldBe(slug + "__search");
        }

        using (var list = await client.GetAsync("/api/mcp/servers", Ct))
        {
            (await list.Content.ReadAsStringAsync(Ct)).ShouldNotContain(slug);
        }

        var id = Id(await CreateAsync(client, Server(slug)));
        using (var stored = await client.PostAsync($"/api/mcp/servers/{id}/test", null, Ct))
        {
            (await AdminFixture.ReadAsync(stored))["succeeded"]!.GetValue<bool>().ShouldBeTrue();
        }

        (await ToolsAsync(client, id)).ShouldBeEmpty();

        fixture.Mcp.RequireHeader(slug, "X-Api-Key", "right");
        using (var refused = await client.PostAsync($"/api/mcp/servers/{id}/test", null, Ct))
        {
            var result = await AdminFixture.ReadAsync(refused);
            result["succeeded"]!.GetValue<bool>().ShouldBeFalse();
            result["httpStatus"]!.GetValue<int>().ShouldBe(401);
        }

        var withKey = Server(slug);
        withKey["authType"] = "StaticHeaders";
        withKey["credential"] = new JsonObject { ["headers"] = new JsonObject { ["X-Api-Key"] = "right" } };
        using (var changes = await client.PostAsJsonAsync($"/api/mcp/servers/{id}/test", withKey, Ct))
        {
            (await AdminFixture.ReadAsync(changes))["succeeded"]!.GetValue<bool>().ShouldBeTrue();
        }

        using var get = await client.GetAsync($"/api/mcp/servers/{id}", Ct);
        (await AdminFixture.ReadAsync(get))["authType"]!.GetValue<string>().ShouldBe("None");
    }

    [Fact]
    public async Task Sync_checks_the_stored_address_again()
    {
        using var client = await fixture.ClientAsync();
        var server = new McpServer { Slug = NewSlug(), DisplayName = "Metadata", Url = "http://169.254.169.254/mcp", Residency = DataResidency.OnPrem, CreatedAt = DateTimeOffset.UtcNow };
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            db.McpServers.Add(server);
            await db.SaveChangesAsync(Ct);
        }

        var sync = await SyncAsync(client, server.Id);
        sync["status"]!.GetValue<string>().ShouldBe("Unavailable");
        sync["lastError"]!.GetValue<string>().ShouldBe(McpUrlGuard.ForbiddenMessage);
        using var delete = await client.DeleteAsync($"/api/mcp/servers/{server.Id}", Ct);
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task One_sync_runs_at_a_time()
    {
        using var client = await fixture.ClientAsync();
        var slug = NewSlug();
        fixture.Mcp.SetTools(slug, McpTestServer.Tool("search"));
        var enabled = Id(await CreateAsync(client, Server(slug)));
        var disabledSlug = NewSlug();
        fixture.Mcp.SetTools(disabledSlug, McpTestServer.Tool("search"));
        var disabledBody = Server(disabledSlug);
        disabledBody["isEnabled"] = false;
        var disabled = Id(await CreateAsync(client, disabledBody));

        var syncLock = fixture.Services.GetRequiredService<IMcpSyncLock>();
        var worker = new McpSyncWorker(fixture.Services.GetRequiredService<IServiceScopeFactory>(), syncLock, Options.Create(new AdminOptions()),
            TimeProvider.System, NullLogger<McpSyncWorker>.Instance);
        var token = (await syncLock.TryAcquireAsync(TimeSpan.FromMinutes(1), Ct)).ShouldNotBeNull();
        try
        {
            using var busy = await client.PostAsync($"/api/mcp/servers/{enabled}/sync", null, Ct);
            busy.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await worker.SyncAllAsync(Ct)).ShouldBe(0);
        }
        finally
        {
            await syncLock.ReleaseAsync(token, Ct);
        }

        (await worker.SyncAllAsync(Ct)).ShouldBeGreaterThan(0);
        (await ToolsAsync(client, enabled)).Keys.ShouldBe(["search"]);
        (await ToolsAsync(client, disabled)).ShouldBeEmpty();
        await syncLock.ReleaseAsync((await syncLock.TryAcquireAsync(TimeSpan.FromSeconds(10), Ct)).ShouldNotBeNull(), Ct);
    }

    private static object KeyBody(Guid team, int? mcpCallsPerMinute = null) => new
    {
        teamId = team, name = "MCP key", allowedModels = Array.Empty<string>(), allowedResidencies = Array.Empty<string>(), piiPolicy = "Redact",
        mcpCallsPerMinute,
    };

    [Fact]
    public async Task Grants_are_validated_scoped_audited_and_copied_on_rotation()
    {
        using var admin = await fixture.ClientAsync();
        var own = await fixture.OrganisationAsync(admin);
        var other = await fixture.OrganisationAsync(admin);
        var slug = NewSlug();
        fixture.Mcp.SetTools(slug, McpTestServer.Tool("search"));
        var server = Id(await CreateAsync(admin, Server(slug)));
        _ = await SyncAsync(admin, server);
        var theirsBody = Server(NewSlug(), "https://mcp.public.test/mcp", "Eu");
        theirsBody["ownerDepartmentId"] = other.Department;
        var theirs = Id(await CreateAsync(admin, theirsBody));

        using var keyResponse = await admin.PostAsJsonAsync("/api/keys", KeyBody(own.Team, 5), Ct);
        var key = (await AdminFixture.ReadAsync(keyResponse, HttpStatusCode.Created))["key"]!;
        key["mcpCallsPerMinute"]!.GetValue<int>().ShouldBe(5);
        var keyId = Id(key);

        using (var put = await admin.PutAsJsonAsync($"/api/keys/{keyId}/mcp-grants", new object[]
        {
            new { serverId = server, allowedTools = new[] { "search", "future_tool" }, approvalPolicy = "NeverForReadOnly", allowResources = true },
            new { serverId = theirs, allowedTools = new[] { "x", "*" } },
        }, Ct))
        {
            var grants = (await AdminFixture.ReadAsync(put)).AsArray().ToDictionary(g => g!["serverId"]!.GetValue<Guid>(), g => g!);
            Strings(grants[server]["allowedTools"]).ShouldBe(["search", "future_tool"]);
            Strings(grants[server]["unknownTools"]).ShouldBe(["future_tool"]);
            grants[server]["approvalPolicy"]!.GetValue<string>().ShouldBe("NeverForReadOnly");
            grants[server]["allowResources"]!.GetValue<bool>().ShouldBeTrue();
            grants[server]["allowPrompts"]!.GetValue<bool>().ShouldBeFalse();
            grants[server]["serverSlug"]!.GetValue<string>().ShouldBe(slug);
            Strings(grants[theirs]["allowedTools"]).ShouldBe(["*"]);
        }

        foreach (var invalid in new object[]
        {
            new object[] { new { serverId = Guid.NewGuid(), allowedTools = new[] { "*" } } },
            new object[] { new { serverId = server, allowedTools = new[] { "*" } }, new { serverId = server, allowedTools = new[] { "search" } } },
            new object[] { new { serverId = server, allowedTools = new[] { "" } } },
        })
        {
            using var refused = await admin.PutAsJsonAsync($"/api/keys/{keyId}/mcp-grants", invalid, Ct);
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using var departmentAdmin = await fixture.ClientAsync("department-admin", own.Code);
        using (var get = await departmentAdmin.GetAsync($"/api/keys/{keyId}/mcp-grants", Ct))
        {
            (await AdminFixture.ReadAsync(get)).AsArray().Select(g => g!["serverId"]!.GetValue<Guid>()).ShouldBe([server]);
        }

        using (var hidden = await departmentAdmin.PutAsJsonAsync($"/api/keys/{keyId}/mcp-grants", new object[] { new { serverId = theirs, allowedTools = new[] { "*" } } }, Ct))
        {
            hidden.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var cleared = await departmentAdmin.PutAsJsonAsync($"/api/keys/{keyId}/mcp-grants", Array.Empty<object>(), Ct))
        {
            (await AdminFixture.ReadAsync(cleared)).AsArray().ShouldBeEmpty();
        }

        using (var get = await admin.GetAsync($"/api/keys/{keyId}/mcp-grants", Ct))
        {
            (await AdminFixture.ReadAsync(get)).AsArray().Select(g => g!["serverId"]!.GetValue<Guid>()).ShouldBe([theirs]);
        }

        using (var audit = await admin.GetAsync("/api/audit?entityType=McpGrants&pageSize=200", Ct))
        {
            (await AdminFixture.ReadAsync(audit))["items"]!.AsArray().Count(e => e!["entityId"]!.GetValue<string>() == keyId.ToString()).ShouldBe(2);
        }

        using (var rotate = await admin.PostAsJsonAsync($"/api/keys/{keyId}/rotate", new { mode = "Grace24Hours" }, Ct))
        {
            var replacement = (await AdminFixture.ReadAsync(rotate))["key"]!;
            replacement["mcpCallsPerMinute"]!.GetValue<int>().ShouldBe(5);
            using var copied = await admin.GetAsync($"/api/keys/{Id(replacement)}/mcp-grants", Ct);
            var grant = (await AdminFixture.ReadAsync(copied)).AsArray().ShouldHaveSingleItem()!;
            grant["serverId"]!.GetValue<Guid>().ShouldBe(theirs);
            Strings(grant["allowedTools"]).ShouldBe(["*"]);
        }

        using var unknownKey = await admin.GetAsync($"/api/keys/{Guid.NewGuid()}/mcp-grants", Ct);
        unknownKey.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Mcp_calls_per_minute_is_edited_with_the_key()
    {
        using var admin = await fixture.ClientAsync();
        var own = await fixture.OrganisationAsync(admin);
        using var create = await admin.PostAsJsonAsync("/api/keys", KeyBody(own.Team), Ct);
        var key = (await AdminFixture.ReadAsync(create, HttpStatusCode.Created))["key"]!;
        key["mcpCallsPerMinute"].ShouldBeNull();
        using var update = await admin.PutAsJsonAsync($"/api/keys/{Id(key)}", KeyBody(own.Team, 7), Ct);
        (await AdminFixture.ReadAsync(update))["mcpCallsPerMinute"]!.GetValue<int>().ShouldBe(7);
        using var invalid = await admin.PutAsJsonAsync($"/api/keys/{Id(key)}", KeyBody(own.Team, 0), Ct);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Export_and_import_carry_servers_and_approvals_without_credentials()
    {
        using var client = await fixture.ClientAsync();
        var org = await fixture.OrganisationAsync(client);
        var slug = NewSlug();
        fixture.Mcp.SetTools(slug, McpTestServer.Tool("search"), McpTestServer.Tool("other"));
        var body = Server(slug);
        body["authType"] = "StaticHeaders";
        body["credential"] = new JsonObject { ["headers"] = new JsonObject { ["X-Api-Key"] = "export-secret" } };
        body["ownerDepartmentId"] = org.Department;
        body["forwardHeaders"] = new JsonArray("X-Trace-Id");
        var id = Id(await CreateAsync(client, body));
        _ = await SyncAsync(client, id);
        var tools = await ToolsAsync(client, id);
        _ = await ApproveAsync(client, id, tools["search"]);
        using (var price = await client.PutAsJsonAsync($"/api/mcp/servers/{id}/tools/{tools["search"]["id"]}/price", new { priceSek = 1.5m }, Ct))
        {
            price.EnsureSuccessStatusCode();
        }

        using (var reject = await client.PostAsync($"/api/mcp/servers/{id}/tools/{tools["other"]["id"]}/reject", null, Ct))
        {
            reject.EnsureSuccessStatusCode();
        }

        using var export = await client.GetAsync("/api/ops/config/export", Ct);
        var exportText = await export.Content.ReadAsStringAsync(Ct);
        exportText.ShouldNotContain("export-secret");
        var exported = JsonNode.Parse(exportText)!["mcpServers"]!.AsArray().Single(s => s!["configuration"]!["slug"]!.GetValue<string>() == slug)!;
        exported["ownerDepartmentCode"]!.GetValue<string>().ShouldBe(org.Code);
        exported["configuration"]!.AsObject().ContainsKey("credential").ShouldBeFalse();
        exported["configuration"]!["authType"]!.GetValue<string>().ShouldBe("StaticHeaders");
        var exportedTools = exported["tools"]!.AsArray().ToDictionary(t => t!["name"]!.GetValue<string>(), t => t!);
        exportedTools["search"]["approvalState"]!.GetValue<string>().ShouldBe("Approved");
        exportedTools["search"]["approvedDefinition"]!["description"]!.GetValue<string>().ShouldBe("Söker ärenden.");
        exportedTools["search"]["priceSek"]!.GetValue<decimal>().ShouldBe(1.5m);
        exportedTools["other"]["approvalState"]!.GetValue<string>().ShouldBe("Rejected");

        using (var delete = await client.DeleteAsync($"/api/mcp/servers/{id}", Ct))
        {
            delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        JsonObject Document(JsonNode server) => new()
        {
            ["schemaVersion"] = 1, ["providers"] = new JsonArray(), ["models"] = new JsonArray(), ["routes"] = new JsonArray(),
            ["mcpServers"] = new JsonArray(server.DeepClone()),
        };

        using (var import = await client.PostAsJsonAsync("/api/ops/config/import", Document(exported), Ct))
        {
            var counts = await AdminFixture.ReadAsync(import);
            counts["created"]!.GetValue<int>().ShouldBe(2);
            counts["skipped"]!.GetValue<int>().ShouldBe(1);
        }

        using (var list = await client.GetAsync("/api/mcp/servers", Ct))
        {
            var server = (await AdminFixture.ReadAsync(list)).AsArray().Single(s => s!["slug"]!.GetValue<string>() == slug)!;
            server["hasCredential"]!.GetValue<bool>().ShouldBeFalse();
            server["ownerDepartmentId"]!.GetValue<Guid>().ShouldBe(org.Department);
            Strings(server["forwardHeaders"]).ShouldBe(["X-Trace-Id"]);
            id = Id(server);
        }

        var search = (await ToolsAsync(client, id)).ShouldHaveSingleItem().Value;
        search["approvalState"]!.GetValue<string>().ShouldBe("Approved");
        search["priceSek"]!.GetValue<decimal>().ShouldBe(1.5m);
        search["exposedName"]!.GetValue<string>().ShouldBe(slug + "__search");

        var reference = "UME_MCP_TEST_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        Environment.SetEnvironmentVariable(reference, """{"X-Api-Key":"from-environment"}""");
        try
        {
            var withEnvironment = exported.DeepClone();
            withEnvironment["credentialEnvironment"] = reference;
            using var import = await client.PostAsJsonAsync("/api/ops/config/import", Document(withEnvironment), Ct);
            (await AdminFixture.ReadAsync(import))["updated"]!.GetValue<int>().ShouldBe(2);
        }
        finally
        {
            Environment.SetEnvironmentVariable(reference, null);
        }

        using (var get = await client.GetAsync($"/api/mcp/servers/{id}", Ct))
        {
            (await AdminFixture.ReadAsync(get))["hasCredential"]!.GetValue<bool>().ShouldBeTrue();
        }

        var withSecret = exported.DeepClone();
        withSecret["configuration"]!["credential"] = new JsonObject { ["headers"] = new JsonObject { ["X-Api-Key"] = "plain" } };
        using var refused = await client.PostAsJsonAsync("/api/ops/config/import", Document(withSecret), Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        fixture.Mcp.RequireHeader(slug, "X-Api-Key", "from-environment");
        var sync = await SyncAsync(client, id);
        Strings(sync["added"]).ShouldBe(["other"]);
        sync["changed"]!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task Ops_health_shows_mcp_servers_and_their_circuit_can_be_forced()
    {
        using var client = await fixture.ClientAsync();
        var slug = NewSlug();
        fixture.Mcp.SetTools(slug, McpTestServer.Tool("search"));
        var id = Id(await CreateAsync(client, Server(slug)));
        _ = await SyncAsync(client, id);

        async Task<JsonNode> HealthAsync()
        {
            using var response = await client.GetAsync("/api/ops/health", Ct);
            return (await AdminFixture.ReadAsync(response))["mcpServers"]!.AsArray().Single(s => Id(s!) == id)!;
        }

        var health = await HealthAsync();
        health["status"]!.GetValue<string>().ShouldBe("Healthy");
        health["pendingTools"]!.GetValue<int>().ShouldBe(1);
        health["circuitState"]!.GetValue<string>().ShouldBe("Closed");

        using (var open = await client.PostAsJsonAsync($"/api/ops/mcp-servers/{id}/circuit", new { state = "Open" }, Ct))
        {
            open.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await HealthAsync())["circuitState"]!.GetValue<string>().ShouldBe("Open");
        using (var close = await client.PostAsJsonAsync($"/api/ops/mcp-servers/{id}/circuit", new { state = "Closed" }, Ct))
        {
            close.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await HealthAsync())["circuitState"]!.GetValue<string>().ShouldBe("Closed");
        using var missing = await client.PostAsJsonAsync($"/api/ops/mcp-servers/{Guid.NewGuid()}/circuit", new { state = "Open" }, Ct);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Usage_and_pii_incidents_filter_mcp_traffic()
    {
        using var admin = await fixture.ClientAsync();
        var own = await fixture.OrganisationAsync(admin);
        using var keyResponse = await admin.PostAsJsonAsync("/api/keys", KeyBody(own.Team), Ct);
        var key = Id((await AdminFixture.ReadAsync(keyResponse, HttpStatusCode.Created))["key"]!);
        UsageRecord Row(GatewayEndpoint endpoint, string model) => new()
        {
            RequestId = "req_" + Guid.NewGuid().ToString("N"), Timestamp = DateTimeOffset.UtcNow, VirtualKeyId = key, TeamId = own.Team, DepartmentId = own.Department,
            Endpoint = endpoint, RequestedModel = model, Outcome = RequestOutcome.Success, StatusCode = 200, PiiCategories = "Email:1", PiiActionApplied = PiiPolicy.Redact,
        };
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            db.UsageRecords.AddRange(Row(GatewayEndpoint.ChatCompletions, "ume/chat"), Row(GatewayEndpoint.Mcp, "mcp:demo/echo"));
            await db.SaveChangesAsync(Ct);
        }

        async Task<string[]> ModelsAsync(string path)
        {
            using var response = await admin.GetAsync(path, Ct);
            return [.. (await AdminFixture.ReadAsync(response))["items"]!.AsArray().Select(r => r!["requestedModel"]!.GetValue<string>())];
        }

        (await ModelsAsync($"/api/usage/requests?keyId={key}")).ShouldBe(["ume/chat", "mcp:demo/echo"], ignoreOrder: true);
        (await ModelsAsync($"/api/usage/requests?keyId={key}&traffic=mcp")).ShouldBe(["mcp:demo/echo"]);
        (await ModelsAsync($"/api/usage/requests?keyId={key}&traffic=LLM")).ShouldBe(["ume/chat"]);
        (await ModelsAsync($"/api/usage/pii-incidents?keyId={key}&traffic=mcp")).ShouldBe(["mcp:demo/echo"]);
        using var invalid = await admin.GetAsync($"/api/usage/requests?keyId={key}&traffic=other", Ct);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
