using System.Text.Json;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.AdminApi.Catalog.Pricing;

/// <summary>
/// A list price in USD per million tokens. Null: the list does not give it, so the model's own value is kept.
/// </summary>
public sealed record ListPrice(
    decimal Input, decimal Output, decimal? CacheRead = null, decimal? CacheWrite = null, decimal? AudioInput = null, decimal? AudioOutput = null);

/// <summary>
/// A model in the models.dev list: <c>provider/model</c>, its price (null when the list has none) and what it says about the
/// model. <paramref name="Features"/> are the gateway's capability tags (<c>tools</c>, <c>thinking</c>, <c>vision</c>, <c>audio</c>,
/// <c>image generation</c>).
/// </summary>
public sealed record ListModel(string Key, ListPrice? Price, string? Description = null, int? ContextWindow = null, string[]? Features = null)
{
    public string[] Features { get; init; } = Features ?? [];
}

/// <summary>
/// Whose price a model has: the list's (<c>List</c>), an admin's because the list does not have the model (<c>Unlisted</c>), or
/// an admin's by choice or because the provider's prices do not follow the list (<c>Manual</c>; Azure deployments, whose price
/// depends on region and deployment type, get the list price only when they have none).
/// </summary>
public enum PriceMode
{
    List,
    Unlisted,
    Manual,
}

/// <summary>The models.dev list, by models.dev provider id and model id (both compared case-insensitively).</summary>
public sealed class ModelList(IReadOnlyDictionary<string, IReadOnlyDictionary<string, ListModel>> providers)
{
    public static readonly ModelList Empty = new(new Dictionary<string, IReadOnlyDictionary<string, ListModel>>());

    /// <summary>The models.dev providers the gateway's provider types map to (see <see cref="ProviderId"/>); the rest is not kept.</summary>
    public static readonly string[] Providers = ["anthropic", "azure", "ollama-cloud", "openai", "openrouter"];

    public int Count => providers.Values.Sum(p => p.Count);

    /// <summary>
    /// The models.dev provider a provider's models are looked up in: Anthropic (Claude on Foundry is <c>azure</c>), Azure OpenAI
    /// and Azure AI Foundry (<c>azure</c>), Ollama Cloud, OpenAI and OpenRouter (by their API host). Local Ollama and other
    /// servers are not in the list.
    /// </summary>
    public static string? ProviderId(ProviderAccount provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var host = Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : string.Empty;
        return provider.Type switch
        {
            ProviderType.Anthropic => FoundryEndpoint.IsFoundryHost(provider.BaseUrl) ? "azure" : "anthropic",
            ProviderType.AzureAIFoundry or ProviderType.AzureOpenAI => "azure",
            ProviderType.OllamaCloud => "ollama-cloud",
            ProviderType.Ollama when host is "ollama.com" or "www.ollama.com" => "ollama-cloud",
            ProviderType.OpenAI or ProviderType.OpenAICompatible when host == "api.openai.com" => "openai",
            ProviderType.OpenAI or ProviderType.OpenAICompatible when host == "openrouter.ai" => "openrouter",
            _ => null,
        };
    }

    /// <summary>The model; Ollama's <c>:latest</c> and <c>-cloud</c> suffixes are tried without.</summary>
    public ListModel? Find(string providerId, string model)
    {
        if (!providers.TryGetValue(providerId, out var models))
        {
            return null;
        }

        model = model.Trim();
        foreach (var candidate in new[] { model, Without(model, ":latest"), Without(model, "-cloud") })
        {
            if (models.TryGetValue(candidate, out var found))
            {
                return found;
            }
        }
        return null;
    }

    public ListModel? Find(ProviderAccount provider, string model) =>
        ProviderId(provider) is { } id ? Find(id, model) : null;

    /// <summary>The model by its own name (<see cref="ModelDeployment.ModelName"/>: the base model of an Azure deployment).</summary>
    public ListModel? Find(ModelDeployment model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.ProviderAccount is { } provider ? Find(provider, model.ModelName) : null;
    }

    /// <summary>
    /// Whether the model's price follows the list. Azure deployments are priced by region and deployment type, which the list
    /// does not know, so they keep Azure's list price (suggested at discovery) and get the list price only when they have
    /// none. Claude on Foundry is billed at Anthropic's price, which the list has.
    /// </summary>
    public static bool FollowsPrices(ProviderAccount provider, ModelDeployment model)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider.Type is not (ProviderType.AzureAIFoundry or ProviderType.AzureOpenAI) || FoundryEndpoint.IsClaude(provider, model);
    }

    /// <summary>
    /// Whose price <paramref name="model"/> has. <paramref name="updatesOn"/>: the daily update runs at all (on and a list
    /// set). The model follows the list when it, its provider and the update are on, its provider's prices follow the list
    /// (<see cref="FollowsPrices"/>) and the list prices it.
    /// </summary>
    public PriceMode Mode(ModelDeployment model, bool updatesOn)
    {
        ArgumentNullException.ThrowIfNull(model);
        return !updatesOn || !model.AutoUpdatePrice || model.ProviderAccount is not { AutoUpdatePrices: true } provider || !FollowsPrices(provider, model)
            ? PriceMode.Manual
            : Find(model)?.Price is not null ? PriceMode.List
            : PriceMode.Unlisted;
    }

    /// <summary>
    /// Whether the daily update gives <paramref name="model"/> the list price although it does not follow the list: an Azure
    /// deployment with automatic prices on and no price at all.
    /// </summary>
    public bool FillsMissingPrice(ModelDeployment model, bool updatesOn)
    {
        ArgumentNullException.ThrowIfNull(model);
        return updatesOn && model.AutoUpdatePrice && model.ProviderAccount is { AutoUpdatePrices: true } provider &&
               !FollowsPrices(provider, model) && model.Prices.Count == 0 && Find(model)?.Price is not null;
    }

    private static string Without(string model, string suffix) =>
        model.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? model[..^suffix.Length] : model;

    /// <summary>
    /// Reads models.dev's <c>api.json</c>: <c>{ provider: { models: { id: { description, tool_call, reasoning, modalities,
    /// limit: { context }, cost: { input, output, cache_read, cache_write, input_audio, output_audio } } } } }</c>, prices in USD
    /// per million tokens. Context tiers are ignored (the gateway has one price per model), and a price of 0 for both input
    /// and output is taken as unknown, never as free.
    /// </summary>
    public static ModelList Parse(JsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The model list is not a JSON object.");
        }

        var result = new Dictionary<string, IReadOnlyDictionary<string, ListModel>>(StringComparer.OrdinalIgnoreCase);
        foreach (var providerId in Providers)
        {
            if (!document.RootElement.TryGetProperty(providerId, out var provider) || provider.ValueKind != JsonValueKind.Object ||
                !provider.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var entries = new Dictionary<string, ListModel>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in models.EnumerateObject().Where(m => m.Value.ValueKind == JsonValueKind.Object))
            {
                entries.TryAdd(model.Name, Read($"{providerId}/{model.Name}", model.Value));
            }
            result[providerId] = entries;
        }
        return new ModelList(result);
    }

    private static ListModel Read(string key, JsonElement model)
    {
        var description = Text(model, "description") is { Length: > 0 } d ? (d.Length > 1000 ? d[..1000] : d) : null;
        var context = model.TryGetProperty("limit", out var limit) && limit.ValueKind == JsonValueKind.Object &&
                      limit.TryGetProperty("context", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var tokens) && tokens > 0
            ? tokens
            : (int?)null;
        var input = Modalities(model, "input");
        var output = Modalities(model, "output");
        string[] features =
        [
            .. Flag(model, "tool_call") ? ["tools"] : Array.Empty<string>(),
            .. Flag(model, "reasoning") ? ["thinking"] : Array.Empty<string>(),
            .. input.Contains("image") ? ["vision"] : Array.Empty<string>(),
            .. input.Contains("audio") ? ["audio"] : Array.Empty<string>(),
            .. output.Contains("image") ? ["image generation"] : Array.Empty<string>(),
        ];
        var price = model.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Object ? Price(cost) : null;
        return new ListModel(key, price, description, context, [.. features.Order()]);
    }

    private static ListPrice? Price(JsonElement cost)
    {
        decimal? Usd(string name) =>
            cost.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) && d is >= 0 and <= 1_000_000
                ? Math.Round(d, 6) // the database keeps six decimals; more would look like a change every day
                : null;

        return Usd("input") is { } input && Usd("output") is { } output && (input > 0 || output > 0)
            ? new ListPrice(input, output, Usd("cache_read"), Usd("cache_write"), Usd("input_audio"), Usd("output_audio"))
            : null;
    }

    private static string? Text(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null;

    private static bool Flag(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static HashSet<string> Modalities(JsonElement model, string direction) =>
        model.TryGetProperty("modalities", out var m) && m.ValueKind == JsonValueKind.Object &&
        m.TryGetProperty(direction, out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
            : [];

    /// <summary>
    /// The price <paramref name="current"/> becomes at list price <paramref name="list"/>, or null when it already is. What
    /// the list does not give (audio duration, per-image price, and any token price it leaves out) is kept.
    /// </summary>
    public static ModelPrice? Changed(ModelPrice? current, ListPrice list)
    {
        ArgumentNullException.ThrowIfNull(list);
        var next = new ModelPrice
        {
            InputPerMillionUsd = list.Input,
            OutputPerMillionUsd = list.Output,
            // Without a cache price, a cached token that cost the input price keeps doing so.
            CachedInputPerMillionUsd = list.CacheRead
                ?? (current is null || current.CachedInputPerMillionUsd == current.InputPerMillionUsd ? list.Input : current.CachedInputPerMillionUsd),
            CacheWriteInputPerMillionUsd = list.CacheWrite ?? current?.CacheWriteInputPerMillionUsd,
            AudioInputPerMillionUsd = list.AudioInput ?? current?.AudioInputPerMillionUsd ?? 0,
            AudioOutputPerMillionUsd = list.AudioOutput ?? current?.AudioOutputPerMillionUsd ?? 0,
            AudioPerMinuteUsd = current?.AudioPerMinuteUsd ?? 0,
            PerImageUsd = current?.PerImageUsd ?? 0,
        };
        return current is not null &&
               current.InputPerMillionUsd == next.InputPerMillionUsd && current.OutputPerMillionUsd == next.OutputPerMillionUsd &&
               current.CachedInputPerMillionUsd == next.CachedInputPerMillionUsd &&
               current.CacheWriteInputPerMillionUsd == next.CacheWriteInputPerMillionUsd &&
               current.AudioInputPerMillionUsd == next.AudioInputPerMillionUsd && current.AudioOutputPerMillionUsd == next.AudioOutputPerMillionUsd
            ? null
            : next;
    }

    /// <summary>
    /// Fills what <paramref name="model"/> leaves empty (description, capability tags, context window) from the list; never
    /// overwrites. Returns whether anything was filled.
    /// </summary>
    public static bool Enrich(ModelDeployment model, ListModel listed)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(listed);
        var changed = false;
        if (string.IsNullOrWhiteSpace(model.Description) && listed.Description is { } description)
        {
            model.Description = description;
            changed = true;
        }
        if (model.Features.Length == 0 && listed.Features.Length > 0)
        {
            model.Features = listed.Features;
            changed = true;
        }
        if (model.ContextWindow is null && listed.ContextWindow is { } context)
        {
            model.ContextWindow = context;
            changed = true;
        }
        return changed;
    }
}
