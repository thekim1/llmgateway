using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Persistence;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>The per-request residency ceiling (<c>x-ume-max-residency</c>) and the key flag that requires it.</summary>
public sealed class ResidencyCeilingTests(GatewayFixture fixture)
{
    private const string Header = "x-ume-max-residency";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, string> Ceiling(string value) => new() { [Header] = value };

    private static VirtualKey Key(bool requireMaxResidency) => new() { Name = "k", Prefix = "ume-sk-x", KeyHash = "h", RequireMaxResidency = requireMaxResidency };

    [Theory]
    [InlineData("OnPrem")]
    [InlineData("onprem")]
    [InlineData(" EU ")]
    [InlineData("eu")]
    public async Task The_ceiling_is_read_case_insensitively_and_keeps_the_request_within_it(string value)
    {
        // The EU target comes first: without a ceiling it answers.
        var route = await fixture.CreateRouteAsync("eu/ok", "onprem/ok");
        var key = await fixture.CreateKeyAsync();
        using var response = await fixture.SendAsync(key, route, headers: Ceiling(value));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var expected = value.Trim().Equals("onprem", StringComparison.OrdinalIgnoreCase) ? "OnPrem" : "Eu";
        response.Headers.GetValues("x-ume-residency").Single().ShouldBe(expected);
    }

    [Fact]
    public async Task No_fallback_or_retry_goes_above_the_ceiling()
    {
        // The EU target fails; the external fallback would answer, but the ceiling forbids it, retries included.
        var route = await fixture.CreateRouteAsync("eu/fail503", "external/ok");
        var key = await fixture.CreateKeyAsync();

        using (var open = await fixture.SendAsync(key, route))
        {
            open.StatusCode.ShouldBe(HttpStatusCode.OK);
            open.Headers.GetValues("x-ume-residency").Single().ShouldBe("External");
        }

        using var capped = await fixture.SendAsync(key, route, headers: Ceiling("Eu"));
        ((int)capped.StatusCode).ShouldBe(502);
        (await ErrorCodeAsync(capped)).ShouldBe("all_providers_failed");
        (await fixture.UsageAsync(capped)).Outcome.ShouldBe(RequestOutcome.ProviderError);
    }

    [Theory]
    [InlineData("Any")]
    [InlineData("0")]
    [InlineData("EU-West")]
    [InlineData("Eu, OnPrem")]
    public async Task A_ceiling_that_is_not_understood_is_refused(string value)
    {
        var key = await fixture.CreateKeyAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent("eu/ok"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        request.Headers.TryAddWithoutValidation(Header, value).ShouldBeTrue();
        using var response = await fixture.Client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(response)).ShouldBe("invalid_request");
        (await fixture.UsageAsync(response)).ErrorCode.ShouldBe("invalid_request");
    }

    [Theory]
    [InlineData(null, false, null, false)]
    [InlineData(null, true, null, true)]
    [InlineData("External", true, DataResidency.External, false)]
    [InlineData("", false, null, true)] // present but empty: not understood, never ignored
    [InlineData("  ", false, null, true)]
    [InlineData("1", false, null, true)]
    public void The_header_reader_refuses_what_it_does_not_understand(string? value, bool required, DataResidency? expected, bool refused)
    {
        var http = new DefaultHttpContext();
        if (value is not null)
        {
            http.Request.Headers[Header] = value;
        }

        var error = Requests.RequestStages.ReadMaxResidency(http.Request, Key(required), out var ceiling);
        (error is not null).ShouldBe(refused);
        error?.StatusCode.ShouldBe(400);
        ceiling.ShouldBe(expected);
    }

    [Fact]
    public void Reading_the_header_allocates_nothing()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers[Header] = "Eu";
        var key = Key(false);
        Requests.RequestStages.ReadMaxResidency(http.Request, key, out _); // warm up
        var before = GC.GetAllocatedBytesForCurrentThread();
        Requests.RequestStages.ReadMaxResidency(http.Request, key, out var ceiling);
        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0);
        ceiling.ShouldBe(DataResidency.Eu);
    }

    [Fact]
    public async Task A_repeated_header_is_refused()
    {
        var key = await fixture.CreateKeyAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = JsonContent("eu/ok") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        request.Headers.TryAddWithoutValidation(Header, ["Eu", "Eu"]).ShouldBeTrue();
        using var response = await fixture.Client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Anthropic_clients_get_the_refusal_in_their_shape()
    {
        var key = await fixture.CreateKeyAsync();
        using var response = await fixture.SendAsync(key, "anthropic/ok", "/v1/messages", headers: Ceiling("Anywhere"));
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        body["type"]!.GetValue<string>().ShouldBe("error");
        body["error"]!["code"]!.GetValue<string>().ShouldBe("invalid_request");
    }

    [Fact]
    public async Task No_target_within_the_ceiling_is_403_and_never_a_wider_one()
    {
        var key = await fixture.CreateKeyAsync();
        using var response = await fixture.SendAsync(key, "eu/ok", headers: Ceiling("OnPrem"));
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).ShouldBe("max_residency_not_met");
        response.Headers.RetryAfter.ShouldBeNull(); // it can never work: nothing to wait for
        (await fixture.UsageAsync(response)).ErrorCode.ShouldBe("max_residency_not_met");

        // The key's own residencies are intersected: an EU-only key has nothing on-prem.
        var euKey = await fixture.CreateKeyAsync(k => k.AllowedResidencies = [DataResidency.Eu]);
        using var euOnly = await fixture.SendAsync(euKey, await fixture.CreateRouteAsync("onprem/ok", "eu/ok"), headers: Ceiling("OnPrem"));
        euOnly.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Streaming: refused before any event is sent.
        using var stream = await fixture.SendAsync(key, "eu/ok", stream: true, headers: Ceiling("OnPrem"));
        stream.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        stream.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
    }

    [Fact]
    public async Task A_disabled_target_within_the_ceiling_is_503_with_retry_after()
    {
        var onPrem = await fixture.AddDeploymentAsync("onprem", "ok", contextWindow: null);
        var route = await fixture.CreateRouteAsync(onPrem, "eu/ok");
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            await db.ModelDeployments.Where(d => d.Name == onPrem).ExecuteUpdateAsync(s => s.SetProperty(d => d.IsEnabled, false), Ct);
        }

        var key = await fixture.CreateKeyAsync();
        using var response = await fixture.SendAsync(key, route, headers: Ceiling("OnPrem"));
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await ErrorCodeAsync(response)).ShouldBe("no_eligible_provider");
        response.Headers.RetryAfter!.Delta.ShouldNotBeNull();

        // A wider ceiling still reaches the EU target.
        using var wider = await fixture.SendAsync(key, route, headers: Ceiling("Eu"));
        wider.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Personal_data_still_goes_on_prem_within_a_wider_ceiling()
    {
        var key = await fixture.CreateKeyAsync(k => k.PiiPolicy = PiiPolicy.RerouteToOnPrem);
        var route = await fixture.CreateRouteAsync("eu/ok", "onprem/ok");
        using var response = await fixture.SendAsync(key, route, prompt: "Personnummer 19121212-1212", headers: Ceiling("Eu"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("x-ume-residency").Single().ShouldBe("OnPrem");

        // No on-prem target: the PII message wins (on-prem is within every ceiling).
        using var none = await fixture.SendAsync(key, "eu/ok", prompt: "Personnummer 19121212-1212", headers: Ceiling("Eu"));
        none.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        JsonNode.Parse(await none.Content.ReadAsStringAsync(Ct))!["error"]!["message"]!.GetValue<string>().ShouldContain("personal data");
    }

    [Fact]
    public async Task A_key_that_requires_the_header_refuses_requests_without_it()
    {
        var key = await fixture.CreateKeyAsync(k => k.RequireMaxResidency = true);

        using (var missing = await fixture.SendAsync(key))
        {
            missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            JsonNode.Parse(await missing.Content.ReadAsStringAsync(Ct))!["error"]!["message"]!.GetValue<string>().ShouldContain(Header);
        }

        using (var embeddings = await fixture.SendAsync(key, "eu/embedding", "/v1/embeddings"))
        {
            embeddings.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var models = await ListModelsAsync(key, null))
        {
            models.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        (await Should.ThrowAsync<Exception>(() => fixture.ConnectRealtimeAsync(key, "eu/ok"))).Message.ShouldContain("400");

        using var sent = await fixture.SendAsync(key, headers: Ceiling("External"));
        sent.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_model_list_shows_only_what_the_ceiling_allows()
    {
        var key = await fixture.CreateKeyAsync();
        var route = await fixture.CreateRouteAsync("eu/ok", "onprem/ok");

        using var response = await ListModelsAsync(key, "OnPrem");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var data = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["data"]!.AsArray();
        var ids = data.Select(n => n!["id"]!.GetValue<string>()).ToList();
        ids.ShouldContain("onprem/ok");
        ids.ShouldNotContain("eu/ok");
        ids.ShouldNotContain("external/ok");
        var alias = data.Single(n => n!["id"]!.GetValue<string>() == route)!;
        alias["ume"]!["residencies"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldBe(["OnPrem"]);

        using var bad = await ListModelsAsync(key, "Nowhere");
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_token_quota_of_a_residency_above_the_ceiling_is_not_reserved()
    {
        // A rate limit and no routing rule: the quota is reserved together with the rate limit, before routing.
        var key = await fixture.CreateKeyAsync(k => k.RequestsPerMinute = 100);
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            db.Budgets.Add(new Budget
            {
                Scope = BudgetScope.Team, ScopeId = key.TeamId, Unit = BudgetUnit.Tokens, LimitTokens = 1, Residency = DataResidency.External,
                Period = BudgetPeriod.Daily, CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        var route = await fixture.CreateRouteAsync("eu/ok", "external/ok");
        using (var open = await fixture.SendAsync(key, route))
        {
            ((int)open.StatusCode).ShouldBe(402); // an external target could be used: its quota is used up
        }

        using var capped = await fixture.SendAsync(key, route, headers: Ceiling("Eu"));
        capped.StatusCode.ShouldBe(HttpStatusCode.OK);
        capped.Headers.GetValues("x-ume-residency").Single().ShouldBe("Eu");
    }

    [Fact]
    public async Task Rerank_requests_stay_within_the_ceiling_too()
    {
        // A rerank route with the EU reranker first: the ceiling sends it on-prem, and refuses an EU-only reranker.
        var route = await fixture.CreateRouteAsync("eu/rerank", "onprem/rerank");
        var key = await fixture.CreateKeyAsync(k => k.RequireMaxResidency = true);
        var body = new JsonObject { ["query"] = "bygglov", ["documents"] = new JsonArray("a", "b") };

        body["model"] = route;
        using (var onPrem = await fixture.SendRawAsync(key, "/v1/rerank", body.ToJsonString(), headers: Ceiling("OnPrem")))
        {
            onPrem.StatusCode.ShouldBe(HttpStatusCode.OK);
            onPrem.Headers.GetValues("x-ume-residency").Single().ShouldBe("OnPrem");
        }

        body["model"] = "eu/rerank";
        using (var refused = await fixture.SendRawAsync(key, "/v1/rerank", body.ToJsonString(), headers: Ceiling("OnPrem")))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await ErrorCodeAsync(refused)).ShouldBe("max_residency_not_met");
        }

        using var missing = await fixture.SendRawAsync(key, "/v1/rerank", body.ToJsonString());
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest); // the key requires the header on every endpoint
    }

    private async Task<HttpResponseMessage> ListModelsAsync(TestKey key, string? ceiling)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        if (ceiling is not null)
        {
            request.Headers.Add(Header, ceiling);
        }

        return await fixture.Client.SendAsync(request, Ct);
    }

    private static StringContent JsonContent(string model) => new(
        new JsonObject { ["model"] = model, ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Hi" }) }.ToJsonString(),
        System.Text.Encoding.UTF8, "application/json");

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["error"]!["code"]!.GetValue<string>();
}
