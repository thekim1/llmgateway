using Microsoft.AspNetCore.Mvc.Testing;

namespace Ume.LlmGateway.TestKit;

/// <summary>Clients for in-memory hosts: https (so secure cookies and HSTS behave as deployed) and, by default, no redirects followed.</summary>
public static class TestClients
{
    public static readonly Uri BaseAddress = new("https://localhost");

    public static WebApplicationFactoryClientOptions Options(bool allowAutoRedirect = false) =>
        new() { BaseAddress = BaseAddress, AllowAutoRedirect = allowAutoRedirect };

    public static HttpClient CreateHttpsClient<TEntryPoint>(this WebApplicationFactory<TEntryPoint> factory, bool allowAutoRedirect = false)
        where TEntryPoint : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory.CreateClient(Options(allowAutoRedirect));
    }

    /// <summary>
    /// Fetches <c>/bff/user</c> to get the admin API's XSRF cookie and sends its token (and <c>X-Requested-With</c>) on
    /// every following request, as the admin UI does. The client keeps the cookie.
    /// </summary>
    public static async Task<HttpClient> WithXsrfTokenAsync(this HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var user = await client.GetAsync(new Uri("/bff/user", UriKind.Relative));
        user.EnsureSuccessStatusCode();
        const string cookie = "XSRF-TOKEN=";
        var token = user.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(cookie, StringComparison.Ordinal)).Split(';')[0][cookie.Length..];
        client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", token);
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return client;
    }
}
