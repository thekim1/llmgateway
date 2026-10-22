using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ume.LlmGateway.TestKit;

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

    [Fact]
    public async Task The_api_reference_runs_only_scripts_carrying_its_nonce()
    {
        using var first = await fixture.Client.GetAsync("/scalar/", Ct);
        using var second = await fixture.Client.GetAsync("/scalar/", Ct);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var csp = first.Headers.GetValues("Content-Security-Policy").Single();
        var nonce = Regex.Match(csp, "'nonce-([^']+)'").Groups[1].Value;
        nonce.ShouldNotBeEmpty();
        csp.ShouldStartWith("default-src 'none'; script-src 'self' 'nonce-");
        csp.ShouldContain("frame-ancestors 'none'");
        var html = await first.Content.ReadAsStringAsync(Ct);
        Regex.Matches(html, "<script(?![^>]*nonce=\"" + Regex.Escape(nonce) + "\")").ShouldBeEmpty();
        second.Headers.GetValues("Content-Security-Policy").Single().ShouldNotBe(csp);
    }

    [Fact]
    public async Task The_api_reference_can_be_switched_off()
    {
        await using var gateway = fixture.Derive(_ => { }, host => host.UseSetting("Gateway:PublishApiReference", "false"));
        using var client = gateway.CreateHttpsClient();

        (await client.GetAsync("/scalar", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync("/openapi/v1.json", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync("/", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Errors no longer point at the reference that is gone.
        using var error = await client.PostAsync("/v1/chat/completions", new StringContent("{}"), Ct);
        JsonNode.Parse(await error.Content.ReadAsStringAsync(Ct))!["error"]!["doc_url"].ShouldBeNull();
    }
}
