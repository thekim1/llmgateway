using System.Net;
using System.Net.Sockets;
using Ume.LlmGateway.Infrastructure.Mcp;

namespace Ume.LlmGateway.AdminApi;

/// <summary>Resolves host names for <see cref="McpUrlGuard"/>.</summary>
public interface IMcpHostResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

internal sealed class DnsMcpHostResolver : IMcpHostResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Dns.GetHostAddressesAsync(host, ct);
}

/// <summary>
/// SSRF rules for the URLs an admin saves or tests (MCP servers and their OAuth token endpoints): forbidden addresses never,
/// private ones only for gateway admins (<see cref="McpAddressGuard"/>). Host names are resolved when a URL is checked. A
/// name can resolve differently later (DNS rebinding): every MCP connection of the gateway and the admin API refuses
/// forbidden addresses again when it connects, but a name that later resolves to a private address is reached.
/// </summary>
public sealed class McpUrlGuard(IMcpHostResolver resolver)
{
    public const string ForbiddenMessage = "Adressen pekar på en länklokal adress, en molntjänsts metadatatjänst eller en reserverad adress och får inte användas.";
    public const string PrivateMessage = "Interna adresser (privata nät och localhost) får bara anges av en gatewayadministratör.";
    public const string UnresolvedMessage = "Värdnamnet kunde inte slås upp.";

    /// <summary>Null when <paramref name="url"/> may be used, otherwise why not (Swedish, for the caller).</summary>
    public async Task<string?> CheckAsync(Uri url, bool allowPrivate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        IPAddress[] addresses;
        if (IPAddress.TryParse(url.IdnHost, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await resolver.ResolveAsync(url.IdnHost, ct);
            }
            catch (SocketException)
            {
                return UnresolvedMessage;
            }
        }

        if (addresses.Length == 0)
        {
            return UnresolvedMessage;
        }

        var classes = addresses.Select(McpAddressGuard.Classify).ToList();
        return classes.Contains(McpAddressClass.Forbidden) ? ForbiddenMessage
            : classes.Contains(McpAddressClass.Private) && !allowPrivate ? PrivateMessage
            : null;
    }
}
