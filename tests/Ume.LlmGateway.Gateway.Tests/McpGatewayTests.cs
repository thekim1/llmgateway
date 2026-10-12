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
using Ume.LlmGateway.TestKit;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>
/// The MCP gateway end to end: the gateway (TestServer) in front of an SDK-built MCP server on a loopback port. Every
/// test registers its own servers (unique slugs and upstream paths) and keys, so tests do not see each other's data.
/// </summary>
public sealed class McpGatewayTests(GatewayFixture fixture) : IAsyncLifetime
{
    private const string LookupDefinition = """{"description":"Finds a case.","inputSchema":{"type":"object","properties":{"number":{"type":"string"}},"required":["number"]},"annotations":{"readOnlyHint":true}}""";
    private const string EchoDefinition = """{"description":"Echoes its arguments.","inputSchema":{"type":"object"},"annotations":{"readOnlyHint":true}}""";
    private const string ReadDefinition = """{"description":"Reads.","inputSchema":{"type":"object"},"annotations":{"readOnlyHint":true}}""";
    private const string DeleteDefinition = """{"description":"Deletes.","inputSchema":{"type":"object"},"annotations":{"destructiveHint":true}}""";
    private const string BigDefinition = """{"description":"Big.","inputSchema":{"type":"object"}}""";

    private readonly McpUpstreamServer _upstream = new();
    private readonly List<Guid> _servers = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => new(_upstream.StartAsync());

    public async ValueTask DisposeAsync()
    {
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            await db.McpServers.Where(s => _servers.Contains(s.Id)).ExecuteDeleteAsync(CancellationToken.None);
        }

        await _upstream.DisposeAsync();
    }

    private async Task<McpServer> AddServerAsync(Action<McpServer>? configure = null, params (string Name, McpToolApprovalState State, string Definition)[] tools)
    {
        var slug = "t" + Guid.NewGuid().ToString("N")[..12];
        var server = new McpServer
        {
            Slug = slug, DisplayName = "Test " + slug, Url = _upstream.Endpoint(slug), Residency = DataResidency.OnPrem,
            Status = McpServerStatus.Healthy, CreatedAt = DateTimeOffset.UtcNow,
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

    private async Task GrantAsync(TestKey key, McpServer server, params string[] tools)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        db.VirtualKeyMcpGrants.Add(new VirtualKeyMcpGrant { VirtualKeyId = key.Id, McpServerId = server.Id, AllowedTools = [.. tools], CreatedAt = DateTimeOffset.UtcNow });
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

        (await RpcAsync(key, "resources/list")).Body!["error"]!["code"]!.GetValue<int>().ShouldBe(-32601);
        (await RpcAsync(key, "notifications/initialized", notification: true)).Response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using var get = await fixture.Client.GetAsync("/mcp", Ct);
        get.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
    }
}
