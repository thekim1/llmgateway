using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;
using Ume.LlmGateway.AdminApi.Security;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi.Tests;

/// <summary>Server-side admin sessions (signing out ends every copy of the cookie) and the absolute session limit.</summary>
public sealed class AdminSessionTests
{
    private static readonly TimeSpan MaxSession = TimeSpan.FromHours(12);

    private sealed class RecordingStore(TimeProvider time) : IAdminSessionStore
    {
        public InMemoryAdminSessionStore Inner { get; } = new(time);
        public Dictionary<string, byte[]> Written { get; } = [];

        public Task SetAsync(string sessionId, byte[] value, TimeSpan timeToLive, CancellationToken cancellationToken)
        {
            Written[sessionId] = value;
            return Inner.SetAsync(sessionId, value, timeToLive, cancellationToken);
        }

        public Task<byte[]?> GetAsync(string sessionId, CancellationToken cancellationToken) => Inner.GetAsync(sessionId, cancellationToken);

        public Task RemoveAsync(string sessionId, CancellationToken cancellationToken) => Inner.RemoveAsync(sessionId, cancellationToken);
    }

    private static AuthenticationTicket Ticket(DateTimeOffset expires) => new(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-1"), new Claim("roles", "gateway-admin"), new Claim("email", "anna@umea.se")], "test")),
        new AuthenticationProperties { ExpiresUtc = expires }, CookieAuthenticationDefaults.AuthenticationScheme);

    [Fact]
    public async Task Ticket_store_keeps_encrypted_sessions_and_forgets_them_on_sign_out()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new RecordingStore(time);
        var tickets = new AdminTicketStore(store, new EphemeralDataProtectionProvider(), time);

        var id = await tickets.StoreAsync(Ticket(time.GetUtcNow().AddHours(8)));

        id.Length.ShouldBeGreaterThanOrEqualTo(40); // 32 random bytes
        (await tickets.RetrieveAsync(id)).ShouldNotBeNull().Principal.FindFirst("sub")!.Value.ShouldBe("user-1");
        var stored = Encoding.UTF8.GetString(store.Written[id]);
        stored.ShouldNotContain("user-1");
        stored.ShouldNotContain("anna@umea.se");

        await tickets.RemoveAsync(id);
        (await tickets.RetrieveAsync(id)).ShouldBeNull();
    }

    [Fact]
    public async Task Ticket_store_refuses_entries_it_did_not_write_and_expired_sessions()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryAdminSessionStore(time);
        var tickets = new AdminTicketStore(store, new EphemeralDataProtectionProvider(), time);

        // Someone who can write to the store cannot create a session.
        await store.SetAsync("forged", TicketSerializer.Default.Serialize(Ticket(time.GetUtcNow().AddHours(8))), TimeSpan.FromHours(8), CancellationToken.None);
        (await tickets.RetrieveAsync("forged")).ShouldBeNull();

        var id = await tickets.StoreAsync(Ticket(time.GetUtcNow().AddHours(1)));
        time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        (await tickets.RetrieveAsync(id)).ShouldBeNull();
    }

    [Fact]
    public void Sign_in_stamps_the_session_and_extension_stops_at_the_limit()
    {
        var signedIn = new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);
        var properties = new AuthenticationProperties { ExpiresUtc = signedIn + AdminSessionLimit.Lifetime };
        var context = new CookieSigningInContext(new DefaultHttpContext(),
            new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions(), new ClaimsPrincipal(), properties, new CookieOptions());

        AdminSessionLimit.OnSigningIn(context, MaxSession, signedIn);
        AdminSessionLimit.LimitOf(properties, MaxSession).ShouldBe(signedIn.AddHours(12));
        properties.ExpiresUtc.ShouldBe(signedIn.AddHours(8));

        // Two hours in: a full lifetime. Seven hours in: only up to the limit. At the limit: refused, nothing changes.
        AdminSessionLimit.Extend(properties, MaxSession, signedIn.AddHours(2)).ShouldBe(signedIn.AddHours(10));
        AdminSessionLimit.Extend(properties, MaxSession, signedIn.AddHours(7)).ShouldBe(signedIn.AddHours(12));
        AdminSessionLimit.Extend(properties, MaxSession, signedIn.AddHours(12)).ShouldBeNull();
        properties.ExpiresUtc.ShouldBe(signedIn.AddHours(12));

        // Signing in again on extension keeps the original sign-in time, so the limit does not move either.
        AdminSessionLimit.OnSigningIn(context, MaxSession, signedIn.AddHours(11));
        AdminSessionLimit.LimitOf(properties, MaxSession).ShouldBe(signedIn.AddHours(12));
    }

    [Fact]
    public void First_expiry_is_capped_when_the_limit_is_shorter_than_a_lifetime()
    {
        // Whole seconds: AuthenticationProperties keeps ExpiresUtc with one-second precision.
        var now = new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);
        var properties = new AuthenticationProperties { ExpiresUtc = now + AdminSessionLimit.Lifetime };
        var context = new CookieSigningInContext(new DefaultHttpContext(),
            new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions(), new ClaimsPrincipal(), properties, new CookieOptions());

        AdminSessionLimit.OnSigningIn(context, TimeSpan.FromHours(2), now);

        properties.ExpiresUtc.ShouldBe(now.AddHours(2));
    }
}
