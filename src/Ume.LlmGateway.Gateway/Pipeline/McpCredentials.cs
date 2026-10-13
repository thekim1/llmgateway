using System.Collections.Concurrent;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Mcp;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>
/// Decrypted MCP server credentials of one <see cref="CatalogSnapshot"/>, like <see cref="ProviderCredentials"/>: Data
/// Protection runs once per server and snapshot, and a new snapshot starts empty. Never logged or exposed.
/// </summary>
internal sealed class McpCredentials
{
    private readonly ConcurrentDictionary<Guid, McpUpstreamAuth> _auth = new();

    /// <summary>
    /// The server's credential, decrypting it on first use. Throws <see cref="System.Security.Cryptography.CryptographicException"/>
    /// or <see cref="FormatException"/> (not cached) when it cannot be decrypted or has the wrong shape.
    /// </summary>
    public McpUpstreamAuth Get(McpServer server, CredentialProtector protector)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(protector);
        if (server.AuthType == McpAuthType.None)
        {
            return McpUpstreamAuth.None;
        }

        return _auth.TryGetValue(server.Id, out var cached)
            ? cached
            : _auth.GetOrAdd(server.Id, McpUpstreamAuth.Parse(server.AuthType, protector.Unprotect(server.EncryptedCredential)));
    }
}
