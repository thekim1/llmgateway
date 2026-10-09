using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Infrastructure.Persistence;

namespace Ume.LlmGateway.Gateway.Tests;

public sealed class GatewayTests(GatewayFixture fixture)
{
    [Theory]
    [InlineData("missing", 401, "invalid_api_key")]
    [InlineData("revoked", 401, "key_revoked")]
    [InlineData("expired", 401, "key_expired")]
    [InlineData("disabled", 403, "key_disabled")]
    [InlineData("past-grace", 401, "key_revoked")]
    public async Task Rejects_unusable_keys(string state, int status, string code)
    {
        var key = state == "missing" ? null : await fixture.CreateKeyAsync(k =>
        {
            k.RevokedAt = state == "revoked" ? DateTimeOffset.UtcNow.AddMinutes(-1) : null;
            k.ExpiresAt = state == "expired" ? DateTimeOffset.UtcNow.AddMinutes(-1) : null;
            k.IsEnabled = state != "disabled";
            k.GraceUntil = state == "past-grace" ? DateTimeOffset.UtcNow.AddMinutes(-1) : null;
        });
        using var response = await fixture.SendAsync(key);
        await AssertErrorAsync(response, status, code);
    }

    [Fact]
    public async Task Grace_key_still_works()
    {
        var key = await fixture.CreateKeyAsync(k => k.GraceUntil = DateTimeOffset.UtcNow.AddHours(24));
        using var response = await fixture.SendAsync(key);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Success_has_routing_cost_security_headers_and_persisted_metadata()
    {
        var key = await fixture.CreateKeyAsync(k => k.RequestsPerMinute = 10);
        await fixture.AddBudgetAsync(BudgetScope.VirtualKey, key.Id, 10);
        using var response = await fixture.SendAsync(key);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("x-ume-provider").Single().ShouldBe("eu");
        response.Headers.GetValues("x-ume-model").Single().ShouldBe("eu/ok");
        response.Headers.GetValues("x-ume-residency").Single().ShouldBe("Eu");
        response.Headers.GetValues("x-ume-fallbacks").Single().ShouldBe("0");
        decimal.Parse(response.Headers.GetValues("x-ume-cost-sek").Single(), CultureInfo.InvariantCulture).ShouldBe(0.00135m);
        response.Headers.GetValues("x-ume-budget-remaining-sek").Single().ShouldBe("10.00");
        response.Headers.GetValues("x-ratelimit-remaining-requests").Single().ShouldBe("9");
        response.Headers.GetValues("X-Content-Type-Options").Single().ShouldBe("nosniff");
        response.Headers.GetValues("X-Frame-Options").Single().ShouldBe("DENY");
        response.Headers.GetValues("Referrer-Policy").Single().ShouldBe("no-referrer");
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("frame-ancestors 'none'");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        response.Headers.Contains("Server").ShouldBeFalse();
        var usage = await fixture.UsageAsync(response);
        usage.VirtualKeyId.ShouldBe(key.Id);
        usage.TeamId.ShouldBe(key.TeamId);
        usage.InputTokens.ShouldBe(100);
        usage.CachedInputTokens.ShouldBe(10);
        usage.OutputTokens.ShouldBe(20);
        usage.CostSek.ShouldBe(0.00135m);
        usage.Outcome.ShouldBe(RequestOutcome.Success);
        using var scope = fixture.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().VirtualKeys.SingleAsync(k => k.Id == key.Id, TestContext.Current.CancellationToken)).LastUsedAt.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("eu/fail503")]
    [InlineData("eu/fail429")]
    [InlineData("slow/slow")]
    public async Task Retryable_failure_falls_back(string failingModel)
    {
        var route = await fixture.CreateRouteAsync(failingModel, "onprem/ok");
        var key = await fixture.CreateKeyAsync();
        using var response = await fixture.SendAsync(key, route);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("x-ume-provider").Single().ShouldBe("onprem");
        response.Headers.GetValues("x-ume-fallbacks").Single().ShouldBe("1");
        (await fixture.UsageAsync(response)).FallbackCount.ShouldBe(1);
    }

    [Fact]
    public async Task Client_error_does_not_fall_back()
    {
        var route = await fixture.CreateRouteAsync("eu/bad400", "onprem/ok");
        var key = await fixture.CreateKeyAsync();
        using var response = await fixture.SendAsync(key, route);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Headers.GetValues("x-ume-provider").Single().ShouldBe("eu");
        response.Headers.GetValues("x-ume-fallbacks").Single().ShouldBe("0");
        (await fixture.UsageAsync(response)).Outcome.ShouldBe(RequestOutcome.ProviderError);
    }

    [Fact]
    public async Task All_fail_returns_gateway_error()
    {
        var route = await fixture.CreateRouteAsync("eu/fail503", "external/fail429");
        using var response = await fixture.SendAsync(await fixture.CreateKeyAsync(), route);
        await AssertErrorAsync(response, 502, "all_providers_failed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Streaming_accounts_usage_and_hides_unrequested_usage(bool includeUsage)
    {
        using var response = await fixture.SendAsync(await fixture.CreateKeyAsync(), stream: true, includeUsage: includeUsage);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        text.ShouldContain("Test answer");
        text.ShouldContain("[DONE]");
        text.Contains("prompt_tokens", StringComparison.Ordinal).ShouldBe(includeUsage);
        var usage = await fixture.UsageAsync(response);
        usage.Streamed.ShouldBeTrue();
        usage.InputTokens.ShouldBe(100);
        usage.OutputTokens.ShouldBe(20);
        usage.CostSek.ShouldBe(0.00135m);
    }

    [Theory]
    [InlineData(BudgetScope.VirtualKey)]
    [InlineData(BudgetScope.Team)]
    public async Task Hierarchical_budget_blocks(BudgetScope budgetScope)
    {
        var key = await fixture.CreateKeyAsync();
        await fixture.AddBudgetAsync(budgetScope, budgetScope == BudgetScope.Team ? key.TeamId : key.Id, 0);
        using var response = await fixture.SendAsync(key);
        await AssertErrorAsync(response, 402, "budget_exceeded");
        (await fixture.UsageAsync(response)).Outcome.ShouldBe(RequestOutcome.BudgetExceeded);
    }

    [Fact]
    public async Task Reservation_cannot_exceed_remaining_budget()
    {
        var key = await fixture.CreateKeyAsync();
        await fixture.AddBudgetAsync(BudgetScope.VirtualKey, key.Id, 0.01m);
        using var response = await fixture.SendRawAsync(key, "/v1/chat/completions",
            """{"model":"eu/ok","messages":[{"role":"user","content":"Test"}],"max_tokens":100000}""");
        await AssertErrorAsync(response, 402, "budget_exceeded");
    }

    [Fact]
    public async Task Rotated_key_inherits_spend_and_budget_of_its_predecessor()
    {
        var old = await fixture.CreateKeyAsync();
        await fixture.AddBudgetAsync(BudgetScope.VirtualKey, old.Id, 0);
        var replacement = await fixture.CreateKeyAsync();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var previous = await db.VirtualKeys.SingleAsync(k => k.Id == old.Id, TestContext.Current.CancellationToken);
            previous.RotatedToKeyId = replacement.Id;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var response = await fixture.SendAsync(replacement);
        await AssertErrorAsync(response, 402, "budget_exceeded");
    }

    [Fact]
    public async Task Request_rate_limit_blocks_second_request()
    {
        var key = await fixture.CreateKeyAsync(k => k.RequestsPerMinute = 1);
        using var first = await fixture.SendAsync(key);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var response = await fixture.SendAsync(key);
        await AssertErrorAsync(response, 429, "rate_limited");
        response.Headers.RetryAfter.ShouldNotBeNull();
        response.Headers.GetValues("x-ratelimit-remaining-requests").Single().ShouldBe("0");
    }

    [Theory]
    [InlineData(PiiPolicy.Block, 400, "blocked", "eu")]
    [InlineData(PiiPolicy.Redact, 200, "redacted", "eu")]
    [InlineData(PiiPolicy.RerouteToOnPrem, 200, "rerouted-onprem", "onprem")]
    public async Task Pii_policy_is_enforced(PiiPolicy policy, int status, string header, string provider)
    {
        var route = await fixture.CreateRouteAsync("eu/ok", "onprem/ok");
        var key = await fixture.CreateKeyAsync(k => k.PiiPolicy = policy);
        using var response = await fixture.SendAsync(key, route, prompt: "Personnummer 19121212-1212");
        ((int)response.StatusCode).ShouldBe(status);
        response.Headers.GetValues("x-ume-pii").Single().ShouldBe(header);
        var usage = await fixture.UsageAsync(response);
        usage.PiiActionApplied.ShouldBe(policy);
        usage.PiiCategories.ShouldNotBeNull().ShouldContain("Personnummer");
        if (status == 200)
        {
            response.Headers.GetValues("x-ume-provider").Single().ShouldBe(provider);
        }
        if (policy == PiiPolicy.Redact)
        {
            var requests = fixture.Upstream.LogEntries.Where(e => e.RequestMessage?.Body?.Contains("[PERSONNUMMER]", StringComparison.Ordinal) == true).ToArray();
            requests.ShouldNotBeEmpty();
            requests.ShouldAllBe(e => !e.RequestMessage!.Body!.Contains("19121212-1212", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Pii_cannot_use_external_only_route()
    {
        var route = await fixture.CreateRouteAsync("external/ok");
        using var response = await fixture.SendAsync(await fixture.CreateKeyAsync(k => k.PiiPolicy = PiiPolicy.RerouteToOnPrem), route, prompt: "19121212-1212");
        await AssertErrorAsync(response, 503, "no_eligible_provider");
    }

    [Theory]
    [InlineData("""{"model":"missing"}""", "application/json", 404, "model_not_found")]
    [InlineData("""{"model":"eu/ok"}""", "text/plain", 415, "unsupported_media_type")]
    [InlineData("{", "application/json", 400, "invalid_request")]
    [InlineData("[]", "application/json", 400, "invalid_request")]
    [InlineData("""{"model":123}""", "application/json", 400, "invalid_request")]
    public async Task Invalid_request_is_rejected(string body, string contentType, int status, string code)
    {
        using var response = await fixture.SendRawAsync(await fixture.CreateKeyAsync(), "/v1/chat/completions", body, contentType);
        await AssertErrorAsync(response, status, code);
    }

    [Fact]
    public async Task Oversized_body_is_rejected_before_upstream_call()
    {
        using var response = await fixture.SendAsync(await fixture.CreateKeyAsync(), prompt: new string('x', 5000));
        await AssertErrorAsync(response, 413, "request_too_large");
    }

    [Theory]
    [InlineData("/v1/embeddings", "eu/embedding")]
    [InlineData("/v1/responses", "eu/ok")]
    public async Task Additional_endpoints_share_auth_and_accounting(string endpoint, string model)
    {
        using var response = await fixture.SendAsync(await fixture.CreateKeyAsync(), model, endpoint);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fixture.UsageAsync(response)).CostSek.ShouldBe(0.00135m);
    }

    [Fact]
    public async Task Model_allowlist_and_endpoint_kind_are_enforced()
    {
        using var forbidden = await fixture.SendAsync(await fixture.CreateKeyAsync(k => k.AllowedModels = ["onprem/ok"]));
        await AssertErrorAsync(forbidden, 403, "model_not_allowed");
        var alias = await fixture.CreateRouteAsync("eu/embedding");
        using var mismatch = await fixture.SendAsync(await fixture.CreateKeyAsync(), alias);
        await AssertErrorAsync(mismatch, 400, "invalid_request");
    }

    [Fact]
    public async Task Provider_allowlist_rejects_routes_without_an_allowed_provider()
    {
        var key = await fixture.CreateKeyAsync(k => k.AllowedProviders = ["eu"]);
        using var forbidden = await fixture.SendAsync(key, "onprem/ok");
        await AssertErrorAsync(forbidden, 403, "model_not_allowed");
        using var allowed = await fixture.SendAsync(key, "eu/ok");
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fixture.UsageAsync(allowed)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Provider_allowlist_skips_disallowed_providers_in_fallback_chain()
    {
        // The route prefers a failing "onprem" target and falls back to "eu"; the key may only use "external".
        var alias = await fixture.CreateRouteAsync("onprem/ok", "eu/ok", "external/ok");
        var key = await fixture.CreateKeyAsync(k => k.AllowedProviders = ["external"]);
        using var response = await fixture.SendAsync(key, alias);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fixture.UsageAsync(response)).ProviderName.ShouldBe("external");
    }

    [Fact]
    public async Task Provider_allowlist_is_case_insensitive_and_empty_means_all()
    {
        using var upper = await fixture.SendAsync(await fixture.CreateKeyAsync(k => k.AllowedProviders = ["EU"]), "eu/ok");
        upper.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var all = await fixture.SendAsync(await fixture.CreateKeyAsync(), "onprem/ok");
        all.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Models_list_filters_by_provider_allowlist()
    {
        var key = await fixture.CreateKeyAsync(k => k.AllowedProviders = ["onprem"]);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        using var response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var ids = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["data"]!.AsArray()
            .Select(n => n!["id"]!.GetValue<string>()).ToList();
        // Other tests add route aliases ("test/...") that may point at this provider, so only the models of the other
        // providers are checked, not the whole list.
        ids.ShouldContain("onprem/ok");
        string[] otherProviders = ["eu/", "external/", "anthropic/", "slow/"];
        ids.ShouldNotContain(id => otherProviders.Any(prefix => id.StartsWith(prefix, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Token_rate_limit_blocks_once_the_minute_budget_is_used()
    {
        // Each mocked call reports 120 tokens (100 in + 20 out); the limit of 100 is crossed by the first one.
        var key = await fixture.CreateKeyAsync(k => k.TokensPerMinute = 100);
        using var first = await fixture.SendAsync(key);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        await fixture.UsageAsync(first);
        using var second = await fixture.SendAsync(key);
        await AssertErrorAsync(second, 429, "rate_limited");
        (await fixture.UsageAsync(second)).Outcome.ShouldBe(RequestOutcome.RateLimited);
    }

    [Fact]
    public async Task Key_with_several_budget_periods_is_blocked_by_the_tightest_one()
    {
        var key = await fixture.CreateKeyAsync();
        await fixture.AddBudgetAsync(BudgetScope.VirtualKey, key.Id, 1000, BudgetPeriod.Monthly);
        await fixture.AddBudgetAsync(BudgetScope.VirtualKey, key.Id, 1000, BudgetPeriod.Daily);
        await fixture.AddBudgetAsync(BudgetScope.VirtualKey, key.Id, 0, BudgetPeriod.Hourly);
        using var response = await fixture.SendAsync(key);
        await AssertErrorAsync(response, 402, "budget_exceeded");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("timme");
    }

    [Fact]
    public async Task Key_with_generous_budgets_in_every_period_is_allowed_and_charged_to_all()
    {
        var key = await fixture.CreateKeyAsync();
        foreach (var period in new[] { BudgetPeriod.Hourly, BudgetPeriod.Daily, BudgetPeriod.Weekly, BudgetPeriod.Monthly })
        {
            await fixture.AddBudgetAsync(BudgetScope.VirtualKey, key.Id, 1000, period);
        }
        using var response = await fixture.SendAsync(key);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fixture.UsageAsync(response)).CostSek.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Anthropic_chat_is_translated_and_messages_pass_through()
    {
        var key = await fixture.CreateKeyAsync();
        using var chat = await fixture.SendAsync(key, "anthropic/ok");
        chat.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = JsonNode.Parse(await chat.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        json["object"]!.GetValue<string>().ShouldBe("chat.completion");
        json["choices"]![0]!["message"]!["content"]!.GetValue<string>().ShouldBe("Test answer");
        (await fixture.UsageAsync(chat)).InputTokens.ShouldBe(100);
        using var messages = await fixture.SendAsync(key, "anthropic/ok", "/v1/messages");
        messages.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode.Parse(await messages.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["type"]!.GetValue<string>().ShouldBe("message");
        using var error = await fixture.SendAsync(null, endpoint: "/v1/messages");
        var failure = JsonNode.Parse(await error.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        failure["type"]!.GetValue<string>().ShouldBe("error");
        failure["error"]!["type"]!.GetValue<string>().ShouldBe("authentication_error");
    }

    [Fact]
    public async Task Models_list_filters_allowlist_and_residency()
    {
        var key = await fixture.CreateKeyAsync(k => { k.AllowedModels = ["onprem/ok", "external/ok"]; k.AllowedResidencies = [DataResidency.OnPrem]; });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        using var response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var data = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["data"]!.AsArray();
        data.Count.ShouldBe(1);
        data[0]!["id"]!.GetValue<string>().ShouldBe("onprem/ok");
    }

    [Fact]
    public async Task Logs_and_database_never_contain_content_or_secrets()
    {
        var key = await fixture.CreateKeyAsync();
        var prompt = "Private-prompt-" + Guid.NewGuid();
        using var response = await fixture.SendAsync(key, prompt: prompt);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var usage = await fixture.UsageAsync(response);
        var persisted = JsonSerializer.Serialize(usage);
        foreach (var sensitive in new[] { prompt, "Test answer", key.Secret, fixture.ProviderSecret })
        {
            fixture.Logs.Text.ShouldNotContain(sensitive);
            persisted.ShouldNotContain(sensitive);
        }
        fixture.Logs.Text.ShouldContain("Request");
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, int status, string code)
    {
        ((int)response.StatusCode).ShouldBe(status);
        var error = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!;
        error["code"]!.GetValue<string>().ShouldBe(code);
        error["request_id"]!.GetValue<string>().ShouldBe(response.Headers.GetValues("x-request-id").Single());
    }
}
