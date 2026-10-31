using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.AdminApi.Settings;

namespace Ume.LlmGateway.AdminApi.Tests;

public sealed class ReleaseCheckTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Release = """
        {"tag_name":"v99.0.0","name":"Ume LLM Gateway 99.0.0","html_url":"https://github.com/thekim1/llmgateway/releases/tag/v99.0.0","published_at":"2026-10-18T18:57:01Z"}
        """;

    [Theory]
    [InlineData("0.6.1", "0.6.0", 1)]
    [InlineData("0.10.0", "0.9.9", 1)]
    [InlineData("v1.0.0", "1.0.0", 0)]
    [InlineData("1.0.0", "1.0.0-rc.1", 1)]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.1", 1)]
    [InlineData("0.6.0", "0.6.1+abc", -1)]
    [InlineData("not-a-version", "0.6.0", 0)]
    public void Versions_compare_by_number_and_releases_beat_prereleases(string a, string b, int expected) =>
        Math.Sign(ReleaseCheck.CompareVersions(a, b)).ShouldBe(expected);

    [Fact]
    public async Task Latest_release_is_read_from_github_and_cached()
    {
        var calls = 0;
        using var handler = new FakeGitHub(request =>
        {
            calls++;
            request.RequestUri!.ToString().ShouldBe("https://api.github.com/repos/thekim1/llmgateway/releases/latest");
            request.Headers.UserAgent.ShouldNotBeEmpty();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Release) };
        });
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var check = Check(handler, time);

        var info = await check.GetAsync(Ct);
        info.CheckEnabled.ShouldBeTrue();
        info.CheckFailed.ShouldBeFalse();
        info.Latest.ShouldBe(new LatestReleaseDto("99.0.0", "Ume LLM Gateway 99.0.0", "https://github.com/thekim1/llmgateway/releases/tag/v99.0.0",
            DateTimeOffset.Parse("2026-10-18T18:57:01Z", System.Globalization.CultureInfo.InvariantCulture)));
        // Test builds are 0.0.0-local: never told to upgrade.
        info.DevelopmentBuild.ShouldBe(ReleaseCheck.IsDevelopmentBuild(info.Version));
        info.UpdateAvailable.ShouldBe(!info.DevelopmentBuild);

        await check.GetAsync(Ct);
        calls.ShouldBe(1);
        time.Advance(TimeSpan.FromHours(7));
        await check.GetAsync(Ct);
        calls.ShouldBe(2);
    }

    [Theory]
    [InlineData(403, "{}")]
    [InlineData(200, "not json")]
    [InlineData(200, """{"tag_name":"latest"}""")]
    public async Task Failed_or_malformed_check_reports_failure_not_a_release(int code, string body)
    {
        using var handler = new FakeGitHub(_ => new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(body) });
        using var check = Check(handler);
        var info = await check.GetAsync(Ct);
        info.CheckFailed.ShouldBeTrue();
        info.Latest.ShouldBeNull();
        info.UpdateAvailable.ShouldBeFalse();
    }

    [Fact]
    public async Task Repository_without_releases_is_not_a_failure()
    {
        using var handler = new FakeGitHub(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var check = Check(handler);
        var info = await check.GetAsync(Ct);
        info.CheckFailed.ShouldBeFalse();
        info.Latest.ShouldBeNull();
    }

    [Fact]
    public async Task Link_outside_github_is_replaced_by_the_release_page()
    {
        using var handler = new FakeGitHub(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"tag_name":"v1.2.3","html_url":"https://evil.example/download"}"""),
        });
        using var check = Check(handler);
        var latest = (await check.GetAsync(Ct)).Latest.ShouldNotBeNull();
        latest.Url.ShouldBe("https://github.com/thekim1/llmgateway/releases/tag/v1.2.3");
        latest.Name.ShouldBe("v1.2.3");
    }

    [Fact]
    public async Task Switched_off_check_never_calls_github()
    {
        using var handler = new FakeGitHub(_ => throw new InvalidOperationException("Must not call GitHub"));
        using var check = Check(handler, options: new AdminOptions { CheckForReleases = false });
        var info = await check.GetAsync(Ct);
        info.CheckEnabled.ShouldBeFalse();
        info.Latest.ShouldBeNull();
        info.Version.ShouldNotBeNullOrEmpty();
    }

    private static ReleaseCheck Check(HttpMessageHandler handler, TimeProvider? time = null, AdminOptions? options = null) =>
        new(new FakeClientFactory(handler), Options.Create(options ?? new AdminOptions()), time ?? TimeProvider.System, NullLogger<ReleaseCheck>.Instance);

    private sealed class FakeGitHub(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private sealed class FakeClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            name.ShouldBe(ReleaseCheck.HttpClientName);
            return new HttpClient(handler, disposeHandler: false);
        }
    }
}

public sealed class VersionEndpointTests(AdminFixture fixture)
{
    [Theory]
    [InlineData(null, 401)]
    [InlineData("viewer", 403)]
    [InlineData("department-admin", 403)]
    [InlineData("gateway-admin", 200)]
    public async Task Only_gateway_admins_see_the_version(string? role, int status)
    {
        using var client = await fixture.ClientAsync(role);
        using var read = await client.GetAsync("/api/settings/version", TestContext.Current.CancellationToken);
        ((int)read.StatusCode).ShouldBe(status);
        if (status == 200)
        {
            var body = await AdminFixture.ReadAsync(read);
            body["version"]!.GetValue<string>().ShouldBe(ReleaseCheck.CurrentVersion);
            body["checkEnabled"]!.GetValue<bool>().ShouldBeFalse(); // switched off in the fixture
            body["updateAvailable"]!.GetValue<bool>().ShouldBeFalse();
        }
    }
}
