using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi.Security;

/// <summary>
/// Keeps admin sessions on the server (<see cref="IAdminSessionStore"/>, Redis in production): the cookie holds only a random
/// session id, so signing out ends the session for every copy of the cookie. The ticket is encrypted before it is stored, so
/// someone who can write to the store cannot create a session.
/// </summary>
public sealed class AdminTicketStore(IAdminSessionStore store, IDataProtectionProvider protection, TimeProvider time) : ITicketStore
{
    private readonly IDataProtector _protector = protection.CreateProtector("Ume.LlmGateway.AdminApi.Session.v1");

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var id = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        await RenewAsync(id, ticket);
        return id;
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var lifetime = (ticket.Properties.ExpiresUtc ?? time.GetUtcNow().AddHours(8)) - time.GetUtcNow();
        return lifetime <= TimeSpan.Zero
            ? store.RemoveAsync(key, CancellationToken.None)
            : store.SetAsync(key, _protector.Protect(TicketSerializer.Default.Serialize(ticket)), lifetime, CancellationToken.None);
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        if (await store.GetAsync(key, CancellationToken.None) is not { } stored)
        {
            return null;
        }

        try
        {
            return TicketSerializer.Default.Deserialize(_protector.Unprotect(stored));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public Task RemoveAsync(string key) => store.RemoveAsync(key, CancellationToken.None);
}
