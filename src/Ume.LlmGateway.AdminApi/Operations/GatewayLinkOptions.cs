using System.ComponentModel.DataAnnotations;

namespace Ume.LlmGateway.AdminApi.Operations;

/// <summary>Bound from the <c>Gateway</c> configuration section: where the admin API finds the gateway.</summary>
public sealed class GatewayLinkOptions : IValidatableObject
{
    public const string SectionName = "Gateway";

    /// <summary>The gateway's local development address, used when <see cref="BaseUrl"/> is not set.</summary>
    public const string DevelopmentBaseUrl = "https://localhost:5140";

    /// <summary>The gateway's public address, shown to key holders in the catalogue.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>The address the admin API reads the gateway's operations status from (internal, HTTPS); defaults to <see cref="BaseUrl"/>.</summary>
    public string? OperationsUrl { get; set; }

    public string PublicBaseUrl => BaseUrl ?? DevelopmentBaseUrl;

    /// <summary><c>null</c> when neither address is set: the operations status is then reported as unavailable.</summary>
    public string? OperationsAddress => OperationsUrl ?? BaseUrl;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var (name, value) in new[] { (nameof(BaseUrl), BaseUrl), (nameof(OperationsUrl), OperationsUrl) })
        {
            if (value is not null && (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")))
            {
                yield return new ValidationResult($"Gateway:{name} must be an absolute http(s) URL.", [name]);
            }
        }
    }
}
