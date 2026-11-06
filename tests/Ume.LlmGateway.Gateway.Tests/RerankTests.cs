using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Infrastructure.Providers;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary><c>POST /v1/rerank</c>: optional, Cohere-compatible, passed through to providers with the Rerank capability.</summary>
public sealed class RerankTests(GatewayFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject Request(string model, string marker) => new()
    {
        ["model"] = model,
        ["query"] = marker + " Hur ansöker jag om bygglov?",
        ["documents"] = new JsonArray("Skolskjuts beviljas för elever med lång väg.", new JsonObject { ["text"] = "Bygglov söker du via e-tjänsten." }),
        ["top_n"] = 2,
        ["return_documents"] = false,
    };

    [Fact]
    public async Task A_rerank_request_is_passed_through_and_charged_by_input_tokens()
    {
        var marker = "RERANK-" + Guid.NewGuid().ToString("N");
        var sent = Request("eu/rerank", marker);
        using var response = await fixture.SendRawAsync(await fixture.CreateKeyAsync(), "/v1/rerank", sent.ToJsonString());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("x-ume-residency").Single().ShouldBe("Eu");
        var results = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["results"]!.AsArray();
        results.Select(r => r!["index"]!.GetValue<int>()).ShouldBe([1, 0]); // the provider's order, unchanged
        results[0]!["relevance_score"]!.GetValue<double>().ShouldBe(0.91);

        // Only the model name is rewritten; documents (strings and objects), top_n and return_documents go as sent.
        var upstream = JsonNode.Parse(await fixture.UpstreamBodyAsync("v1", marker))!;
        upstream["model"]!.GetValue<string>().ShouldBe("eu-rerank");
        foreach (var field in new[] { "query", "documents", "top_n", "return_documents" })
        {
            JsonNode.DeepEquals(upstream[field], sent[field]).ShouldBeTrue(field);
        }

        var usage = await fixture.UsageAsync(response);
        usage.Endpoint.ShouldBe(GatewayEndpoint.Rerank);
        usage.InputTokens.ShouldBe(120);
        usage.OutputTokens.ShouldBe(0);
        usage.CostUsd.ShouldBe(0.00012m); // 120 tokens at $1 per million
    }

    [Fact]
    public async Task Long_documents_are_not_refused_by_the_context_window()
    {
        // Rerankers score each document separately and split long ones themselves: the whole request is not compared.
        var small = await fixture.AddDeploymentAsync("eu", "rerank", contextWindow: 10);
        var body = Request(small, "LONG-" + Guid.NewGuid().ToString("N"));
        body["documents"] = new JsonArray([.. Enumerable.Repeat(string.Concat(Enumerable.Repeat("lorem ipsum ", 20)), 6).Select(d => JsonValue.Create(d))]);
        using var response = await fixture.SendRawAsync(await fixture.CreateKeyAsync(), "/v1/rerank", body.ToJsonString());
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Rerank_and_other_kinds_do_not_mix()
    {
        var key = await fixture.CreateKeyAsync();
        using (var chatModel = await fixture.SendRawAsync(key, "/v1/rerank", Request("eu/ok", "MIX").ToJsonString()))
        {
            chatModel.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using var chat = await fixture.SendAsync(key, "eu/rerank");
        chat.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rerankers_are_listed_with_their_kind()
    {
        var key = await fixture.CreateKeyAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        using var response = await fixture.Client.SendAsync(request, Ct);
        var model = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["data"]!.AsArray()
            .Single(m => m!["id"]!.GetValue<string>() == "eu/rerank")!;
        model["ume"]!["kind"]!.GetValue<string>().ShouldBe("Rerank");
    }

    [Theory]
    [InlineData("""{"results":[],"usage":{"prompt_tokens":40,"total_tokens":40}}""", 40)] // Berget AI
    [InlineData("""{"results":[],"usage":{"total_tokens":55}}""", 55)] // vLLM
    [InlineData("""{"results":[],"meta":{"billed_units":{"search_units":1,"input_tokens":70}}}""", 70)] // Cohere
    [InlineData("""{"meta":{"tokens":{"input_tokens":30,"output_tokens":0}},"results":[]}""", 30)]
    [InlineData("""{"meta":{"billed_units":{"input_tokens":70}},"usage":{"total_tokens":12}}""", 12)] // usage wins
    [InlineData("""{"usage":{"total_tokens":12},"meta":{"billed_units":{"input_tokens":70}}}""", 12)]
    [InlineData("""{"results":[],"meta":{"billed_units":{"search_units":1}}}""", 0)] // nothing in tokens: estimated later
    public void Rerank_usage_is_read_in_every_shape_providers_send(string json, long inputTokens)
    {
        var usage = ProviderJson.ReadUsage(Encoding.UTF8.GetBytes(json));
        usage.InputTokens.ShouldBe(inputTokens);
        usage.OutputTokens.ShouldBe(0);
    }
}
