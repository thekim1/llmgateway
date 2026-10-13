using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Ume.LlmGateway.Infrastructure.Mcp;

/// <summary>
/// OAuth 2.0 client-credentials tokens for MCP servers, per server and credential, cached on this instance until shortly
/// before they expire. Only one fetch per server runs at a time; the others wait for its token.
/// </summary>
public sealed class McpTokenCache(IHttpClientFactory httpClients, TimeProvider time)
{
    public const string HttpClientName = "mcp-oauth";

    /// <summary>A token is renewed this long before the expiry its issuer gave.</summary>
    private static readonly TimeSpan RenewBefore = TimeSpan.FromSeconds(60);

    /// <summary>Used when the token response has no <c>expires_in</c>.</summary>
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public async Task<string> GetAsync(Guid serverId, McpUpstreamAuth.ClientCredentials credentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var entry = _entries.AddOrUpdate(serverId,
            _ => new Entry(credentials.Fingerprint),
            (_, existing) => existing.Fingerprint == credentials.Fingerprint ? existing : new Entry(credentials.Fingerprint));
        if (entry.Valid(time.GetUtcNow()) is { } cached)
        {
            return cached;
        }

        await entry.Lock.WaitAsync(cancellationToken);
        try
        {
            if (entry.Valid(time.GetUtcNow()) is { } fetched)
            {
                return fetched;
            }

            var (token, lifetime) = await FetchTokenAsync(credentials, cancellationToken);
            entry.Set(token, time.GetUtcNow() + lifetime - RenewBefore);
            return token;
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    /// <summary>Forgets the server's token, for example after the server refused it.</summary>
    public void Invalidate(Guid serverId)
    {
        if (_entries.TryGetValue(serverId, out var entry))
        {
            entry.Set(null, DateTimeOffset.MinValue);
        }
    }

    private async Task<(string Token, TimeSpan Lifetime)> FetchTokenAsync(McpUpstreamAuth.ClientCredentials credentials, CancellationToken cancellationToken)
    {
        try
        {
            return await FetchAsync(credentials, cancellationToken);
        }
        catch (McpTokenException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            throw new McpTokenException(null, ex);
        }
    }

    private async Task<(string Token, TimeSpan Lifetime)> FetchAsync(McpUpstreamAuth.ClientCredentials credentials, CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>> { new("grant_type", "client_credentials") };
        if (credentials.Scope is { Length: > 0 } scope)
        {
            form.Add(new("scope", scope));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, credentials.TokenUrl) { Content = new FormUrlEncodedContent(form) };
        // RFC 6749 2.3.1: client id and secret form-encoded, then HTTP Basic.
        var basic = Uri.EscapeDataString(credentials.ClientId) + ":" + Uri.EscapeDataString(credentials.ClientSecret);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(basic)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new McpTokenException(response.StatusCode);
        }

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("access_token", out var token) || token.ValueKind != JsonValueKind.String)
        {
            throw new McpTokenException(response.StatusCode);
        }

        var lifetime = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : DefaultLifetime;
        return (token.GetString()!, lifetime < RenewBefore * 2 ? RenewBefore * 2 : lifetime);
    }

    private sealed class Entry(string fingerprint)
    {
        private volatile Token? _token;

        public string Fingerprint { get; } = fingerprint;
        public SemaphoreSlim Lock { get; } = new(1, 1);

        public string? Valid(DateTimeOffset now) => _token is { } token && now < token.RenewAt ? token.Value : null;

        public void Set(string? token, DateTimeOffset renewAt) => _token = token is null ? null : new Token(token, renewAt);
    }

    private sealed class Token(string value, DateTimeOffset renewAt)
    {
        public string Value { get; } = value;
        public DateTimeOffset RenewAt { get; } = renewAt;
    }
}

/// <summary>
/// The OAuth token endpoint gave no access token: it failed, refused the client or was unreachable. The MCP server itself
/// was never asked, so this must not count against its circuit.
/// </summary>
public sealed class McpTokenException(HttpStatusCode? statusCode, Exception? inner = null)
    : Exception($"The MCP server's token endpoint gave no access token{(statusCode is { } code ? $" (HTTP {(int)code})" : string.Empty)}.", inner)
{
    /// <summary>The token endpoint's status when it answered; null when it could not be reached.</summary>
    public HttpStatusCode? StatusCode { get; } = statusCode;
}
