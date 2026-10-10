using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Providers;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.AdminApi;

public sealed record DiscoveredPrice(decimal InputPerMillionUsd, decimal CachedInputPerMillionUsd, decimal OutputPerMillionUsd);

public sealed record DiscoveredModel(
    string Id,
    string DisplayName,
    ModelKind Kind,
    ParameterProfile ParameterProfile,
    int? ContextWindow,
    string[] Features,
    DiscoveredPrice? Price,
    bool AlreadyAdded);

/// <summary>
/// Lists the models an upstream provider exposes, using the provider's stored credential. Doubles as a
/// connection test. Providers rarely publish prices or capabilities, so those fields are best effort.
/// </summary>
public static partial class ModelDiscovery
{
    public const string HttpClientName = "provider-discovery";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>Models the gateway has no endpoint for (speech synthesis, images, video, moderation, chat with audio).</summary>
    [GeneratedRegex(@"(^|[-/])(tts|dall-e|gpt-image|sora)|moderation|audio", RegexOptions.IgnoreCase)]
    private static partial Regex UnsupportedModel();

    /// <summary>Speech to text, served by /v1/audio/transcriptions, /v1/audio/translations and live on /v1/realtime.</summary>
    [GeneratedRegex(@"whisper|transcribe", RegexOptions.IgnoreCase)]
    private static partial Regex TranscriptionModel();

    /// <summary>Live speech translation (interpreting), served by /v1/realtime/translations.</summary>
    [GeneratedRegex(@"realtime-translate|live-translate", RegexOptions.IgnoreCase)]
    private static partial Regex SpeechTranslationModel();

    /// <summary>Realtime conversation models, served by /v1/realtime.</summary>
    [GeneratedRegex(@"realtime", RegexOptions.IgnoreCase)]
    private static partial Regex RealtimeModel();

    [GeneratedRegex(@"^(o\d|gpt-5)", RegexOptions.IgnoreCase)]
    private static partial Regex ReasoningModel();

    /// <param name="requireHttps">Only https base URLs (production), the same rule the gateway applies to provider calls.</param>
    public static async Task<DiscoveredModel[]> DiscoverAsync(
        ProviderAccount provider, HttpClient http, CredentialProtector protector, bool requireHttps, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(protector);
        if (provider.Type == ProviderType.AzureOpenAI)
        {
            throw new ApiFaultException(400, "Azure OpenAI kan inte lista distributioner via API:et. Lägg till modellerna manuellt.");
        }

        var anthropic = provider.Type == ProviderType.Anthropic;
        var ollama = provider.Type is ProviderType.Ollama or ProviderType.OllamaCloud;
        // Ollama's OpenAI-compatible base URL ends in /v1; model listing lives on the native /api.
        var baseUrl = ollama ? WithoutVersionSegment(provider.BaseUrl) : provider.BaseUrl;
        var listUri = Build(baseUrl, ollama ? "api/tags" : "models");
        if (anthropic)
        {
            listUri = WithQueryParameter(listUri, "limit=1000");
        }
        if (!ProviderTransport.IsPermitted(listUri, requireHttps))
        {
            throw new ApiFaultException(400, "Leverantörens adress måste använda https.");
        }
        var credential = protector.Unprotect(provider.EncryptedCredential);

        HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
        {
            var request = new HttpRequestMessage(method, uri);
            ProviderAuth.Apply(request, provider, credential);
            return request;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        JsonNode? body;
        try
        {
            using var request = CreateRequest(HttpMethod.Get, listUri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var hint = (int)response.StatusCode is 401 or 403 ? "Kontrollera API-nyckeln." : "Kontrollera adressen och inloggningsmetoden.";
                throw new ApiFaultException(502, $"Leverantören svarade {(int)response.StatusCode}. {hint}");
            }
            body = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ApiFaultException(504, "Leverantören svarade inte i tid.");
        }
        catch (HttpRequestException)
        {
            throw new ApiFaultException(502, "Kunde inte ansluta till leverantören. Kontrollera adressen.");
        }
        catch (System.Text.Json.JsonException)
        {
            throw new ApiFaultException(502, "Leverantören returnerade ett oväntat svar och verkar inte ha en modellista.");
        }

        var items = body switch
        {
            JsonArray array => array,
            JsonObject obj => (obj["data"] ?? obj["models"]) as JsonArray,
            _ => null,
        } ?? throw new ApiFaultException(502, "Leverantören returnerade ingen modellista.");

        if (ollama)
        {
            await EnrichOllamaAsync(items, http, CreateRequest, Build(baseUrl, "api/show"), timeout.Token);
        }

        var existing = provider.Deployments.Select(d => d.UpstreamModel).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. items.OfType<JsonObject>()
            .Select(item => Map(item, existing))
            .OfType<DiscoveredModel>()
            .Where(m => !UnsupportedModel().IsMatch(m.Id) || m.Kind is ModelKind.Transcription or ModelKind.Realtime or ModelKind.SpeechTranslation)
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary><paramref name="path"/> after the base URL's path; a query string on the base URL (Azure's api-version) is kept.</summary>
    private static Uri Build(string baseUrl, string path)
    {
        try
        {
            return ProviderTransport.BuildUri(baseUrl, path);
        }
        catch (UriFormatException)
        {
            throw new ApiFaultException(400, "Leverantörens adress är ogiltig.");
        }
    }

    /// <summary>The base URL without a trailing <c>/v1</c> path segment (its query string is kept).</summary>
    private static string WithoutVersionSegment(string baseUrl)
    {
        var queryStart = baseUrl.IndexOf('?', StringComparison.Ordinal);
        var path = (queryStart < 0 ? baseUrl : baseUrl[..queryStart]).TrimEnd('/');
        var query = queryStart < 0 ? string.Empty : baseUrl[queryStart..];
        return (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? path[..^3] : path) + query;
    }

    private static Uri WithQueryParameter(Uri uri, string parameter) =>
        new UriBuilder(uri) { Query = uri.Query.Length > 1 ? uri.Query[1..] + "&" + parameter : parameter }.Uri;

    /// <summary>Best effort: <c>/api/show</c> reports capabilities and context length per Ollama model.</summary>
    private static async Task EnrichOllamaAsync(
        JsonArray items, HttpClient http, Func<HttpMethod, Uri, HttpRequestMessage> create, Uri showUri, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(8);
        await Task.WhenAll(items.OfType<JsonObject>().Select(async item =>
        {
            var name = Text(item, "model") ?? Text(item, "name");
            if (name is null)
            {
                return;
            }
            await gate.WaitAsync(ct);
            try
            {
                using var request = create(HttpMethod.Post, showUri);
                request.Content = JsonContent.Create(new { model = name });
                using var response = await http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode || JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) is not JsonObject show)
                {
                    return;
                }
                if (show["capabilities"] is JsonArray caps)
                {
                    item["ollama_capabilities"] = caps.DeepClone();
                }
                if (show["model_info"] is JsonObject info)
                {
                    foreach (var (key, value) in info)
                    {
                        if (key.EndsWith(".context_length", StringComparison.Ordinal) && value is JsonValue v && v.TryGetValue<long>(out var l))
                        {
                            item["context_length"] = l;
                            break;
                        }
                    }
                }
            }
            catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                // Details are optional; the model is still listed.
            }
            finally
            {
                gate.Release();
            }
        }));
    }

    private static string Normalize(string feature) => feature.Trim().ToLowerInvariant() switch
    {
        "image" or "image output" or "image generation" => "image generation",
        "image input" or "images" or "vision" => "vision",
        "tool use" or "tool calling" or "tools" => "tools",
        "reasoning" or "thinking" => "thinking",
        var other => other,
    };

    private static string[] OllamaCapabilities(JsonObject item) =>
        item["ollama_capabilities"] is JsonArray a
            ? [.. a.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>()]
            : [];

    private static DiscoveredModel? Map(JsonObject item, HashSet<string> existing)
    {
        var id = Text(item, "id") ?? Text(item, "model") ?? Text(item, "name");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var ollamaCaps = OllamaCapabilities(item);
        var kind = TranscriptionModel().IsMatch(id) ? ModelKind.Transcription
            : SpeechTranslationModel().IsMatch(id) ? ModelKind.SpeechTranslation
            : RealtimeModel().IsMatch(id) ? ModelKind.Realtime
            : id.Contains("embed", StringComparison.OrdinalIgnoreCase) || ollamaCaps.Contains("embedding") ? ModelKind.Embedding
            : ModelKind.Chat;
        var context = Number(item, "context_length") ?? Number(item, "max_input_tokens") ?? Number(item, "context_window");
        return new DiscoveredModel(
            id,
            Text(item, "display_name") ?? id,
            kind,
            ReasoningModel().IsMatch(id) ? ParameterProfile.OpenAIReasoning : ParameterProfile.Standard,
            context is > 0 and <= int.MaxValue ? (int)context : null,
            Features(item),
            Price(item),
            existing.Contains(id));
    }

    private static string? Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? Number(JsonObject o, string key) => o[key] switch
    {
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<string>(out var s) && long.TryParse(s, CultureInfo.InvariantCulture, out var l) => l,
        _ => null,
    };

    /// <summary>Anthropic-style <c>capabilities</c> flags and OpenRouter-style <c>architecture.input_modalities</c>.</summary>
    private static string[] Features(JsonObject item)
    {
        var features = new List<string>(OllamaCapabilities(item).Where(c => c is not "completion"));
        if (item["capabilities"] is JsonObject caps)
        {
            foreach (var (name, value) in caps)
            {
                if (value is JsonObject flag && flag["supported"] is JsonValue s && s.TryGetValue<bool>(out var on) && on)
                {
                    features.Add(name.Replace('_', ' '));
                }
            }
        }
        if (item["architecture"]?["input_modalities"] is JsonArray modalities)
        {
            features.AddRange(modalities.OfType<JsonValue>()
                .Select(m => m.TryGetValue<string>(out var s) ? s : null)
                .Where(m => m is not null and not "text")
                .Select(m => $"{m} input"));
        }
        return [.. features.Select(Normalize).Where(f => f is not ("completion" or "insert")).Distinct().Order()];
    }

    /// <summary>OpenRouter-style pricing is USD per token as strings; convert to USD per million tokens.</summary>
    private static DiscoveredPrice? Price(JsonObject item)
    {
        if (item["pricing"] is not JsonObject pricing)
        {
            return null;
        }
        decimal? PerMillion(string key) =>
            pricing[key] is JsonValue v && v.TryGetValue<string>(out var s) && decimal.TryParse(s, CultureInfo.InvariantCulture, out var d)
                ? Math.Round(d * 1_000_000m, 4)
                : null;
        var input = PerMillion("prompt");
        var output = PerMillion("completion");
        return input is null || output is null
            ? null
            : new DiscoveredPrice(input.Value, PerMillion("input_cache_read") ?? input.Value, output.Value);
    }
}
