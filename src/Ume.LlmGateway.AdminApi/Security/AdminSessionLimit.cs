using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Ume.LlmGateway.AdminApi.Common;

namespace Ume.LlmGateway.AdminApi.Security;

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
