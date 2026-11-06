using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Budgets;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Security;
using WireMock.Logging;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>
/// A reranker deployed on Azure AI Foundry, under the Foundry provider (as Claude on Foundry is), and a deployment's own
/// residency: the request address comes from the deployment's publisher or its override, the residency from the
/// deployment when it has one, in routing, the ceiling, the PII restriction and <c>/v1/models</c>.
/// </summary>
public sealed class FoundryRerankTests(GatewayFixture fixture)
{
    private const string Ceiling = "x-ume-max-residency";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Foundry(string Name, Guid Id, string Rerank, string Chat);

    /// <summary>
    /// A Foundry provider of its own at the mock's address (a project endpoint, as admins enter it) with a Cohere reranker
    /// and a chat model; <paramref name="configure"/> changes the reranker.
    /// </summary>
    private async Task<Foundry> AddFoundryAsync(DataResidency residency, Action<ModelDeployment>? configure = null, DataResidency? chatResidency = null)
    {
        var name = "foundry-" + Guid.NewGuid().ToString("N")[..10];
        var rerank = new ModelDeployment
        {
            Name = name + "/rerank", UpstreamModel = name + "-Cohere-rerank-v5-fast", Kind = ModelKind.Rerank, Publisher = "Cohere",
            Prices = [Price()],
        };
        configure?.Invoke(rerank);
        var chat = new ModelDeployment { Name = name + "/chat", UpstreamModel = name + "-gpt-4.1", Kind = ModelKind.Chat, Residency = chatResidency, Prices = [Price()] };
        using var scope = fixture.Services.CreateScope();
        var provider = new ProviderAccount
        {
            Name = name, Type = ProviderType.AzureAIFoundry, Residency = residency, BaseUrl = fixture.Upstream.Url + "/api/projects/umea",
            AuthMode = ProviderAuthMode.ApiKeyHeader, EncryptedCredential = scope.ServiceProvider.GetRequiredService<CredentialProtector>().Protect(fixture.ProviderSecret),
            TimeoutSeconds = 10, CreatedAt = DateTimeOffset.UtcNow, Deployments = [rerank, chat],
        };
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        db.ProviderAccounts.Add(provider);
        await db.SaveChangesAsync(Ct);

        // Cohere's v2 answer: scores and billed search units, no tokens.
        fixture.Upstream.Given(Request.Create().WithPath(new WildcardMatcher("/*/rerank")).UsingPost().WithBody(new JsonPartialMatcher(new { model = rerank.UpstreamModel })))
            .AtPriority(1).RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(
                """{"id":"rr-azure","results":[{"index":1,"relevance_score":0.93},{"index":0,"relevance_score":0.08}],"meta":{"api_version":{"version":"2"},"billed_units":{"search_units":1}}}"""));
        fixture.Upstream.Given(Request.Create().WithPath("/openai/v1/chat/completions").UsingPost().WithBody(new JsonPartialMatcher(new { model = chat.UpstreamModel })))
            .AtPriority(1).RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(
                """{"id":"chatcmpl-azure","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"Test answer"},"finish_reason":"stop"}],"usage":{"prompt_tokens":100,"completion_tokens":20}}"""));
        return new Foundry(name, provider.Id, rerank.Name, chat.Name);
    }

    private static ModelPrice Price() => new() { InputPerMillionUsd = 1, OutputPerMillionUsd = 2, EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-1) };

    private static string RerankBody(string model, string marker) => new JsonObject
    {
        ["model"] = model,
        ["query"] = marker + " library opening hours",
        ["documents"] = new JsonArray("The city library is open 10-19.", "Waste is collected every other week."),
        ["top_n"] = 2,
    }.ToJsonString();

    private Task<HttpResponseMessage> RerankAsync(TestKey key, string model, string marker, string? ceiling = null) =>
        fixture.SendRawAsync(key, "/v1/rerank", RerankBody(model, marker), headers: ceiling is null ? null : new Dictionary<string, string> { [Ceiling] = ceiling });

    private async Task<ILogEntry> UpstreamAsync(string marker) =>
        (await fixture.UpstreamRequestsAsync(e => e.RequestMessage?.Body?.Contains(marker, StringComparison.Ordinal) == true)).ShouldHaveSingleItem();

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["error"]!["code"]!.GetValue<string>();

    [Fact]
    public async Task A_Foundry_reranker_is_called_at_its_publishers_address_with_the_providers_key()
    {
        var foundry = await AddFoundryAsync(DataResidency.Eu);
        var marker = "FRR-" + Guid.NewGuid().ToString("N");
        using var response = await RerankAsync(await fixture.CreateKeyAsync(), foundry.Rerank, marker);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var results = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["results"]!.AsArray();
        results.Select(r => r!["index"]!.GetValue<int>()).ShouldBe([1, 0]);

        var upstream = await UpstreamAsync(marker);
        upstream.RequestMessage!.Path.ShouldBe("/providers/cohere/v2/rerank"); // from the publisher, lower case
        upstream.RequestMessage.Headers!["api-key"].ShouldBe([fixture.ProviderSecret]);
        upstream.RequestMessage.Headers.ContainsKey("Authorization").ShouldBeFalse();
        JsonNode.Parse(upstream.RequestMessage.Body!)!["model"]!.GetValue<string>().ShouldBe(foundry.Name + "-Cohere-rerank-v5-fast");

        // One provider: usage, health and circuit are the Foundry provider's.
        var usage = await fixture.UsageAsync(response);
        usage.Endpoint.ShouldBe(GatewayEndpoint.Rerank);
        usage.ProviderAccountId.ShouldBe(foundry.Id);
        usage.InputTokens.ShouldBeGreaterThan(0); // search units are not tokens: charged by the request-size estimate
        usage.OutputTokens.ShouldBe(0);
        usage.CostUsd.ShouldBe(usage.InputTokens / 1_000_000m); // $1 per million input tokens
        response.Headers.GetValues("x-ume-provider").Single().ShouldBe(foundry.Name);
    }

    [Fact]
    public async Task The_request_address_and_authentication_set_on_the_deployment_win()
    {
        var foundry = await AddFoundryAsync(DataResidency.Eu, d =>
        {
            d.Publisher = null;
            d.RequestBaseUrl = fixture.Upstream.Url + "/custom/cohere/v2/";
            d.RequestAuthMode = ProviderAuthMode.Bearer;
        });
        var marker = "FRR-" + Guid.NewGuid().ToString("N");
        using var response = await RerankAsync(await fixture.CreateKeyAsync(), foundry.Rerank, marker);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var upstream = await UpstreamAsync(marker);
        upstream.RequestMessage!.Path.ShouldBe("/custom/cohere/v2/rerank");
        upstream.RequestMessage.Headers!["Authorization"].ShouldBe(["Bearer " + fixture.ProviderSecret]);
        upstream.RequestMessage.Headers.ContainsKey("api-key").ShouldBeFalse();
    }

    [Fact]
    public async Task A_reranker_without_an_address_on_the_resource_is_not_routed()
    {
        // Another host would receive the provider's key: the gateway does not use it, whatever is stored.
        var upstream = new Uri(fixture.Upstream.Url!);
        var other = new UriBuilder(upstream) { Host = upstream.Host == "localhost" ? "127.0.0.1" : "localhost" };
        var offResource = await AddFoundryAsync(DataResidency.Eu, d => d.RequestBaseUrl = other.Uri + "providers/cohere/v2");
        var noPublisher = await AddFoundryAsync(DataResidency.Eu, d => d.Publisher = null);
        var key = await fixture.CreateKeyAsync();

        foreach (var model in new[] { offResource.Rerank, noPublisher.Rerank })
        {
            using var response = await RerankAsync(key, model, "FRR-" + Guid.NewGuid().ToString("N"));
            response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            (await ErrorCodeAsync(response)).ShouldBe("no_eligible_provider");
        }
    }

    [Fact]
    public async Task A_Foundry_chat_model_on_the_same_provider_is_unchanged()
    {
        var foundry = await AddFoundryAsync(DataResidency.Eu);
        var marker = "FCH-" + Guid.NewGuid().ToString("N");
        using var response = await fixture.SendAsync(await fixture.CreateKeyAsync(), foundry.Chat, prompt: marker);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var upstream = await UpstreamAsync(marker);
        upstream.RequestMessage!.Path.ShouldBe("/openai/v1/chat/completions");
        upstream.RequestMessage.Headers!["api-key"].ShouldBe([fixture.ProviderSecret]);
        response.Headers.GetValues("x-ume-residency").Single().ShouldBe("Eu");

        // The chat model is not a reranker, and the reranker is not a chat model.
        using var chatAsRerank = await RerankAsync(await fixture.CreateKeyAsync(), foundry.Chat, "FCH-" + Guid.NewGuid().ToString("N"));
        chatAsRerank.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_deployments_own_residency_is_where_routing_and_the_ceiling_place_it()
    {
        // An external resource (global deployments) with an EU data zone reranker; the chat model is the provider's.
        var foundry = await AddFoundryAsync(DataResidency.External, d => d.Residency = DataResidency.Eu);
        var key = await fixture.CreateKeyAsync();

        using (var within = await RerankAsync(key, foundry.Rerank, "FRR-" + Guid.NewGuid().ToString("N"), ceiling: "Eu"))
        {
            within.StatusCode.ShouldBe(HttpStatusCode.OK);
            within.Headers.GetValues("x-ume-residency").Single().ShouldBe("Eu");
        }

        using (var below = await RerankAsync(key, foundry.Rerank, "FRR-" + Guid.NewGuid().ToString("N"), ceiling: "OnPrem"))
        {
            below.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await ErrorCodeAsync(below)).ShouldBe("max_residency_not_met");
        }

        using var chat = await fixture.SendAsync(key, foundry.Chat, headers: new Dictionary<string, string> { [Ceiling] = "Eu" });
        chat.StatusCode.ShouldBe(HttpStatusCode.Forbidden); // the provider's External
        (await ErrorCodeAsync(chat)).ShouldBe("max_residency_not_met");

        // A key limited to EU reaches the reranker, not the chat model.
        var euKey = await fixture.CreateKeyAsync(k => k.AllowedResidencies = [DataResidency.Eu]);
        using (var allowed = await RerankAsync(euKey, foundry.Rerank, "FRR-" + Guid.NewGuid().ToString("N")))
        {
            allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var refused = await fixture.SendAsync(euKey, foundry.Chat);
        refused.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task A_wider_residency_on_the_deployment_is_kept_out_of_a_lower_ceiling()
    {
        // An EU resource with a global deployment: the deployment is External, whatever the provider says.
        var foundry = await AddFoundryAsync(DataResidency.Eu, d => d.Residency = DataResidency.External, chatResidency: DataResidency.External);
        var key = await fixture.CreateKeyAsync();

        using var rerank = await RerankAsync(key, foundry.Rerank, "FRR-" + Guid.NewGuid().ToString("N"), ceiling: "Eu");
        rerank.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(rerank)).ShouldBe("max_residency_not_met");

        using var chat = await fixture.SendAsync(key, foundry.Chat, headers: new Dictionary<string, string> { [Ceiling] = "Eu" });
        chat.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var open = await fixture.SendAsync(key, foundry.Chat);
        open.StatusCode.ShouldBe(HttpStatusCode.OK);
        open.Headers.GetValues("x-ume-residency").Single().ShouldBe("External");
    }

    [Fact]
    public async Task Personal_data_goes_to_a_deployment_whose_own_residency_is_on_prem()
    {
        var external = await AddFoundryAsync(DataResidency.External);
        var onPrem = await AddFoundryAsync(DataResidency.External, chatResidency: DataResidency.OnPrem);
        var key = await fixture.CreateKeyAsync(k => k.PiiPolicy = PiiPolicy.RerouteToOnPrem);

        using var refused = await fixture.SendAsync(key, external.Chat, prompt: "Personnummer 19121212-1212");
        refused.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        using var allowed = await fixture.SendAsync(key, onPrem.Chat, prompt: "Personnummer 19121212-1212");
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        allowed.Headers.GetValues("x-ume-residency").Single().ShouldBe("OnPrem");
    }

    [Fact]
    public async Task A_token_quota_for_a_residency_counts_a_deployment_by_its_own_residency()
    {
        var onPrem = await AddFoundryAsync(DataResidency.External, chatResidency: DataResidency.OnPrem);
        var external = await AddFoundryAsync(DataResidency.OnPrem, chatResidency: DataResidency.External);
        var key = await fixture.CreateKeyAsync();
        foreach (var model in new[] { onPrem.Chat, external.Chat })
        {
            using var response = await fixture.SendAsync(key, model);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            await fixture.UsageAsync(response);
        }

        // Seeded from the usage records (a cold counter): only the on-prem deployment's 120 tokens.
        var quota = new Budget
        {
            Scope = BudgetScope.Team, ScopeId = key.TeamId, Unit = BudgetUnit.Tokens, LimitTokens = 1000, Residency = DataResidency.OnPrem,
            Period = BudgetPeriod.Daily,
        };
        using var scope = fixture.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().SpentAsync(quota, BudgetPeriods.GetWindow(BudgetPeriod.Daily, DateTimeOffset.UtcNow), Ct))
            .ShouldBe(120);
    }

    [Fact]
    public async Task The_model_list_shows_the_reranker_with_its_own_residency_within_the_ceiling()
    {
        var foundry = await AddFoundryAsync(DataResidency.External, d => d.Residency = DataResidency.Eu);
        var key = await fixture.CreateKeyAsync();

        var all = await ModelsAsync(key, null);
        var rerank = all.Single(m => m["id"]!.GetValue<string>() == foundry.Rerank);
        rerank["ume"]!["kind"]!.GetValue<string>().ShouldBe("Rerank");
        rerank["ume"]!["residency"]!.GetValue<string>().ShouldBe("Eu");
        all.Single(m => m["id"]!.GetValue<string>() == foundry.Chat)["ume"]!["residency"]!.GetValue<string>().ShouldBe("External");

        var eu = await ModelsAsync(key, "Eu");
        eu.ShouldContain(m => m["id"]!.GetValue<string>() == foundry.Rerank);
        eu.ShouldNotContain(m => m["id"]!.GetValue<string>() == foundry.Chat);
        (await ModelsAsync(key, "OnPrem")).ShouldNotContain(m => m["id"]!.GetValue<string>() == foundry.Rerank);
    }

    private async Task<List<JsonNode>> ModelsAsync(TestKey key, string? ceiling)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        if (ceiling is not null)
        {
            request.Headers.Add(Ceiling, ceiling);
        }

        using var response = await fixture.Client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return [.. JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["data"]!.AsArray().Select(m => m!)];
    }
}
