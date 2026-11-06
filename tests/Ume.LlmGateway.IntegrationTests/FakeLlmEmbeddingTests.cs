extern alias Fake;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using FakeEmbeddings = Fake::Ume.LlmGateway.FakeLlm.FakeEmbeddings;

namespace Ume.LlmGateway.IntegrationTests;

/// <summary>The fake provider's embeddings: sizes, similarity and the base64 encoding clients ask for.</summary>
public sealed class FakeLlmEmbeddingTests : IAsyncLifetime
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
    public async Task Without_a_size_the_original_eight_values_are_returned()
    {
        var data = await EmbedAsync(new JsonObject { ["model"] = "fake-embed", ["input"] = "hello" });
        var vector = Floats(data[0]!["embedding"]!);
        vector.Length.ShouldBe(FakeEmbeddings.DefaultDimensions);
        // The original fake: the first 8 bytes of SHA-256("hello") scaled to [-1, 1).
        var hash = System.Security.Cryptography.SHA256.HashData("hello"u8);
        vector.ShouldBe([.. hash.Take(8).Select(b => (b - 128) / 128f)]);
    }

    [Theory]
    [InlineData("fake-embed", 1024, 1024)]
    [InlineData("fake-embed-1024", null, 1024)]
    [InlineData("bge-m3-768", null, 768)]
    [InlineData("fake-embed-1024", 256, 256)] // the parameter wins
    public async Task The_size_comes_from_dimensions_or_the_model_name(string model, int? dimensions, int expected)
    {
        var body = new JsonObject { ["model"] = model, ["input"] = new JsonArray("one", "two") };
        if (dimensions is { } d)
        {
            body["dimensions"] = d;
        }

        var data = await EmbedAsync(body);
        data.Count.ShouldBe(2);
        var vector = Floats(data[0]!["embedding"]!);
        vector.Length.ShouldBe(expected);
        Math.Sqrt(vector.Sum(v => (double)v * v)).ShouldBe(1, 1e-4); // L2-normalised
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4097)]
    [InlineData("1024")]
    public async Task A_size_out_of_range_is_400_like_openai(object dimensions)
    {
        var body = new JsonObject { ["model"] = "fake-embed", ["input"] = "x", ["dimensions"] = JsonValue.Create(dimensions) };
        using var response = await _client.PostAsJsonAsync("/v1/embeddings", body, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["error"]!["param"]!.GetValue<string>().ShouldBe("dimensions");
    }

    [Fact]
    public async Task Similar_texts_get_similar_vectors_and_the_same_text_the_same_vector()
    {
        var data = await EmbedAsync(new JsonObject
        {
            ["model"] = "fake-embed-512",
            ["input"] = new JsonArray(
                "Hur ansöker jag om bygglov för ett garage?",
                "Hur ansöker man om bygglov för garage",
                "Skolskjuts beviljas för elever med lång väg till skolan.",
                "Hur ansöker jag om bygglov för ett garage?"),
        });
        var vectors = data.Select(n => Floats(n!["embedding"]!)).ToList();
        Cosine(vectors[0], vectors[1]).ShouldBeGreaterThan(Cosine(vectors[0], vectors[2]) + 0.3);
        vectors[3].ShouldBe(vectors[0]);
    }

    [Fact]
    public async Task Base64_is_little_endian_float32()
    {
        var plain = Floats((await EmbedAsync(new JsonObject { ["model"] = "fake-embed-64", ["input"] = "text" }))[0]!["embedding"]!);
        var encoded = (await EmbedAsync(new JsonObject { ["model"] = "fake-embed-64", ["input"] = "text", ["encoding_format"] = "base64" }))[0]!["embedding"]!;
        Floats(encoded).ShouldBe(plain);

        // The original 8 values too.
        var original = (await EmbedAsync(new JsonObject { ["model"] = "fake-embed", ["input"] = "text", ["encoding_format"] = "base64" }))[0]!["embedding"]!;
        Floats(original).Length.ShouldBe(FakeEmbeddings.DefaultDimensions);
    }

    private async Task<JsonArray> EmbedAsync(JsonObject body)
    {
        using var response = await _client.PostAsJsonAsync("/v1/embeddings", body, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["data"]!.AsArray();
    }

    private static float[] Floats(JsonNode embedding)
    {
        if (embedding is JsonArray array)
        {
            return [.. array.Select(v => v!.GetValue<float>())];
        }

        var bytes = Convert.FromBase64String(embedding.GetValue<string>());
        return [.. Enumerable.Range(0, bytes.Length / sizeof(float)).Select(i => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float))))];
    }

    private static double Cosine(float[] a, float[] b) => a.Zip(b, (x, y) => (double)x * y).Sum(); // both L2-normalised
}
