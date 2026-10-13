using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Security;
using Ume.LlmGateway.Infrastructure.Stores;
using Ume.LlmGateway.TestKit;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>
/// The MCP gateway end to end: the gateway (TestServer) in front of an SDK-built MCP server on a loopback port. Every
/// test registers its own servers (unique slugs and upstream paths) and keys, so tests do not see each other's data.
/// </summary>
public sealed partial class McpGatewayTests(GatewayFixture fixture) : IAsyncLifetime
{
    private const string LookupDefinition = """{"description":"Finds a case.","inputSchema":{"type":"object","properties":{"number":{"type":"string"}},"required":["number"]},"annotations":{"readOnlyHint":true}}""";
    private const string EchoDefinition = """{"description":"Echoes its arguments.","inputSchema":{"type":"object"},"annotations":{"readOnlyHint":true}}""";
    private const string ReadDefinition = """{"description":"Reads.","inputSchema":{"type":"object"},"annotations":{"readOnlyHint":true}}""";
    private const string DeleteDefinition = """{"description":"Deletes.","inputSchema":{"type":"object"},"annotations":{"destructiveHint":true}}""";
    private const string BigDefinition = """{"description":"Big.","inputSchema":{"type":"object"}}""";

    private readonly McpUpstreamServer _upstream = new();
    private readonly List<Guid> _servers = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _upstream.StartAsync();
        await _responses.StartAsync();
        await _messages.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            await db.McpServers.Where(s => _servers.Contains(s.Id)).ExecuteDeleteAsync(CancellationToken.None);
        }

        await _upstream.DisposeAsync();
        await _responses.DisposeAsync();
        await _messages.DisposeAsync();
    }

    private async Task<McpServer> AddServerAsync(Action<McpServer>? configure = null, params (string Name, McpToolApprovalState State, string Definition)[] tools)
    {
        var slug = "t" + Guid.NewGuid().ToString("N")[..12];
        var server = new McpServer
        {
            Slug = slug, DisplayName = "Test " + slug, Url = _upstream.Endpoint(slug), Residency = DataResidency.OnPrem,
            Status = McpServerStatus.Healthy, CreatedAt = DateTimeOffset.UtcNow,
            Capabilities = McpServerCapabilities.Tools | McpServerCapabilities.Resources | McpServerCapabilities.Prompts,
            Tools = [.. tools.Select(t => new McpTool
            {
                Name = t.Name, Definition = t.Definition, DefinitionHash = new string('0', 64), ApprovalState = t.State,
                ApprovedDefinition = t.State is McpToolApprovalState.Pending ? null : t.Definition,
                ExposedName = McpToolNames.Exposed(slug, t.Name), DiscoveredAt = DateTimeOffset.UtcNow,
            })],
        };
        configure?.Invoke(server);
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        db.McpServers.Add(server);
        await db.SaveChangesAsync(Ct);
        _servers.Add(server.Id);
        return server;
    }

    private Task GrantAsync(TestKey key, McpServer server, params string[] tools) => GrantAsync(key, server, tools, resources: false, prompts: false);

    private async Task GrantAsync(TestKey key, McpServer server, string[] tools, bool resources, bool prompts)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        db.VirtualKeyMcpGrants.Add(new VirtualKeyMcpGrant
        {
            VirtualKeyId = key.Id, McpServerId = server.Id, AllowedTools = [.. tools], AllowResources = resources, AllowPrompts = prompts, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
    }

    private async Task<(TestKey Key, McpServer Server)> GrantedServerAsync(Action<VirtualKey>? key = null, Action<McpServer>? server = null)
    {
        var testKey = await fixture.CreateKeyAsync(key);
        var mcpServer = await AddServerAsync(server,
            ("lookup.case", McpToolApprovalState.Approved, LookupDefinition), ("echo_args", McpToolApprovalState.Approved, EchoDefinition),
            ("flaky_read", McpToolApprovalState.Approved, ReadDefinition), ("flaky_delete", McpToolApprovalState.Approved, DeleteDefinition),
            ("big", McpToolApprovalState.Approved, BigDefinition), ("contact", McpToolApprovalState.Approved, ReadDefinition),
            ("fail_pii", McpToolApprovalState.Approved, ReadDefinition));
        await GrantAsync(testKey, mcpServer, "*");
        return (testKey, mcpServer);
    }

    private async Task<(HttpResponseMessage Response, JsonObject? Body)> RpcAsync(TestKey? key, string method, JsonObject? parameters = null, string path = "/mcp",
        IDictionary<string, string>? headers = null, bool stateless = true, bool notification = false, HttpClient? client = null)
    {
        parameters ??= [];
        if (stateless)
        {
            parameters["_meta"] = new JsonObject
            {
                ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
            };
        }

        var body = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters };
        if (!notification)
        {
            body["id"] = 1;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        if (stateless)
        {
            request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        }

        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        }

        var response = await (client ?? fixture.Client).SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text) as JsonObject);
    }

    private Task<(HttpResponseMessage Response, JsonObject? Body)> CallAsync(TestKey key, string tool, JsonObject? arguments = null, string path = "/mcp",
        IDictionary<string, string>? headers = null) =>
        RpcAsync(key, "tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments ?? [] }, path, headers);

    private async Task<string[]> ListAsync(TestKey key, string path = "/mcp")
    {
        var (response, body) = await RpcAsync(key, "tools/list", path: path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return [.. body!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>())];
    }

    [Fact]
    public async Task Unauthenticated_requests_get_401_with_a_bearer_challenge()
    {
        var (response, body) = await RpcAsync(null, "tools/list");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer");
        body!["error"]!["code"]!.GetValue<string>().ShouldBe("invalid_api_key");

        var revoked = await fixture.CreateKeyAsync(k => k.RevokedAt = DateTimeOffset.UtcNow.AddMinutes(-1));
        (response, _) = await RpcAsync(revoked, "tools/list");
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer");
    }

    [Fact]
    public async Task Tools_list_shows_only_granted_enabled_in_residency_approved_tools()
    {
        var key = await fixture.CreateKeyAsync(k => k.AllowedResidencies = [DataResidency.OnPrem, DataResidency.Eu]);
        var changedDefinition = """{"description":"New, unapproved description.","inputSchema":{"type":"object"}}""";
        var all = await AddServerAsync(null,
            ("lookup.case", McpToolApprovalState.Approved, LookupDefinition), ("pending", McpToolApprovalState.Pending, EchoDefinition),
            ("rejected", McpToolApprovalState.Rejected, EchoDefinition), ("changed", McpToolApprovalState.Changed, changedDefinition));
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var changed = await db.McpTools.SingleAsync(t => t.McpServerId == all.Id && t.Name == "changed", Ct);
            changed.ApprovedDefinition = EchoDefinition;
            await db.SaveChangesAsync(Ct);
        }

        var partial = await AddServerAsync(null, ("a", McpToolApprovalState.Approved, EchoDefinition), ("b", McpToolApprovalState.Approved, EchoDefinition));
        var notGranted = await AddServerAsync(null, ("x", McpToolApprovalState.Approved, EchoDefinition));
        var disabled = await AddServerAsync(s => s.IsEnabled = false, ("x", McpToolApprovalState.Approved, EchoDefinition));
        var external = await AddServerAsync(s => s.Residency = DataResidency.External, ("x", McpToolApprovalState.Approved, EchoDefinition));
        await GrantAsync(key, all, "*");
        await GrantAsync(key, partial, "a");
        await GrantAsync(key, disabled, "*");
        await GrantAsync(key, external, "*");
        _ = notGranted;

        var (response, body) = await RpcAsync(key, "tools/list");
        var tools = body!["result"]!["tools"]!.AsArray();
        var byName = tools.ToDictionary(t => t!["name"]!.GetValue<string>(), t => t!);
        byName.Keys.ShouldBe(new[] { $"{all.Slug}__changed", $"{all.Slug}__lookup_case", $"{partial.Slug}__a" }.Order(StringComparer.Ordinal));
        byName[$"{all.Slug}__changed"]["description"]!.GetValue<string>().ShouldBe("Echoes its arguments.");
        byName[$"{all.Slug}__lookup_case"]["inputSchema"]!["required"]![0]!.GetValue<string>().ShouldBe("number");
        byName[$"{all.Slug}__lookup_case"]["annotations"]!["readOnlyHint"]!.GetValue<bool>().ShouldBeTrue();
        body["result"]!["cacheScope"]!.GetValue<string>().ShouldBe("private");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();

        (await ListAsync(await fixture.CreateKeyAsync())).ShouldBeEmpty();
    }

    [Fact]
    public async Task Calling_a_hidden_or_ungranted_tool_looks_exactly_like_an_unknown_tool()
    {
        var key = await fixture.CreateKeyAsync();
        var server = await AddServerAsync(null, ("granted", McpToolApprovalState.Approved, EchoDefinition),
            ("other", McpToolApprovalState.Approved, EchoDefinition), ("pending", McpToolApprovalState.Pending, EchoDefinition));
        var ungrantedServer = await AddServerAsync(null, ("x", McpToolApprovalState.Approved, EchoDefinition));
        await GrantAsync(key, server, "granted");

        async Task<string> ErrorAsync(string name)
        {
            var (response, body) = await CallAsync(key, name);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            return body!["error"]!.ToJsonString().Replace(name, "NAME", StringComparison.Ordinal);
        }

        var unknown = await ErrorAsync("no_such_tool");
        JsonNode.Parse(unknown)!["message"]!.GetValue<string>().ShouldBe("Unknown tool: 'NAME'");
        JsonNode.Parse(unknown)!["code"]!.GetValue<int>().ShouldBe(-32602);
        (await ErrorAsync($"{server.Slug}__other")).ShouldBe(unknown);
        (await ErrorAsync($"{server.Slug}__pending")).ShouldBe(unknown);
        (await ErrorAsync($"{ungrantedServer.Slug}__x")).ShouldBe(unknown);
        _upstream.Requests.ShouldBeEmpty();
        fixture.Logs.Text.ShouldContain("tool_not_granted");
    }

    [Fact]
    public async Task Image_and_structured_content_pass_through_unchanged()
    {
        var (key, server) = await GrantedServerAsync();
        var (response, body) = await CallAsync(key, $"{server.Slug}__lookup_case", new JsonObject { ["number"] = "KS-1" });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var direct = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(_upstream.Endpoint(server.Slug)), TransportMode = HttpTransportMode.StreamableHttp }),
            loggerFactory: NullLoggerFactory.Instance, cancellationToken: Ct);
        var expected = (await direct.SendRequestAsync(new ModelContextProtocol.Protocol.JsonRpcRequest
        {
            Method = "tools/call",
            Params = new JsonObject { ["name"] = "lookup.case", ["arguments"] = new JsonObject { ["number"] = "KS-1" } },
        }, Ct)).Result;

        JsonNode.DeepEquals(body!["result"], expected).ShouldBeTrue();
        body["result"]!["content"]![1]!["data"]!.GetValue<string>().ShouldBe(McpUpstreamServer.PixelPng);
        body["result"]!["structuredContent"]!["tags"]!.AsArray().Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_call_is_accounted_once_with_the_mcp_fields_and_priced_tools_charge_the_budget()
    {
        var (key, server) = await GrantedServerAsync();
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            (await db.McpTools.SingleAsync(t => t.McpServerId == server.Id && t.Name == "lookup.case", Ct)).PriceSek = 2.5m;
            await db.SaveChangesAsync(Ct);
        }

        await fixture.AddBudgetAsync(BudgetScope.VirtualKey, key.Id, 4m);
        var (response, _) = await CallAsync(key, $"{server.Slug}__lookup_case", new JsonObject { ["number"] = "KS-2" });
        response.Headers.GetValues("x-ume-cost-sek").Single().ShouldBe("2.500000");

        var usage = await fixture.UsageAsync(response);
        usage.Endpoint.ShouldBe(GatewayEndpoint.Mcp);
        usage.McpServerId.ShouldBe(server.Id);
        usage.McpMethod.ShouldBe("tools/call");
        usage.McpTool.ShouldBe("lookup.case");
        usage.RequestedModel.ShouldBe($"mcp:{server.Slug}/lookup.case");
        usage.Outcome.ShouldBe(RequestOutcome.Success);
        usage.StatusCode.ShouldBe(200);
        usage.CostSek.ShouldBe(2.5m);
        usage.ProviderAccountId.ShouldBeNull();

        // 2.5 of 4 SEK is spent: the next 2.5 SEK call does not fit.
        var (_, refused) = await CallAsync(key, $"{server.Slug}__lookup_case", new JsonObject { ["number"] = "KS-3" });
        refused!["error"]!["code"]!.GetValue<int>().ShouldBe(-32001);
        _upstream.For(server.Slug).Count(r => r.Method == "tools/call").ShouldBe(1);

        // A budget counter rebuilt from Postgres (cold start, new instance) counts the MCP call.
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var budget = await db.Budgets.SingleAsync(b => b.Scope == BudgetScope.VirtualKey && b.ScopeId == key.Id, Ct);
            (await db.SpentAsync(budget, BudgetPeriods.GetWindow(budget.Period, DateTimeOffset.UtcNow), Ct)).ShouldBe(2.5m);
        }

        // Discovery is answered by the gateway and not accounted.
        var (list, _) = await RpcAsync(key, "tools/list");
        await fixture.Services.GetRequiredService<Pipeline.UsageWriter>().FlushAsync(Ct);
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var requestId = list.Headers.GetValues("x-request-id").Single();
            (await db.UsageRecords.AnyAsync(u => u.RequestId == requestId, Ct)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Arguments_are_checked_against_the_approved_input_schema()
    {
        var (key, server) = await GrantedServerAsync();
        var (_, body) = await CallAsync(key, $"{server.Slug}__lookup_case", new JsonObject { ["number"] = 5 });

        body!["error"]!["code"]!.GetValue<int>().ShouldBe(-32602);
        body["error"]!["message"]!.GetValue<string>().ShouldContain("/number");
        _upstream.For(server.Slug).ShouldNotContain(r => r.Method == "tools/call");
    }

    [Fact]
    public async Task Pii_is_redacted_in_arguments_and_results()
    {
        var (key, server) = await GrantedServerAsync(k => k.PiiPolicy = PiiPolicy.Redact);
        var (response, body) = await CallAsync(key, $"{server.Slug}__echo_args", new JsonObject { ["note"] = "Mejla anna.svensson@example.se" });

        var sent = _upstream.For(server.Slug).Single(r => r.Method == "tools/call").Body;
        sent.ShouldNotContain("anna.svensson@example.se");
        sent.ShouldContain("[E-POST]");
        body!["result"]!["structuredContent"]!["note"]!.GetValue<string>().ShouldBe("Mejla [E-POST]");
        response.Headers.GetValues("x-ume-pii").Single().ShouldBe("redacted");
        var usage = await fixture.UsageAsync(response);
        usage.PiiCategories.ShouldBe("Email:1");
        usage.PiiActionApplied.ShouldBe(PiiPolicy.Redact);
    }

    [Fact]
    public async Task Pii_found_only_in_a_result_is_redacted_or_blocks_the_answer()
    {
        var (redactKey, server) = await GrantedServerAsync(k => k.PiiPolicy = PiiPolicy.Redact);
        var blockKey = await fixture.CreateKeyAsync(k => k.PiiPolicy = PiiPolicy.Block);
        await GrantAsync(blockKey, server, "*");

        var (redactResponse, redacted) = await CallAsync(redactKey, $"{server.Slug}__contact");
        redacted!["result"]!["content"]![0]!["text"]!.GetValue<string>().ShouldBe("Ring [TELEFON]");
        redacted["result"]!["structuredContent"]!["phone"]!.GetValue<string>().ShouldBe("[TELEFON]");
        (await fixture.UsageAsync(redactResponse)).PiiCategories.ShouldBe("Phone:2");

        var (blockResponse, blocked) = await CallAsync(blockKey, $"{server.Slug}__contact");
        blocked!["error"]!["code"]!.GetValue<int>().ShouldBe(-32002);
        blocked.ToJsonString().ShouldNotContain("070");
        (await fixture.UsageAsync(blockResponse)).Outcome.ShouldBe(RequestOutcome.PiiBlocked);
        _upstream.For(server.Slug).Count(r => r.Tool == "contact").ShouldBe(2);
    }

    [Fact]
    public async Task The_mcp_call_rate_limit_answers_with_retry_information()
    {
        var (key, server) = await GrantedServerAsync(k => k.McpCallsPerMinute = 1);
        (await CallAsync(key, $"{server.Slug}__flaky_read")).Body!["result"].ShouldNotBeNull();

        var (response, body) = await CallAsync(key, $"{server.Slug}__flaky_read");
        body!["error"]!["code"]!.GetValue<int>().ShouldBe(-32000);
        body["error"]!["data"]!["retryAfterSeconds"]!.GetValue<int>().ShouldBeGreaterThan(0);
        response.Headers.RetryAfter.ShouldNotBeNull();
        (await fixture.UsageAsync(response)).Outcome.ShouldBe(RequestOutcome.RateLimited);
    }

    [Fact]
    public async Task A_read_only_tool_is_retried_after_a_server_error_and_a_destructive_one_is_not()
    {
        var (key, server) = await GrantedServerAsync();
        _upstream.FailOnce(server.Slug, "flaky_read");
        _upstream.FailOnce(server.Slug, "flaky_delete");

        (await CallAsync(key, $"{server.Slug}__flaky_read")).Body!["result"]!["content"]![0]!["text"]!.GetValue<string>().ShouldBe("ok");
        var (response, body) = await CallAsync(key, $"{server.Slug}__flaky_delete");

        body!["error"]!["code"]!.GetValue<int>().ShouldBe(-32003);
        _upstream.For(server.Slug).Count(r => r.Tool == "flaky_read").ShouldBe(2);
        _upstream.For(server.Slug).Count(r => r.Tool == "flaky_delete").ShouldBe(1);
        var usage = await fixture.UsageAsync(response);
        usage.Outcome.ShouldBe(RequestOutcome.ProviderError);
        usage.ErrorCode.ShouldBe("mcp_upstream_failed");
    }

    [Fact]
    public async Task A_wrong_server_address_is_a_plain_error_without_retries()
    {
        var (key, server) = await GrantedServerAsync(server: s => s.Url = s.Url.Replace("/mcp", "/elsewhere/mcp", StringComparison.Ordinal));
        var (response, body) = await CallAsync(key, $"{server.Slug}__flaky_read");

        body!["error"]!["code"]!.GetValue<int>().ShouldBe(-32003);
        // The SDK's version probe (server/discover, then initialize) got 404 once each; the gateway did not try again.
        _upstream.Requests.Count.ShouldBeLessThanOrEqualTo(2);
        (await fixture.UsageAsync(response)).ErrorCode.ShouldBe("mcp_upstream_failed");
    }

    [Fact]
    public async Task Transport_failures_open_the_servers_circuit_and_hide_its_tools()
    {
        var (key, server) = await GrantedServerAsync(server: s => s.Url = "http://127.0.0.1:1/mcp");
        (await ListAsync(key)).ShouldContain($"{server.Slug}__flaky_read");

        for (var i = 0; i < 5; i++)
        {
            (await CallAsync(key, $"{server.Slug}__flaky_read")).Body!["error"]!["code"]!.GetValue<int>().ShouldBe(-32003);
        }

        (await ListAsync(key)).ShouldNotContain(t => t.StartsWith(server.Slug, StringComparison.Ordinal));
        (await CallAsync(key, $"{server.Slug}__flaky_read")).Body!["error"]!["code"]!.GetValue<int>().ShouldBe(-32602);
        fixture.Logs.Text.ShouldContain("circuit_open");
    }

    [Fact]
    public async Task A_servers_error_message_is_cut_and_goes_through_the_pii_policy()
    {
        var (redactKey, server) = await GrantedServerAsync(k => k.PiiPolicy = PiiPolicy.Redact);
        var blockKey = await fixture.CreateKeyAsync(k => k.PiiPolicy = PiiPolicy.Block);
        var offKey = await fixture.CreateKeyAsync();
        await GrantAsync(blockKey, server, "*");
        await GrantAsync(offKey, server, "*");

        var (redactResponse, redacted) = await CallAsync(redactKey, $"{server.Slug}__fail_pii");
        var message = redacted!["error"]!["message"]!.GetValue<string>();
        message.ShouldStartWith("Inget ärende för [E-POST].");
        message.Length.ShouldBeLessThanOrEqualTo(500);
        redacted["error"]!["code"]!.GetValue<int>().ShouldBe(-32602);
        var usage = await fixture.UsageAsync(redactResponse);
        usage.PiiCategories.ShouldBe("Email:1");
        usage.ErrorCode.ShouldBe("provider_rejected");

        var (_, blocked) = await CallAsync(blockKey, $"{server.Slug}__fail_pii");
        blocked!["error"]!["message"]!.GetValue<string>().ShouldNotContain("@");

        var (_, plain) = await CallAsync(offKey, $"{server.Slug}__fail_pii");
        plain!["error"]!["message"]!.GetValue<string>().Length.ShouldBe(500);
    }

    [Fact]
    public async Task Each_upstream_call_is_a_client_span_on_the_gateways_activity_source()
    {
        var (key, server) = await GrantedServerAsync();
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Microsoft.Extensions.Hosting.Extensions.TelemetryName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        await CallAsync(key, $"{server.Slug}__lookup_case", new JsonObject { ["number"] = "KS-8" });

        var span = spans.Single(a => (string?)a.GetTagItem("ume.mcp.server") == server.Slug);
        span.DisplayName.ShouldBe("tools/call lookup.case");
        span.Kind.ShouldBe(ActivityKind.Client);
        span.GetTagItem("gen_ai.tool.name").ShouldBe("lookup.case");
        span.GetTagItem("mcp.method.name").ShouldBe("tools/call");
    }

    [Fact]
    public async Task A_result_over_the_server_limit_becomes_an_error()
    {
        var (key, server) = await GrantedServerAsync(server: s => s.MaxResultBytes = 10_000);
        var (_, body) = await CallAsync(key, $"{server.Slug}__big");

        body!["error"]!["code"]!.GetValue<int>().ShouldBe(-32004);
    }

    [Fact]
    public async Task Static_headers_reach_the_server_and_the_callers_key_and_headers_stay_with_the_call()
    {
        var protector = fixture.Services.GetRequiredService<CredentialProtector>();
        var (key, server) = await GrantedServerAsync(server: s =>
        {
            s.AuthType = McpAuthType.StaticHeaders;
            s.EncryptedCredential = protector.Protect("""{"X-Upstream-Key":"upstream-secret"}""");
            s.ForwardHeaders = ["X-Caller-Tag", "Authorization", "x-api-key"];
        });

        await CallAsync(key, $"{server.Slug}__flaky_read", headers: new Dictionary<string, string> { ["X-Caller-Tag"] = "first", ["x-api-key"] = key.Secret });
        await CallAsync(key, $"{server.Slug}__flaky_read", headers: new Dictionary<string, string> { ["X-Caller-Tag"] = "second" });

        var requests = _upstream.For(server.Slug);
        requests.ShouldAllBe(r => r.Headers["X-Upstream-Key"] == "upstream-secret");
        requests.ShouldAllBe(r => !r.Headers.Values.Any(v => v.Contains(key.Secret, StringComparison.Ordinal)));
        requests.ShouldAllBe(r => !r.Headers.ContainsKey("Authorization"));
        requests.Where(r => r.Method == "tools/call").Select(r => r.Headers["X-Caller-Tag"]).ShouldBe(["first", "second"]);
        var own = requests.Where(r => r.Method != "tools/call").ToList();
        own.ShouldNotBeEmpty(); // the SDK client's own discover/initialize
        own.ShouldAllBe(r => !r.Headers.ContainsKey("X-Caller-Tag"));
    }

    [Fact]
    public async Task Concurrent_callers_each_forward_only_their_own_headers()
    {
        var (key, server) = await GrantedServerAsync(server: s => s.ForwardHeaders = ["X-Caller-Tag"]);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            CallAsync(key, $"{server.Slug}__echo_args", new JsonObject { ["caller"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                headers: new Dictionary<string, string> { ["X-Caller-Tag"] = "tag-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) })));

        var calls = _upstream.For(server.Slug).Where(r => r.Method == "tools/call").ToList();
        calls.Count.ShouldBe(12);
        foreach (var call in calls)
        {
            var caller = JsonNode.Parse(call.Body)!["params"]!["arguments"]!["caller"]!.GetValue<string>();
            call.Headers["X-Caller-Tag"].ShouldBe("tag-" + caller);
        }

        _upstream.For(server.Slug).Where(r => r.Method != "tools/call").ShouldAllBe(r => !r.Headers.ContainsKey("X-Caller-Tag"));
    }

    [Fact]
    public async Task The_slug_endpoint_uses_the_servers_own_tool_names()
    {
        var (key, server) = await GrantedServerAsync();
        var path = "/mcp/" + server.Slug;

        (await ListAsync(key, path)).ShouldBe(["big", "contact", "echo_args", "fail_pii", "flaky_delete", "flaky_read", "lookup.case"]);
        (await CallAsync(key, "lookup.case", new JsonObject { ["number"] = "KS-9" }, path)).Body!["result"]!["structuredContent"]!["number"]!.GetValue<string>().ShouldBe("KS-9");
        (await CallAsync(key, $"{server.Slug}__lookup_case", new JsonObject { ["number"] = "KS-9" }, path)).Body!["error"]!["code"]!.GetValue<int>().ShouldBe(-32602);

        var other = await fixture.CreateKeyAsync();
        (await RpcAsync(other, "tools/list", path: path)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await RpcAsync(key, "tools/list", path: "/mcp/nosuchserver")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2025-11-25")]
    [InlineData("2025-06-18")]
    public async Task Sdk_clients_of_every_supported_revision_list_and_call_tools(string? protocolVersion)
    {
        var (key, server) = await GrantedServerAsync(server: s => s.Instructions = "Use lookup.case for case numbers.");
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.Client.BaseAddress!, "/mcp/" + server.Slug),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + key.Secret },
            },
            fixture.Client, NullLoggerFactory.Instance, ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport, new McpClientOptions { ProtocolVersion = protocolVersion }, NullLoggerFactory.Instance, Ct);

        client.NegotiatedProtocolVersion.ShouldBe(protocolVersion ?? "2026-07-28");
        client.ServerInfo.Name.ShouldBe("ume-llm-gateway");
        client.ServerInstructions.ShouldBe("Use lookup.case for case numbers.");
        (await client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).ShouldContain("lookup.case");
        var result = await client.CallToolAsync("lookup.case", new Dictionary<string, object?> { ["number"] = "KS-7" }, cancellationToken: Ct);
        result.StructuredContent!.Value.GetProperty("number").GetString().ShouldBe("KS-7");
        if (protocolVersion is not null)
        {
            (await client.PingAsync(cancellationToken: Ct)).ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task Any_gateway_instance_serves_any_request_of_a_handshake_client()
    {
        var (key, server) = await GrantedServerAsync();
        await using var second = fixture.Derive(_ => { });
        using var other = second.CreateHttpsClient();
        var handshake = new Dictionary<string, string> { ["MCP-Protocol-Version"] = "2025-11-25" };

        var (initialized, init) = await RpcAsync(key, "initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-11-25", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "t", ["version"] = "1" },
        }, stateless: false);
        init!["result"]!["protocolVersion"]!.GetValue<string>().ShouldBe("2025-11-25");
        initialized.Headers.Contains("Mcp-Session-Id").ShouldBeFalse();

        var (_, list) = await RpcAsync(key, "tools/list", headers: handshake, stateless: false, client: other);
        list!["result"]!["tools"]!.AsArray().ShouldContain(t => t!["name"]!.GetValue<string>() == $"{server.Slug}__lookup_case");
        list["result"]!.AsObject().ContainsKey("ttlMs").ShouldBeFalse();
        var (_, call) = await RpcAsync(key, "tools/call", new JsonObject { ["name"] = $"{server.Slug}__lookup_case", ["arguments"] = new JsonObject { ["number"] = "KS-5" } },
            headers: handshake, stateless: false, client: other);
        call!["result"]!["structuredContent"]!["number"]!.GetValue<string>().ShouldBe("KS-5");
    }

    [Fact]
    public async Task Unsupported_versions_and_unknown_methods_are_json_rpc_errors()
    {
        var (key, _) = await GrantedServerAsync();
        var (response, body) = await RpcAsync(key, "tools/list", headers: new Dictionary<string, string> { ["MCP-Protocol-Version"] = "2099-01-01" }, stateless: false);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        body!["error"]!["code"]!.GetValue<int>().ShouldBe(-32022);

        (await RpcAsync(key, "completion/complete")).Body!["error"]!["code"]!.GetValue<int>().ShouldBe(-32601);
        (await RpcAsync(key, "subscriptions/listen", stateless: false, headers: new Dictionary<string, string> { ["MCP-Protocol-Version"] = "2025-11-25" }))
            .Body!["error"]!["code"]!.GetValue<int>().ShouldBe(-32601);
        (await RpcAsync(key, "notifications/initialized", notification: true)).Response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using var get = await fixture.Client.GetAsync("/mcp", Ct);
        get.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
    }

    private async Task<(TestKey Key, McpServer Server)> DocumentServerAsync(Action<VirtualKey>? key = null, bool resources = true, bool prompts = true)
    {
        var testKey = await fixture.CreateKeyAsync(key);
        var server = await AddServerAsync(null, ("lookup.case", McpToolApprovalState.Approved, LookupDefinition),
            ("progress", McpToolApprovalState.Approved, ReadDefinition), ("slow", McpToolApprovalState.Approved, ReadDefinition),
            ("link", McpToolApprovalState.Approved, ReadDefinition));
        await GrantAsync(testKey, server, ["*"], resources, prompts);
        return (testKey, server);
    }

    private static string[] Names(JsonObject body, string field, string name) =>
        [.. body["result"]![field]!.AsArray().Select(r => r![name]!.GetValue<string>())];

    [Fact]
    public async Task Resources_are_listed_page_by_page_with_namespaced_uris_and_read_through_the_gateway()
    {
        var (key, server) = await DocumentServerAsync(k => k.PiiPolicy = PiiPolicy.Redact);
        var toolsOnly = await AddServerAsync(null, ("lookup.case", McpToolApprovalState.Approved, LookupDefinition));
        await GrantAsync(key, toolsOnly, "*");
        var prefix = $"mcp+ume://{server.Slug}/";

        var (_, first) = await RpcAsync(key, "resources/list");
        Names(first!, "resources", "uri").ShouldBe([prefix + "file:///notes/a.txt", prefix + "file:///notes/b.bin"]);
        var cursor = first!["result"]!["nextCursor"]!.GetValue<string>();
        var (_, second) = await RpcAsync(key, "resources/list", new JsonObject { ["cursor"] = cursor });
        Names(second!, "resources", "uri").ShouldBe([prefix + "file:///notes/c.txt"]);
        second!["result"]!.AsObject().ContainsKey("nextCursor").ShouldBeFalse();
        _upstream.For(toolsOnly.Slug).ShouldBeEmpty(); // no resource grant, so not even asked

        var (_, templates) = await RpcAsync(key, "resources/templates/list");
        Names(templates!, "resourceTemplates", "uriTemplate").ShouldBe([prefix + "file:///cases/{number}"]);

        var (response, read) = await RpcAsync(key, "resources/read", new JsonObject { ["uri"] = prefix + "file:///notes/a.txt" });
        var contents = read!["result"]!["contents"]![0]!;
        contents["uri"]!.GetValue<string>().ShouldBe(prefix + "file:///notes/a.txt");
        contents["text"]!.GetValue<string>().ShouldBe("Kontakt: [E-POST]");
        var usage = await fixture.UsageAsync(response);
        usage.McpMethod.ShouldBe("resources/read");
        usage.McpServerId.ShouldBe(server.Id);
        usage.McpTool.ShouldBeNull();
        usage.RequestedModel.ShouldBe($"mcp:{server.Slug}");
        usage.PiiCategories.ShouldBe("Email:1");
        _upstream.For(server.Slug).Single(r => r.Method == "resources/read").Body.ShouldContain("\"uri\":\"file:///notes/a.txt\"");

        var (_, blob) = await RpcAsync(key, "resources/read", new JsonObject { ["uri"] = prefix + "file:///notes/b.bin" });
        blob!["result"]!["contents"]![0]!["blob"]!.GetValue<string>().ShouldBe(McpUpstreamServer.PixelPng);
    }

    [Fact]
    public async Task Resource_links_in_aggregated_tool_results_can_be_read_through_the_gateway()
    {
        var (key, server) = await DocumentServerAsync();
        var prefix = $"mcp+ume://{server.Slug}/";

        var (_, called) = await CallAsync(key, $"{server.Slug}__link");
        var content = called!["result"]!["content"]!.AsArray();
        content[0]!["uri"]!.GetValue<string>().ShouldBe(prefix + "file:///notes/a.txt");
        content[0]!["name"]!.GetValue<string>().ShouldBe("a");
        content[1]!["resource"]!["uri"]!.GetValue<string>().ShouldBe(prefix + "file:///notes/c.txt");
        called["result"]!["structuredContent"]!["uri"]!.GetValue<string>().ShouldBe("file:///notes/a.txt"); // the tool's own data

        var (_, read) = await RpcAsync(key, "resources/read", new JsonObject { ["uri"] = content[0]!["uri"]!.GetValue<string>() });
        read!["result"]!["contents"]![0]!["text"]!.GetValue<string>().ShouldStartWith("Kontakt:");

        var (_, direct) = await CallAsync(key, "link", path: "/mcp/" + server.Slug);
        direct!["result"]!["content"]![0]!["uri"]!.GetValue<string>().ShouldBe("file:///notes/a.txt");
    }

    [Fact]
    public async Task A_failing_server_is_left_out_of_aggregated_lists_and_logged()
    {
        var (key, server) = await DocumentServerAsync();
        var broken = await AddServerAsync(s => s.Url = "http://127.0.0.1:1/mcp", ("x", McpToolApprovalState.Approved, EchoDefinition));
        await GrantAsync(key, broken, ["*"], resources: true, prompts: false);

        var (_, first) = await RpcAsync(key, "resources/list");
        var uris = Names(first!, "resources", "uri").ToList();
        while (first!["result"]!["nextCursor"] is { } cursor)
        {
            (_, first) = await RpcAsync(key, "resources/list", new JsonObject { ["cursor"] = cursor.GetValue<string>() });
            uris.AddRange(Names(first!, "resources", "uri"));
        }

        uris.ShouldBe([.. new[] { "a.txt", "b.bin", "c.txt" }.Select(f => $"mcp+ume://{server.Slug}/file:///notes/{f}")]);
        fixture.Logs.Text.ShouldContain($"MCP server {broken.Slug} left out of resources/list (Transport");
    }

    [Fact]
    public async Task A_tool_grant_exposes_no_resources_or_prompts_and_reading_them_looks_unknown()
    {
        var (key, server) = await DocumentServerAsync(resources: false, prompts: false);

        Names((await RpcAsync(key, "resources/list")).Body!, "resources", "uri").ShouldBeEmpty();
        Names((await RpcAsync(key, "prompts/list")).Body!, "prompts", "name").ShouldBeEmpty();
        var (_, denied) = await RpcAsync(key, "resources/read", new JsonObject { ["uri"] = $"mcp+ume://{server.Slug}/file:///notes/a.txt" });
        var (_, unknown) = await RpcAsync(key, "resources/read", new JsonObject { ["uri"] = "mcp+ume://nosuch/file:///notes/a.txt" });
        denied!["error"]!.ToJsonString().ShouldBe(unknown!["error"]!.ToJsonString());
        var (_, prompt) = await RpcAsync(key, "prompts/get", new JsonObject { ["name"] = $"{server.Slug}__summarize_case" });
        prompt!["error"]!["code"]!.GetValue<int>().ShouldBe(-32602);
        _upstream.For(server.Slug).ShouldBeEmpty();

        var (_, init) = await RpcAsync(key, "initialize", new JsonObject { ["protocolVersion"] = "2025-11-25" }, stateless: false);
        var capabilities = init!["result"]!["capabilities"]!.AsObject();
        capabilities.ContainsKey("tools").ShouldBeTrue();
        capabilities.ContainsKey("resources").ShouldBeFalse();
        capabilities.ContainsKey("prompts").ShouldBeFalse();
    }

    [Fact]
    public async Task The_slug_endpoint_keeps_original_resource_uris_cursors_and_prompt_names()
    {
        var (key, server) = await DocumentServerAsync();
        var path = "/mcp/" + server.Slug;

        var (_, first) = await RpcAsync(key, "resources/list", path: path);
        Names(first!, "resources", "uri").ShouldBe(["file:///notes/a.txt", "file:///notes/b.bin"]);
        first!["result"]!["nextCursor"]!.GetValue<string>().ShouldBe("page2");
        Names((await RpcAsync(key, "resources/list", new JsonObject { ["cursor"] = "page2" }, path)).Body!, "resources", "uri").ShouldBe(["file:///notes/c.txt"]);
        (await RpcAsync(key, "resources/read", new JsonObject { ["uri"] = "file:///notes/a.txt" }, path)).Body!["result"]!["contents"]![0]!["uri"]!
            .GetValue<string>().ShouldBe("file:///notes/a.txt");
        Names((await RpcAsync(key, "prompts/list", path: path)).Body!, "prompts", "name").ShouldBe(["summarize.case"]);

        var (_, init) = await RpcAsync(key, "initialize", new JsonObject { ["protocolVersion"] = "2025-11-25" }, path, stateless: false);
        init!["result"]!["capabilities"]!.AsObject().ContainsKey("resources").ShouldBeTrue();
    }

    [Fact]
    public async Task Prompts_are_named_like_tools_and_got_through_the_gateway_with_the_pii_policy()
    {
        var (key, server) = await DocumentServerAsync(k => k.PiiPolicy = PiiPolicy.Redact);
        var exposed = $"{server.Slug}__summarize_case";

        // Not listed first: the gateway finds the server's name for it by itself.
        var (response, got) = await RpcAsync(key, "prompts/get", new JsonObject { ["name"] = exposed, ["arguments"] = new JsonObject { ["number"] = "KS-1" } });
        got!["result"]!["messages"]![0]!["content"]!["text"]!.GetValue<string>().ShouldBe("Sammanfatta ärende KS-1. Handläggare: [E-POST]");
        var usage = await fixture.UsageAsync(response);
        usage.McpMethod.ShouldBe("prompts/get");
        usage.McpTool.ShouldBe("summarize.case");
        usage.RequestedModel.ShouldBe($"mcp:{server.Slug}/summarize.case");

        Names((await RpcAsync(key, "prompts/list")).Body!, "prompts", "name").ShouldBe([exposed]);

        var blockKey = await fixture.CreateKeyAsync(k => k.PiiPolicy = PiiPolicy.Block);
        await GrantAsync(blockKey, server, ["*"], resources: false, prompts: true);
        var (_, blocked) = await RpcAsync(blockKey, "prompts/get", new JsonObject { ["name"] = exposed, ["arguments"] = new JsonObject { ["number"] = "anna@example.se" } });
        blocked!["error"]!["code"]!.GetValue<int>().ShouldBe(-32002);
        _upstream.For(server.Slug).Count(r => r.Method == "prompts/get").ShouldBe(1);
    }

    [Fact]
    public async Task Progress_is_relayed_on_an_event_stream_with_the_callers_token()
    {
        var (key, server) = await DocumentServerAsync();
        var parameters = new JsonObject
        {
            ["name"] = $"{server.Slug}__progress", ["arguments"] = new JsonObject(),
        };
        var meta = new JsonObject { ["progressToken"] = "caller-token" };
        parameters["_meta"] = meta;
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 7, ["method"] = "tools/call", ["params"] = parameters };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        using var response = await fixture.Client.SendAsync(request, Ct);

        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        var events = (await response.Content.ReadAsStringAsync(Ct)).Split('\n').Where(l => l.StartsWith("data: ", StringComparison.Ordinal))
            .Select(l => JsonNode.Parse(l[6..])!).ToList();
        // Two progress notifications upstream; the relay may drop one that arrives out of order, never repeat or reorder them.
        var progress = events.SkipLast(1).ToList();
        progress.Count.ShouldBeInRange(1, 2);
        progress.ShouldAllBe(e => e["method"]!.GetValue<string>() == "notifications/progress" && e["params"]!["progressToken"]!.GetValue<string>() == "caller-token");
        progress.Select(e => e["params"]!["progress"]!.GetValue<double>()).ShouldBeInOrder(SortDirection.Ascending);
        events[^1]["id"]!.GetValue<int>().ShouldBe(7);
        events[^1]["result"]!["content"]![0]!["text"]!.GetValue<string>().ShouldBe("done");
        _upstream.For(server.Slug).Single(r => r.Tool == "progress").Body.ShouldNotContain("caller-token");
    }

    [Fact]
    public async Task A_client_that_leaves_cancels_the_upstream_call_and_is_accounted_as_cancelled()
    {
        var (key, server) = await DocumentServerAsync();
        using var leave = new CancellationTokenSource();
        var call = CallAsyncWith(key, $"{server.Slug}__slow", leave.Token);
        while (!_upstream.For(server.Slug).Any(r => r.Tool == "slow"))
        {
            await Task.Delay(20, Ct);
        }

        await leave.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(call);
        await _upstream.SlowCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await fixture.Services.GetRequiredService<Pipeline.UsageWriter>().FlushAsync(Ct);
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        UsageRecord? usage = null;
        for (var i = 0; i < 100 && usage is null; i++)
        {
            usage = await db.UsageRecords.AsNoTracking().SingleOrDefaultAsync(u => u.VirtualKeyId == key.Id, Ct);
            await Task.Delay(50, Ct);
        }

        usage!.Outcome.ShouldBe(RequestOutcome.ClientCancelled);
        usage.StatusCode.ShouldBe(499);
    }

    private async Task CallAsyncWith(TestKey key, string tool, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = new JsonObject() },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        using var response = await fixture.Client.SendAsync(request, cancellationToken);
        await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private sealed class ListenStream(HttpResponseMessage response, StreamReader reader) : IDisposable
    {
        public HttpResponseMessage Response => response;

        /// <summary>The next SSE message, or null when the stream ends.</summary>
        public async Task<JsonObject?> NextAsync(CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.StartsWith("data: ", StringComparison.Ordinal) || line.StartsWith('{'))
                {
                    return JsonNode.Parse(line.StartsWith('{') ? line : line[6..])!.AsObject();
                }
            }

            return null;
        }

        public void Dispose()
        {
            reader.Dispose();
            response.Dispose();
        }
    }

    private static async Task<ListenStream> ListenAsync(HttpClient client, TestKey key, string path = "/mcp")
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = "listen-1", ["method"] = "subscriptions/listen",
            ["params"] = new JsonObject
            {
                ["_meta"] = new JsonObject
                {
                    ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                    ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
                },
                ["notifications"] = new JsonObject { ["toolsListChanged"] = true, ["promptsListChanged"] = true, ["resourceSubscriptions"] = new JsonArray("x") },
            },
        };
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        return new ListenStream(response, new StreamReader(await response.Content.ReadAsStreamAsync(Ct)));
    }

    [Fact]
    public async Task A_listen_stream_announces_changes_the_key_can_see_and_ends_when_the_key_is_revoked()
    {
        var (key, server) = await DocumentServerAsync();
        using var stream = await ListenAsync(fixture.Client, key);
        stream.Response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");

        var ack = (await stream.NextAsync(Ct))!;
        ack["method"]!.GetValue<string>().ShouldBe("notifications/subscriptions/acknowledged");
        ack["params"]!["_meta"]!["io.modelcontextprotocol/subscriptionId"]!.GetValue<string>().ShouldBe("listen-1");
        ack["params"]!["notifications"]!.ToJsonString().ShouldBe("""{"toolsListChanged":true,"promptsListChanged":true}""");

        var bus = fixture.Services.GetRequiredService<IInvalidationBus>();
        var other = await AddServerAsync(null, ("x", McpToolApprovalState.Approved, EchoDefinition));
        await bus.PublishAsync(InvalidationKind.Config, Ct); // a server the key has no grant for: nothing to announce
        await GrantAsync(key, other, ["x"], resources: false, prompts: true);
        await bus.PublishAsync(InvalidationKind.Config, Ct);

        var changes = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            var next = (await stream.NextAsync(Ct))!;
            next["params"]!["_meta"]!["io.modelcontextprotocol/subscriptionId"]!.GetValue<string>().ShouldBe("listen-1");
            changes.Add(next["method"]!.GetValue<string>());
        }

        changes.ShouldBe(["notifications/tools/list_changed", "notifications/prompts/list_changed"], ignoreOrder: true);

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            (await db.VirtualKeys.SingleAsync(k => k.Id == key.Id, Ct)).RevokedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync(Ct);
        }

        await bus.PublishAsync(InvalidationKind.Keys, Ct);
        var end = (await stream.NextAsync(Ct))!;
        end["id"]!.GetValue<string>().ShouldBe("listen-1");
        end["result"]!["resultType"]!.GetValue<string>().ShouldBe("complete");
        (await stream.NextAsync(Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Listen_streams_are_capped_per_key()
    {
        var (key, _) = await DocumentServerAsync();
        await using var gateway = fixture.Derive(services => services.Configure<Pipeline.GatewayOptions>(o => o.Mcp.MaxListenStreamsPerKey = 1));
        using var client = gateway.CreateHttpsClient();
        using var first = await ListenAsync(client, key);
        (await first.NextAsync(Ct))!["method"]!.GetValue<string>().ShouldBe("notifications/subscriptions/acknowledged");

        using var second = await ListenAsync(client, key);
        var refused = (await second.NextAsync(Ct))!;
        refused["error"]!["code"]!.GetValue<int>().ShouldBe(-32000);
    }
}
