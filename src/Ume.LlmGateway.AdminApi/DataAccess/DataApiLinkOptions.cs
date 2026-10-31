using System.ComponentModel.DataAnnotations;

namespace Ume.LlmGateway.AdminApi.DataAccess;

/// <summary>Bound from the <c>DataApi</c> configuration section of the admin API: where integrations reach the Data API.</summary>
public sealed class DataApiLinkOptions : IValidatableObject
{
    public const string SectionName = "DataApi";

    /// <summary>The Data API's public address, shown with a new Data API key. Empty when the Data API is not deployed.</summary>
    public string? PublicUrl { get; set; }

    public string? Address => string.IsNullOrWhiteSpace(PublicUrl) ? null : PublicUrl.TrimEnd('/');

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Address is { } value && (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")))
        {
            yield return new ValidationResult("DataApi:PublicUrl must be an absolute http(s) URL.", [nameof(PublicUrl)]);
        }
    }
}
