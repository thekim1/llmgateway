using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ume.LlmGateway.Domain;

namespace Ume.LlmGateway.Infrastructure.Mcp;

/// <summary>
/// How the gateway authenticates to one MCP server, from its decrypted <see cref="Domain.Entities.McpServer.EncryptedCredential"/>.
/// Holds secrets: never logged or serialised (no record types, so nothing prints them).
/// </summary>
public abstract class McpUpstreamAuth
{
    private protected McpUpstreamAuth(string fingerprint) => Fingerprint = fingerprint;

    public static McpUpstreamAuth None { get; } = new NoAuth();

    /// <summary>Hash of the credential, so a pooled client is replaced when the credential changes.</summary>
    public string Fingerprint { get; }

    /// <summary>
    /// <see cref="McpAuthType.StaticHeaders"/>: a JSON object of header names and string values.
    /// <see cref="McpAuthType.OAuthClientCredentials"/>: <c>{"tokenUrl","clientId","clientSecret","scope"}</c>.
    /// Throws <see cref="FormatException"/> when the credential does not have that shape.
    /// </summary>
    public static McpUpstreamAuth Parse(McpAuthType type, string? plaintext)
    {
        if (type == McpAuthType.None)
        {
            return None;
        }

        if (string.IsNullOrWhiteSpace(plaintext))
        {
            throw new FormatException("The MCP server has no credential.");
        }

        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(type + "\n" + plaintext)));
        try
        {
            using var document = JsonDocument.Parse(plaintext);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("The MCP server credential is not a JSON object.");
            }

            if (type == McpAuthType.StaticHeaders)
            {
                var headers = new List<KeyValuePair<string, string>>();
                foreach (var property in root.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        throw new FormatException("Every static MCP header value must be a string.");
                    }

                    headers.Add(new(property.Name, property.Value.GetString()!));
                }

                return new StaticHeaders(headers, fingerprint);
            }

            return new ClientCredentials(
                new Uri(Required(root, "tokenUrl"), UriKind.Absolute), Required(root, "clientId"), Required(root, "clientSecret"),
                root.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.String ? scope.GetString() : null, fingerprint);
        }
        catch (JsonException)
        {
            throw new FormatException("The MCP server credential is not valid JSON.");
        }
        catch (UriFormatException)
        {
            throw new FormatException("The MCP server's token URL is not an absolute URL.");
        }
    }

    private static string Required(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : throw new FormatException($"The MCP server credential has no '{name}'.");

    public override string ToString() => GetType().Name;

    private sealed class NoAuth() : McpUpstreamAuth(string.Empty);

    public sealed class StaticHeaders : McpUpstreamAuth
    {
        internal StaticHeaders(IReadOnlyList<KeyValuePair<string, string>> headers, string fingerprint) : base(fingerprint) => Headers = headers;

        public IReadOnlyList<KeyValuePair<string, string>> Headers { get; }
    }

    public sealed class ClientCredentials : McpUpstreamAuth
    {
        internal ClientCredentials(Uri tokenUrl, string clientId, string clientSecret, string? scope, string fingerprint) : base(fingerprint)
        {
            TokenUrl = tokenUrl;
            ClientId = clientId;
            ClientSecret = clientSecret;
            Scope = scope;
        }

        public Uri TokenUrl { get; }
        public string ClientId { get; }
        public string ClientSecret { get; }
        public string? Scope { get; }
    }
}
