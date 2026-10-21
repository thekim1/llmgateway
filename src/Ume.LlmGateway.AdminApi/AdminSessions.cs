using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi;

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

/// <summary>
/// The absolute session limit (<see cref="AdminOptions.MaxSessionHours"/>): the sign-in time is kept in the ticket, the
/// first expiry is capped by it, and extending never goes past it. After it the user signs in at the identity provider
/// again, which also picks up role and department changes made there.
/// </summary>
public static class AdminSessionLimit
{
    /// <summary>How long one sign-in or extension lasts at most.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);

    private const string SignedInItem = ".ume.signedIn";

    /// <summary>Stamps the sign-in time on a new session and caps its expiry (cookie <c>OnSigningIn</c>).</summary>
    public static void OnSigningIn(CookieSigningInContext context, TimeSpan maxSession, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Properties.Items.ContainsKey(SignedInItem))
        {
            context.Properties.Items[SignedInItem] = now.ToString("O", CultureInfo.InvariantCulture);
        }

        if (LimitOf(context.Properties, maxSession) is { } limit && (context.Properties.ExpiresUtc is not { } expires || expires > limit))
        {
            context.Properties.ExpiresUtc = limit;
        }
    }

    /// <summary>
    /// Moves the expiry to <see cref="Lifetime"/> from now, but not past the limit; null (and nothing changed) when less than a
    /// minute is left before the limit. A ticket without a sign-in time is treated as signed in now and stamped.
    /// </summary>
    public static DateTimeOffset? Extend(AuthenticationProperties properties, TimeSpan maxSession, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(properties);
        if (!properties.Items.ContainsKey(SignedInItem))
        {
            properties.Items[SignedInItem] = now.ToString("O", CultureInfo.InvariantCulture);
        }

        var limit = LimitOf(properties, maxSession)!.Value;
        if (limit <= now.AddMinutes(1))
        {
            return null;
        }

        var expires = now + Lifetime < limit ? now + Lifetime : limit;
        properties.ExpiresUtc = expires;
        properties.IsPersistent = false;
        return expires;
    }

    /// <summary>When the session must end at the latest; null for a ticket without a sign-in time.</summary>
    public static DateTimeOffset? LimitOf(AuthenticationProperties? properties, TimeSpan maxSession) =>
        properties?.Items.TryGetValue(SignedInItem, out var value) == true &&
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var signedIn)
            ? signedIn + maxSession
            : null;
}
