using Microsoft.AspNetCore.DataProtection;

namespace Ume.LlmGateway.Infrastructure.Security;

/// <summary>Encrypts provider credentials at rest using ASP.NET Core Data Protection (key ring in Postgres).</summary>
public sealed class CredentialProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Ume.LlmGateway.ProviderCredential.v1");

    public string? Protect(string? plaintext) =>
        string.IsNullOrEmpty(plaintext) ? null : _protector.Protect(plaintext);

    public string? Unprotect(string? ciphertext) =>
        string.IsNullOrEmpty(ciphertext) ? null : _protector.Unprotect(ciphertext);
}
