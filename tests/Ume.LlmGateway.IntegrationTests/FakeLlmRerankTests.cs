extern alias Fake;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ume.LlmGateway.IntegrationTests;

/// <summary>The fake provider's Cohere-compatible reranker, for local development without a rerank account.</summary>
public sealed class FakeLlmRerankTests : IAsyncLifetime
{
    private WebApplicationFactory<Fake::Program> _fake = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _fake = new WebApplicationFactory<Fake::Program>().WithWebHostBuilder(b => b.UseEnvironment("Testing"));
        _client = _fake.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _fake.DisposeAsync();
    }

    [Fact]
    public async Task Documents_about_the_query_rank_first()
    {
        using var response = await _client.PostAsJsonAsync("/v1/rerank", new JsonObject
        {
            ["model"] = "fake-rerank",
            ["query"] = "bygglov för garage",
            ["documents"] = new JsonArray("Skolskjuts till skolan.", new JsonObject { ["text"] = "Bygglov för garage söks i e-tjänsten." }, "Garage och carport."),
            ["top_n"] = 2,
            ["return_documents"] = true,
        }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        var results = body["results"]!.AsArray();
        results.Count.ShouldBe(2);
        results[0]!["index"]!.GetValue<int>().ShouldBe(1);
        results[0]!["relevance_score"]!.GetValue<double>().ShouldBe(1);
        results[1]!["index"]!.GetValue<int>().ShouldBe(2);
        results[0]!["document"]!["text"]!.GetValue<string>().ShouldStartWith("Bygglov");
        body["usage"]!["prompt_tokens"]!.GetValue<int>().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Missing_documents_are_400()
    {
        using var response = await _client.PostAsJsonAsync("/v1/rerank", new JsonObject { ["query"] = "x" }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
