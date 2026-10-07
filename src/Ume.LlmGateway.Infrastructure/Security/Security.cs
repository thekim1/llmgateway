using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Ume.LlmGateway.Domain.Services;

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

public static class KeyHasherFactory
{
    public static VirtualKeyHasher Create(GatewaySecurityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new VirtualKeyHasher(Encoding.UTF8.GetBytes(options.KeyPepper));
    }
}

/// <summary>Encrypts provider credentials at rest using ASP.NET Core Data Protection (key ring in Postgres).</summary>
public sealed class CredentialProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Ume.LlmGateway.ProviderCredential.v1");

    public string? Protect(string? plaintext) =>
        string.IsNullOrEmpty(plaintext) ? null : _protector.Protect(plaintext);

    public string? Unprotect(string? ciphertext) =>
        string.IsNullOrEmpty(ciphertext) ? null : _protector.Unprotect(ciphertext);
}
