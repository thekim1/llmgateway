using System.Net;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>The response headers the gateway sets on every response, and the stricter ones on the client API (<c>/v1</c>).</summary>
public sealed class SecurityHeaderTests(GatewayFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void AssertCommonHeaders(HttpResponseMessage response)
    {
        response.Headers.GetValues("X-Content-Type-Options").Single().ShouldBe("nosniff");
        response.Headers.GetValues("X-Frame-Options").Single().ShouldBe("DENY");
        response.Headers.GetValues("Referrer-Policy").Single().ShouldBe("no-referrer");
        response.Headers.Contains("Server").ShouldBeFalse();
    }

    [Fact]
    public async Task Client_api_errors_get_the_api_csp_and_no_store()
    {
        using var response = await fixture.SendAsync(key: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        AssertCommonHeaders(response);
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldBe("default-src 'none'; frame-ancestors 'none'");
        response.Headers.GetValues("Cache-Control").Single().ShouldBe("no-store");
    }

    [Fact]
    public async Task Client_api_answers_get_the_api_csp_and_no_store()
    {
        var key = await fixture.CreateKeyAsync();
        using var response = await fixture.SendAsync(key);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        AssertCommonHeaders(response);
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldBe("default-src 'none'; frame-ancestors 'none'");
        response.Headers.GetValues("Cache-Control").Single().ShouldBe("no-store");
    }

    [Fact]
    public async Task A_streamed_answer_keeps_its_own_cache_control()
    {
        var key = await fixture.CreateKeyAsync();
        using var response = await fixture.SendAsync(key, stream: true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        AssertCommonHeaders(response);
        response.Headers.CacheControl!.NoCache.ShouldBeTrue();
        response.Headers.CacheControl.NoStore.ShouldBeTrue();
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldBe("default-src 'none'; frame-ancestors 'none'");
    }

    [Theory]
    [InlineData("/version")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/no-such-page")]
    public async Task Other_paths_get_the_common_headers_without_the_api_csp(string path)
    {
        using var response = await fixture.Client.GetAsync(path, Ct);

        AssertCommonHeaders(response);
        response.Headers.Contains("Content-Security-Policy").ShouldBeFalse();
        response.Headers.CacheControl?.NoStore.ShouldNotBe(true);
    }
}
