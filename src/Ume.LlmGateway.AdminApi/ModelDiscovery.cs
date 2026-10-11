using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Providers;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.AdminApi;

public sealed record DiscoveredPrice(
    decimal InputPerMillionUsd, decimal CachedInputPerMillionUsd, decimal OutputPerMillionUsd,
    decimal AudioInputPerMillionUsd = 0, decimal AudioOutputPerMillionUsd = 0);

public sealed record DiscoveredModel(
    string Id,
    string DisplayName,
    ModelKind Kind,
    ParameterProfile ParameterProfile,
    int? ContextWindow,
    string[] Features,
    DiscoveredPrice? Price,
    bool AlreadyAdded,
    string? PriceSource = null);

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

    [GeneratedRegex(@"^(o\d|gpt-[5-9])", RegexOptions.IgnoreCase)]
    private static partial Regex ReasoningModel();

    /// <summary>
    /// The provider's models. With <paramref name="requireHttps"/> (production) only an https base URL is called, the same
    /// rule the gateway applies to provider calls. Foundry deployments get a price from <paramref name="azurePrices"/>, the
    /// Azure list prices, when it is given and the list is unambiguous.
    /// </summary>
    public static async Task<DiscoveredModel[]> DiscoverAsync(
        ProviderAccount provider, HttpClient http, CredentialProtector protector, bool requireHttps, CancellationToken ct,
        Func<CancellationToken, Task<IReadOnlyList<AzurePriceMeter>>>? azurePrices = null)
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
        // Foundry's /openai/v1/models lists every model the resource could deploy; the project API lists the deployments.
        // Claude on Foundry is an Anthropic provider (…services.ai.azure.com/anthropic/v1) listed from the same project API.
        var foundry = provider.Type == ProviderType.AzureAIFoundry || (anthropic && FoundryEndpoint.IsFoundryHost(provider.BaseUrl));
        // Ollama's OpenAI-compatible base URL ends in /v1; model listing lives on the native /api.
        var baseUrl = ollama ? WithoutVersionSegment(provider.BaseUrl) : provider.BaseUrl;
        var listUri = foundry ? FoundryDeployments(baseUrl) : Build(baseUrl, ollama ? "api/tags" : "models");
        if (anthropic && !foundry)
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
            if (foundry && provider.AuthMode == ProviderAuthMode.XApiKeyHeader)
            {
                // Claude on Foundry takes x-api-key or api-key; the project API only api-key.
                request.Headers.TryAddWithoutValidation("api-key", credential);
                return request;
            }
            ProviderAuth.Apply(request, provider, credential);
            return request;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        async Task<JsonNode?> GetAsync(Uri uri)
        {
            using var request = CreateRequest(HttpMethod.Get, uri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var hint = ((int)response.StatusCode, foundry) switch
                {
                    (401 or 403, true) => "Kontrollera att API-nyckeln hör till Foundry-resursen och att den tillåter projektets API.",
                    (401 or 403, false) => "Kontrollera API-nyckeln.",
                    (404, true) => "Ange Foundry-resursens adress (https://<resurs>.services.ai.azure.com/openai/v1) eller projektets adress.",
                    _ => "Kontrollera adressen och inloggningsmetoden.",
                };
                throw new ApiFaultException(502, $"Leverantören svarade {(int)response.StatusCode}. {hint}");
            }
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        }

        JsonNode? body;
        try
        {
            body = await GetAsync(listUri);
            if (foundry)
            {
                body = await FoundryAllPagesAsync(body, GetAsync, listUri);
            }
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
            JsonObject obj => (obj["data"] ?? obj["models"] ?? obj["value"]) as JsonArray,
            _ => null,
        } ?? throw new ApiFaultException(502, "Leverantören returnerade ingen modellista.");

        if (ollama)
        {
            await EnrichOllamaAsync(items, http, CreateRequest, Build(baseUrl, "api/show"), timeout.Token);
        }

        var existing = provider.Deployments.Select(d => d.UpstreamModel).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<AzurePriceMeter> meters = foundry && azurePrices is not null ? await azurePrices(ct) : [];
        return [.. items.OfType<JsonObject>()
            .Where(item => !foundry || IsAnthropic(item) == anthropic)
            .Select(item => foundry ? MapDeployment(item, existing, meters) : Map(item, existing))
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

    private static Uri FoundryDeployments(string baseUrl)
    {
        try
        {
            return FoundryEndpoint.DeploymentsUri(baseUrl);
        }
        catch (UriFormatException)
        {
            throw new ApiFaultException(400, "Leverantörens adress är ogiltig.");
        }
    }

    /// <summary>
    /// The project API pages its deployments (<c>value</c>, <c>nextLink</c>); returns them as one <c>value</c> array.
    /// Only links on the same host are followed, so the credential never leaves the resource.
    /// </summary>
    private static async Task<JsonNode?> FoundryAllPagesAsync(JsonNode? first, Func<Uri, Task<JsonNode?>> get, Uri listUri)
    {
        const int MaxPages = 20;
        var page = first;
        var all = new JsonArray();
        for (var i = 0; i < MaxPages && page is JsonObject obj && obj["value"] is JsonArray value; i++)
        {
            foreach (var item in value.ToArray())
            {
                value.Remove(item);
                all.Add(item);
            }
            if (Text(obj, "nextLink") is not { Length: > 0 } next || !Uri.TryCreate(listUri, next, out var nextUri) ||
                !string.Equals(nextUri.Host, listUri.Host, StringComparison.OrdinalIgnoreCase) || nextUri.Scheme != listUri.Scheme)
            {
                break;
            }
            page = await get(nextUri);
        }
        return first is JsonObject ? new JsonObject { ["value"] = all } : first;
    }

    /// <summary>Claude deployments are served by Foundry's Anthropic API, every other model by its OpenAI v1 API.</summary>
    private static bool IsAnthropic(JsonObject deployment) =>
        string.Equals(Text(deployment, "modelPublisher"), "Anthropic", StringComparison.OrdinalIgnoreCase) ||
        (Text(deployment, "modelName") ?? string.Empty).StartsWith("claude", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A Foundry deployment: requests name the deployment, so it is the model id; the model behind it decides the kind,
    /// and its model, version and SKU find the Azure list price.
    /// </summary>
    private static DiscoveredModel? MapDeployment(JsonObject item, HashSet<string> existing, IReadOnlyList<AzurePriceMeter> meters)
    {
        var name = Text(item, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var model = Text(item, "modelName") ?? name;
        var capabilities = item["capabilities"] is JsonObject caps
            ? caps.Where(c => c.Value is JsonValue v && (v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<string>(out var s) && bool.TryParse(s, out var p) && p))
                .Select(c => c.Key.Replace('_', ' ').ToLowerInvariant())
                .ToArray()
            : [];
        var mapped = Map(new JsonObject { ["id"] = model }, existing)!;
        var embedding = mapped.Kind == ModelKind.Embedding || capabilities.Any(c => c.Contains("embed", StringComparison.Ordinal));
        var version = Text(item, "modelVersion");
        var suggestion = AzurePriceMatcher.Suggest(meters, model, version, item["sku"] is JsonObject sku ? Text(sku, "name") : null);
        var display = string.Equals(name, model, StringComparison.OrdinalIgnoreCase) ? name : $"{name} ({model})";
        return mapped with
        {
            Id = name,
            DisplayName = version is { Length: > 0 } ? $"{display}, version {version}" : display,
            Kind = embedding && mapped.Kind == ModelKind.Chat ? ModelKind.Embedding : mapped.Kind,
            Features = [.. capabilities.Where(c => !c.Contains("completion", StringComparison.Ordinal) && !c.Contains("embed", StringComparison.Ordinal))
                .Select(Normalize).Distinct().Order()],
            AlreadyAdded = existing.Contains(name),
            Price = suggestion?.Price,
            PriceSource = suggestion is { } s ? $"Azure list price: {string.Join(", ", s.Meters)}" : null,
        };
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
