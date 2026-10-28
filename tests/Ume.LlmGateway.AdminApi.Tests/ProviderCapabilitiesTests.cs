using System.Net;
using System.Net.Http.Json;

namespace Ume.LlmGateway.AdminApi.Tests;

/// <summary>Known provider types serve what their API offers; admins choose endpoints only for an OpenAI-compatible server.</summary>
public sealed class ProviderCapabilitiesTests(AdminFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Body(string name, string type, string baseUrl, string[]? capabilities) => new
    {
        name, baseUrl, type, authMode = "Bearer", credential = "secret", residency = "Eu", capabilities, timeoutSeconds = 10, isEnabled = true,
    };

    private static async Task<string[]> CapabilitiesAsync(HttpResponseMessage response, HttpStatusCode status) =>
        [.. (await AdminFixture.ReadAsync(response, status))["capabilities"]!.AsArray().Select(c => c!.GetValue<string>()).Order()];

    [Fact]
    public async Task Known_types_need_no_capabilities_and_ignore_any_sent()
    {
        using var client = await fixture.ClientAsync();
        var name = "openai-" + Guid.NewGuid().ToString("N")[..8];
        using var create = await client.PostAsJsonAsync("/api/providers", Body(name, "OpenAI", "https://api.openai.com/v1", null), Ct);
        var created = await AdminFixture.ReadAsync(create, HttpStatusCode.Created);
        created["capabilities"]!.AsArray().Select(c => c!.GetValue<string>()).Order()
            .ShouldBe(["AudioTranscriptions", "ChatCompletions", "Embeddings", "Images", "Realtime", "Responses", "Streaming"]);

        using var update = await client.PutAsJsonAsync($"/api/providers/{created["id"]}", Body(name, "OpenAI", "https://api.openai.com/v1", ["ChatCompletions"]), Ct);
        (await CapabilitiesAsync(update, HttpStatusCode.OK)).Length.ShouldBe(7);
    }

    [Fact]
    public async Task OpenAI_compatible_servers_keep_the_endpoints_chosen_and_start_with_chat_and_embeddings()
    {
        using var client = await fixture.ClientAsync();
        var name = "vllm-" + Guid.NewGuid().ToString("N")[..8];
        using var create = await client.PostAsJsonAsync("/api/providers", Body(name, "OpenAICompatible", "https://vllm.invalid/v1", null), Ct);
        var created = await AdminFixture.ReadAsync(create, HttpStatusCode.Created);
        created["capabilities"]!.AsArray().Select(c => c!.GetValue<string>()).Order().ShouldBe(["ChatCompletions", "Embeddings", "Streaming"]);

        using var speech = await client.PutAsJsonAsync($"/api/providers/{created["id"]}", Body(name, "OpenAICompatible", "https://vllm.invalid/v1", ["AudioTranscriptions"]), Ct);
        (await CapabilitiesAsync(speech, HttpStatusCode.OK)).ShouldBe(["AudioTranscriptions"]);

        using var kept = await client.PutAsJsonAsync($"/api/providers/{created["id"]}", Body(name, "OpenAICompatible", "https://vllm.invalid/v1", null), Ct);
        (await CapabilitiesAsync(kept, HttpStatusCode.OK)).ShouldBe(["AudioTranscriptions"]);

        using var none = await client.PutAsJsonAsync($"/api/providers/{created["id"]}", Body(name, "OpenAICompatible", "https://vllm.invalid/v1", []), Ct);
        none.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
