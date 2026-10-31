using System.Text.RegularExpressions;

namespace Ume.LlmGateway.AdminApi.Catalog.Pricing;

/// <summary>A pay-as-you-go token meter from the Azure retail price list, read from its free-text meter name.</summary>
public sealed record AzurePriceMeter(
    string MeterName, string Product, string ModelKey, string? Version, AzureDeploymentType? DeploymentType,
    AzureMeterRole Role, decimal PerMillionUsd);

public enum AzureDeploymentType { Global, DataZone, Regional }

public enum AzureMeterRole { Input, CachedInput, Output, AudioInput, AudioOutput }

/// <summary>
/// Suggests Foundry deployment prices from the Azure retail price list. The list has no model ids, only meter names
/// such as <c>gpt 4.1 cached Inp glbl Tokens</c> or <c>GPT 5 Mini Inpt DZone 1M Tokens</c>, so a price is suggested only
/// when every meter of the deployment's model and deployment type agrees; anything unclear is left for the admin.
/// </summary>
public static partial class AzurePriceMatcher
{
    private static readonly HashSet<string> InputWords = ["inp", "inpt", "input"];
    private static readonly HashSet<string> OutputWords = ["outp", "outpt", "out", "output", "opt"];
    private static readonly HashSet<string> CachedWords = ["cached", "cchd", "cd"];
    private static readonly HashSet<string> AudioWords = ["aud", "audio"];
    private static readonly HashSet<string> TextWords = ["txt", "text"];
    private static readonly HashSet<string> GlobalWords = ["glbl", "gl", "global"];
    private static readonly HashSet<string> DataZoneWords = ["dz", "dzone", "datazone"];
    private static readonly HashSet<string> RegionalWords = ["regnl", "rgnl", "regional", "regn"];
    private static readonly HashSet<string> NoiseWords = ["tokens", "1m", "1k", "std", "shortco", "new"];

    /// <summary>Batch, fine-tuning, provisioned, priority, flex, long-context, cache-write and image meters price something else.</summary>
    private static readonly HashSet<string> OtherBillingWords =
        ["batch", "ft", "finetuned", "fine", "tuned", "hosting", "training", "grader", "rft", "provisioned", "pp", "priority", "flex", "wr", "longco", "image", "img"];

    [GeneratedRegex(@"[\s\-_]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"^\d{4}$")]
    private static partial Regex MonthDay();

    [GeneratedRegex(@"^\d{4}-(\d{2})-(\d{2})$")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"[^a-z0-9.]")]
    private static partial Regex NotKeyCharacter();

    /// <summary>The meter, or <c>null</c> when it is not a pay-as-you-go token meter the gateway can price.</summary>
    public static AzurePriceMeter? Parse(string productName, string meterName, string unitOfMeasure, decimal unitPrice, string type)
    {
        ArgumentNullException.ThrowIfNull(meterName);
        var perUnit = unitOfMeasure switch { "1K" => 1000m, "1M" => 1m, _ => 0m };
        if (type != "Consumption" || perUnit == 0 || !meterName.EndsWith("Tokens", StringComparison.Ordinal))
        {
            return null;
        }

        var words = Words(meterName.Replace("Data Zone", "DataZone", StringComparison.OrdinalIgnoreCase));
        if (words.Any(OtherBillingWords.Contains))
        {
            return null;
        }

        bool? output = null;
        bool cached = false, audio = false;
        AzureDeploymentType? deploymentType = null;
        string? version = null;
        var model = new List<string>();
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (InputWords.Contains(w)) { output ??= false; }
            else if (OutputWords.Contains(w)) { output = true; }
            else if (CachedWords.Contains(w)) { cached = true; }
            else if (AudioWords.Contains(w)) { audio = true; }
            else if (TextWords.Contains(w) && i > 0) { } // "text" leads model names (text-embedding-3-large)
            else if (GlobalWords.Contains(w)) { deploymentType = AzureDeploymentType.Global; }
            else if (DataZoneWords.Contains(w)) { deploymentType = AzureDeploymentType.DataZone; }
            else if (RegionalWords.Contains(w)) { deploymentType = AzureDeploymentType.Regional; }
            else if (NoiseWords.Contains(w)) { }
            else if (MonthDay().IsMatch(w)) { version = w; }
            else { model.Add(w == "prvw" ? "preview" : w); }
        }

        // Cached audio has no price of its own in the gateway; it is billed as cached input.
        if (model.Count == 0 || (cached && audio))
        {
            return null;
        }
        var role = (output ?? false, cached, audio) switch
        {
            (_, true, _) => AzureMeterRole.CachedInput,
            (true, _, true) => AzureMeterRole.AudioOutput,
            (true, _, _) => AzureMeterRole.Output,
            (false, _, true) => AzureMeterRole.AudioInput,
            _ => AzureMeterRole.Input, // one-price meters (embeddings) have no direction
        };
        return new AzurePriceMeter(meterName, Key(productName), string.Concat(model), version, deploymentType, role, unitPrice * perUnit);
    }

    /// <summary>
    /// The price of a deployment of <paramref name="modelName"/> (<paramref name="modelVersion"/>, e.g. 2025-04-14) with
    /// the Foundry SKU <paramref name="skuName"/>, and the meters it came from; <c>null</c> unless the list is unambiguous.
    /// Provisioned and batch deployments are billed differently and get no suggestion.
    /// </summary>
    public static (DiscoveredPrice Price, string[] Meters)? Suggest(IReadOnlyList<AzurePriceMeter> meters, string modelName, string? modelVersion, string? skuName)
    {
        ArgumentNullException.ThrowIfNull(meters);
        ArgumentNullException.ThrowIfNull(modelName);
        AzureDeploymentType? deploymentType = skuName switch
        {
            "GlobalStandard" => AzureDeploymentType.Global,
            "DataZoneStandard" => AzureDeploymentType.DataZone,
            "Standard" => AzureDeploymentType.Regional,
            _ => null,
        };
        if (deploymentType is null)
        {
            return null;
        }

        var key = string.Concat(Words(modelName).Where(w => w != "instruct"));
        var date = modelVersion is null ? null : IsoDate().Match(modelVersion);
        var monthDay = date is { Success: true } ? date.Groups[1].Value + date.Groups[2].Value : null;
        var candidates = meters.Where(m =>
                (m.DeploymentType is null || m.DeploymentType == deploymentType) &&
                (m.Version is null || m.Version == monthDay) &&
                key.EndsWith(m.ModelKey, StringComparison.Ordinal) &&
                // "5 mini" under the product "Azure OpenAI GPT5" is gpt-5-mini; "4.1" under "Azure OpenAI" is not gpt-4.1.
                (key.Length == m.ModelKey.Length || m.Product.Contains(key[..^m.ModelKey.Length], StringComparison.Ordinal)))
            .ToList();
        if (candidates.Any(m => m.Version is not null))
        {
            candidates.RemoveAll(m => m.Version is null); // a dated meter is the deployed version's price
        }

        var byRole = candidates.GroupBy(m => m.Role).ToDictionary(g => g.Key, g => g.Select(m => m.PerMillionUsd).Distinct().ToList());
        if (byRole.Values.Any(prices => prices.Count > 1) || !byRole.TryGetValue(AzureMeterRole.Input, out var input))
        {
            return null;
        }
        decimal Price(AzureMeterRole role, decimal fallback) => byRole.TryGetValue(role, out var p) ? p[0] : fallback;
        var price = new DiscoveredPrice(input[0], Price(AzureMeterRole.CachedInput, input[0]), Price(AzureMeterRole.Output, 0),
            Price(AzureMeterRole.AudioInput, 0), Price(AzureMeterRole.AudioOutput, 0));
        return (price, [.. candidates.Select(m => m.MeterName).Order(StringComparer.Ordinal)]);
    }

    private static string[] Words(string text) => Separators().Split(text.ToLowerInvariant()).Where(w => w.Length > 0).ToArray();

    private static string Key(string text) => NotKeyCharacter().Replace(text.ToLowerInvariant(), string.Empty);
}
