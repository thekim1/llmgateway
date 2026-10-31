using System.ComponentModel.DataAnnotations;

namespace Ume.LlmGateway.Infrastructure.Security;

public sealed class GatewaySecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// Server-side secret mixed into virtual-key hashes (HMAC). Must be identical for gateway and admin API,
    /// at least 32 characters, and never committed to source control. Changing it invalidates all keys.
    /// </summary>
    [Required]
    [MinLength(32)]
    public string KeyPepper { get; set; } = string.Empty;
}
