using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.AdminApi.Tests;

public sealed class ModelDiscoveryTests
{
    private sealed class ModelList(string json) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Speech_and_realtime_models_get_their_kind_and_other_audio_models_are_left_out()
    {
        using var http = new HttpClient(new ModelList("""
            {"data":[{"id":"gpt-4.1"},{"id":"whisper-1"},{"id":"gpt-4o-mini-transcribe"},{"id":"text-embedding-3-small"},
                     {"id":"tts-1"},{"id":"gpt-4o-realtime-preview"},{"id":"gpt-4o-audio-preview"},{"id":"dall-e-3"},
                     {"id":"gpt-realtime-translate"},{"id":"gpt-realtime-whisper"}]}
            """));
        var provider = new ProviderAccount { Name = "openai", BaseUrl = "https://api.example/v1", Type = ProviderType.OpenAI };

        var models = await ModelDiscovery.DiscoverAsync(provider, http, new CredentialProtector(new EphemeralDataProtectionProvider()), TestContext.Current.CancellationToken);

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

    [Fact]
    public async Task A_query_string_on_the_base_url_is_kept_after_the_models_path()
    {
        var list = new ModelList("""{"data":[{"id":"gpt-4.1"}]}""");
        using var http = new HttpClient(list);
        var provider = new ProviderAccount { Name = "foundry", BaseUrl = "https://res.example/openai?api-version=2025-04-01-preview", Type = ProviderType.AzureAIFoundry };

        await ModelDiscovery.DiscoverAsync(provider, http, new CredentialProtector(new EphemeralDataProtectionProvider()), TestContext.Current.CancellationToken);

        list.LastUri.ShouldBe(new Uri("https://res.example/openai/models?api-version=2025-04-01-preview"));
    }
}
