using System.ComponentModel.DataAnnotations;

namespace Ume.LlmGateway.AdminApi;

/// <summary>Bound from the <c>Admin</c> configuration section.</summary>
public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    /// <summary>Requests per minute per signed-in user (or client address before sign-in).</summary>
    [Range(1, 10_000)]
    public int RequestsPerMinute { get; set; } = 120;

    /// <summary>The Azure region (<c>armRegionName</c>) whose list prices are suggested for Azure AI Foundry deployments.</summary>
    [RegularExpression("^[a-z0-9]{2,40}$")]
    public string AzurePriceRegion { get; set; } = "swedencentral";
}

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

/// <summary>
/// Bound from the <c>Oidc</c> configuration section. Defaults match the bundled Keycloak realm; other
/// identity providers (Keycloak with different mappers, AD FS, Entra ID) can remap claims or map groups to roles.
/// </summary>
public sealed class OidcOptions
{
    public const string SectionName = "Oidc";

    /// <summary>The identity provider's issuer URL.</summary>
    [Required]
    public string? Authority { get; set; }

    [Required]
    public string ClientId { get; set; } = "ume-admin";

    public string? ClientSecret { get; set; }

    public string RoleClaim { get; set; } = "roles";
    public string DepartmentClaim { get; set; } = "departmentCodes";
    public string GroupClaim { get; set; } = "groups";
    /// <summary>Role name (gateway-admin, department-admin, viewer) to the group names that grant it, separated by ';'.</summary>
    public Dictionary<string, string> RoleGroups { get; set; } = [];
    /// <summary>Extra scopes to request, for example "groups".</summary>
    public string[] ExtraScopes { get; set; } = [];
}
