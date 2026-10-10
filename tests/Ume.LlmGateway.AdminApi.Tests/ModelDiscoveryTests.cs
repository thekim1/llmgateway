using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Security;
using Ume.LlmGateway.ServiceDefaults;

namespace Ume.LlmGateway.AdminApi.Tests;

public sealed class ModelDiscoveryTests(AdminFixture fixture)
{
    private static readonly CredentialProtector Protector = new(new EphemeralDataProtectionProvider());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Answers every request with <paramref name="respond"/> and records what was sent.</summary>
    private sealed class FakeProvider(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public FakeProvider(string json, HttpStatusCode status = HttpStatusCode.OK)
            : this(_ => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") })
        {
        }

        public List<(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers)> Requests { get; } = [];

        public Uri? LastUri => Requests.Count == 0 ? null : Requests[^1].Uri;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add((request.Method, request.RequestUri!, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)));
            }

            return Task.FromResult(respond(request));
        }
    }

    private static ProviderAccount Provider(string baseUrl, ProviderType type = ProviderType.OpenAI, ProviderAuthMode auth = ProviderAuthMode.Bearer, string? credential = "secret") =>
        new() { Name = "provider", BaseUrl = baseUrl, Type = type, AuthMode = auth, EncryptedCredential = Protector.Protect(credential) };

    private static async Task<DiscoveredModel[]> DiscoverAsync(ProviderAccount provider, HttpMessageHandler handler, bool requireHttps = false)
    {
        using var http = new HttpClient(handler, disposeHandler: false);
        return await ModelDiscovery.DiscoverAsync(provider, http, Protector, requireHttps, Ct);
    }

    [Fact]
    public async Task Speech_and_realtime_models_get_their_kind_and_other_audio_models_are_left_out()
    {
        using var list = new FakeProvider("""
            {"data":[{"id":"gpt-4.1"},{"id":"whisper-1"},{"id":"gpt-4o-mini-transcribe"},{"id":"text-embedding-3-small"},
                     {"id":"tts-1"},{"id":"gpt-4o-realtime-preview"},{"id":"gpt-4o-audio-preview"},{"id":"dall-e-3"},
                     {"id":"gpt-realtime-translate"},{"id":"gpt-realtime-whisper"}]}
            """);

        var models = await DiscoverAsync(Provider("https://api.example/v1"), list);

        models.Select(m => (m.Id, m.Kind)).ShouldBe(
        [
            ("gpt-4.1", ModelKind.Chat),
            ("gpt-4o-mini-transcribe", ModelKind.Transcription),
            ("gpt-4o-realtime-preview", ModelKind.Realtime),
            ("gpt-realtime-translate", ModelKind.SpeechTranslation),
            ("gpt-realtime-whisper", ModelKind.Transcription),
            ("text-embedding-3-small", ModelKind.Embedding),
            ("whisper-1", ModelKind.Transcription),
        ]);
    }

    [Theory]
    [InlineData("https://res.example/openai?api-version=2025-04-01-preview", "https://res.example/openai/models?api-version=2025-04-01-preview")]
    [InlineData("https://res.example/openai/?api-version=1", "https://res.example/openai/models?api-version=1")]
    [InlineData("https://api.example/v1/", "https://api.example/v1/models")]
    [InlineData("https://api.example/v1", "https://api.example/v1/models")]
    public async Task The_models_path_goes_after_the_base_path_and_before_its_query_string(string baseUrl, string expected)
    {
        using var list = new FakeProvider("""{"data":[{"id":"gpt-4.1"}]}""");

        await DiscoverAsync(Provider(baseUrl, ProviderType.AzureAIFoundry), list);

        list.LastUri.ShouldBe(new Uri(expected));
    }

    [Fact]
    public async Task Anthropic_asks_for_the_whole_list_and_keeps_a_base_query_string()
    {
        using var plain = new FakeProvider("""{"data":[{"id":"claude-x"}]}""");
        await DiscoverAsync(Provider("https://api.anthropic.example/v1", ProviderType.Anthropic, ProviderAuthMode.XApiKeyHeader), plain);
        plain.LastUri.ShouldBe(new Uri("https://api.anthropic.example/v1/models?limit=1000"));

        using var withQuery = new FakeProvider("""{"data":[{"id":"claude-x"}]}""");
        await DiscoverAsync(Provider("https://proxy.example/anthropic?tenant=a", ProviderType.Anthropic, ProviderAuthMode.XApiKeyHeader), withQuery);
        withQuery.LastUri.ShouldBe(new Uri("https://proxy.example/anthropic/models?tenant=a&limit=1000"));
    }

    [Fact]
    public async Task Ollama_lists_on_the_native_api_without_the_v1_segment_and_reads_details()
    {
        using var ollama = new FakeProvider(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/api/tags", StringComparison.Ordinal)
                ? """{"models":[{"name":"llama3:8b"},{"name":"nomic-embed-text"}]}"""
                : """{"capabilities":["completion","tools"],"model_info":{"llama.context_length":8192}}""", Encoding.UTF8, "application/json"),
        });

        var models = await DiscoverAsync(Provider("http://ollama.local:11434/v1/", ProviderType.Ollama, credential: null), ollama);

        ollama.Requests[0].Uri.ShouldBe(new Uri("http://ollama.local:11434/api/tags"));
        ollama.Requests.Skip(1).ShouldAllBe(r => r.Method == HttpMethod.Post && r.Uri == new Uri("http://ollama.local:11434/api/show"));
        ollama.Requests.ShouldAllBe(r => !r.Headers.ContainsKey("Authorization"));
        var llama = models.Single(m => m.Id == "llama3:8b");
        llama.ContextWindow.ShouldBe(8192);
        llama.Features.ShouldBe(["tools"]);
    }

    [Theory]
    [InlineData(ProviderType.OpenAI, ProviderAuthMode.Bearer, "Authorization", "Bearer secret")]
    [InlineData(ProviderType.AzureAIFoundry, ProviderAuthMode.ApiKeyHeader, "api-key", "secret")]
    [InlineData(ProviderType.OpenAI, ProviderAuthMode.XApiKeyHeader, "x-api-key", "secret")]
    public async Task The_credential_goes_in_the_header_the_auth_mode_names(ProviderType type, ProviderAuthMode mode, string header, string value)
    {
        using var list = new FakeProvider("""{"data":[]}""");

        await DiscoverAsync(Provider("https://api.example/v1", type, mode), list);

        var sent = list.Requests.Single().Headers;
        sent[header].ShouldBe(value);
        string[] credentialHeaders = ["Authorization", "api-key", "x-api-key"];
        credentialHeaders.Where(h => h != header).ShouldAllBe(h => !sent.ContainsKey(h));
        sent.ContainsKey("anthropic-version").ShouldBeFalse();
        list.LastUri!.Query.ShouldNotContain("secret");
    }

    [Fact]
    public async Task Anthropic_gets_its_api_version_header()
    {
        using var list = new FakeProvider("""{"data":[]}""");

        await DiscoverAsync(Provider("https://api.anthropic.example/v1", ProviderType.Anthropic, ProviderAuthMode.XApiKeyHeader), list);

        var sent = list.Requests.Single().Headers;
        sent["x-api-key"].ShouldBe("secret");
        sent["anthropic-version"].ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Without_a_credential_no_auth_header_is_sent()
    {
        using var list = new FakeProvider("""{"data":[]}""");

        await DiscoverAsync(Provider("https://api.example/v1", credential: null), list);

        list.Requests.Single().Headers.ContainsKey("Authorization").ShouldBeFalse();
    }

    [Fact]
    public async Task Plain_http_is_refused_when_https_is_required_before_anything_is_sent()
    {
        using var list = new FakeProvider("""{"data":[]}""");

        var refused = await Should.ThrowAsync<ApiFaultException>(() => DiscoverAsync(Provider("http://api.example/v1"), list, requireHttps: true));

        refused.Status.ShouldBe(400);
        list.Requests.ShouldBeEmpty();
        (await DiscoverAsync(Provider("https://api.example/v1"), list, requireHttps: true)).ShouldBeEmpty();
        (await DiscoverAsync(Provider("http://api.example/v1"), list, requireHttps: false)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Kontrollera API-nyckeln.")]
    [InlineData(HttpStatusCode.Forbidden, "Kontrollera API-nyckeln.")]
    [InlineData(HttpStatusCode.NotFound, "Kontrollera adressen och inloggningsmetoden.")]
    public async Task An_error_status_becomes_a_bad_gateway_with_a_hint(HttpStatusCode status, string hint)
    {
        using var list = new FakeProvider("""{"error":"nope"}""", status);

        var fault = await Should.ThrowAsync<ApiFaultException>(() => DiscoverAsync(Provider("https://api.example/v1"), list));

        fault.Status.ShouldBe(502);
        fault.Message.ShouldContain(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture));
        fault.Message.ShouldEndWith(hint);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"unexpected":true}""")]
    [InlineData("42")]
    public async Task An_answer_without_a_model_list_becomes_a_bad_gateway(string body)
    {
        using var list = new FakeProvider(body);

        var fault = await Should.ThrowAsync<ApiFaultException>(() => DiscoverAsync(Provider("https://api.example/v1"), list));

        fault.Status.ShouldBe(502);
    }

    [Fact]
    public async Task A_connection_failure_becomes_a_bad_gateway()
    {
        using var down = new FakeProvider(_ => throw new HttpRequestException("connection refused"));

        var fault = await Should.ThrowAsync<ApiFaultException>(() => DiscoverAsync(Provider("https://api.example/v1"), down));

        fault.Status.ShouldBe(502);
    }

    [Fact]
    public async Task Azure_OpenAI_cannot_list_deployments()
    {
        using var list = new FakeProvider("""{"data":[]}""");

        var fault = await Should.ThrowAsync<ApiFaultException>(() => DiscoverAsync(Provider("https://res.example/openai", ProviderType.AzureOpenAI, ProviderAuthMode.ApiKeyHeader), list));

        fault.Status.ShouldBe(400);
        list.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Models_already_added_are_marked()
    {
        using var list = new FakeProvider("""{"data":[{"id":"gpt-4.1"},{"id":"gpt-4o"}]}""");
        var provider = Provider("https://api.example/v1");
        provider.Deployments.Add(new ModelDeployment { Name = "gpt-4.1", UpstreamModel = "GPT-4.1" });

        var models = await DiscoverAsync(provider, list);

        models.Single(m => m.Id == "gpt-4.1").AlreadyAdded.ShouldBeTrue();
        models.Single(m => m.Id == "gpt-4o").AlreadyAdded.ShouldBeFalse();
    }

    [Fact]
    public void The_discovery_client_makes_a_single_attempt()
    {
        using var handler = fixture.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(ModelDiscovery.HttpClientName);

        Pipeline(handler).ShouldNotContain(h => h is ResilienceHandler);
        Pipeline(fixture.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("gateway-operations")).ShouldContain(h => h is ResilienceHandler);
    }

    private static List<HttpMessageHandler> Pipeline(HttpMessageHandler handler)
    {
        var chain = new List<HttpMessageHandler>();
        for (HttpMessageHandler? current = handler; current is not null; current = (current as DelegatingHandler)?.InnerHandler)
        {
            chain.Add(current);
        }

        return chain;
    }
}
