using System.Collections.Concurrent;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>
/// Decrypted provider credentials of one <see cref="CatalogSnapshot"/>, so Data Protection runs once per provider and
/// snapshot instead of on every attempt. A new snapshot (credential edited, catalogue refreshed) starts empty, and the
/// old one's plaintext goes with it. Never logged, serialised or exposed beyond the provider call.
/// </summary>
internal sealed class ProviderCredentials
{
    private readonly ConcurrentDictionary<Guid, string?> _plaintext = new();

    /// <summary>The provider's credential, decrypting it on first use. Throws <see cref="System.Security.Cryptography.CryptographicException"/> (not cached) when it cannot be decrypted.</summary>
    public string? Get(ProviderAccount provider, CredentialProtector protector)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(protector);
        return _plaintext.TryGetValue(provider.Id, out var cached)
            ? cached
            : _plaintext.GetOrAdd(provider.Id, protector.Unprotect(provider.EncryptedCredential));
    }
}
