using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.AdminApi.Auditing;
using Ume.LlmGateway.AdminApi.Settings;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.ServiceDefaults;

namespace Ume.LlmGateway.AdminApi.Tests;

public sealed class BrandingTests(AdminFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The smallest valid PNG (1x1, transparent).
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private const string Svg = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><circle cx="5" cy="5" r="4" fill="#0a6"/></svg>""";

    [Theory]
    [InlineData(null, 401)]
    [InlineData("viewer", 403)]
    [InlineData("department-admin", 403)]
    public async Task Only_gateway_admins_change_the_branding(string? role, int status)
    {
        using var client = await fixture.ClientAsync(role);
        using var name = await client.PutAsJsonAsync("/api/settings/branding", new { organisationName = "Someone else" }, Ct);
        ((int)name.StatusCode).ShouldBe(status);
        using var logo = await client.PutAsJsonAsync("/api/settings/branding/logo", new { data = Convert.ToBase64String(Png) }, Ct);
        ((int)logo.StatusCode).ShouldBe(status);
        using var remove = await client.DeleteAsync("/api/settings/branding/logo", Ct);
        ((int)remove.StatusCode).ShouldBe(status);
    }

    [Fact]
    public async Task Name_and_logo_are_set_by_gateway_admins_shown_to_everyone_and_audited_without_the_image()
    {
        using var admin = await fixture.ClientAsync();
        using var anonymous = await fixture.ClientAsync(null);
        try
        {
            using var named = await admin.PutAsJsonAsync("/api/settings/branding", new { organisationName = "  Example kommun " }, Ct);
            (await AdminFixture.ReadAsync(named))["organisationName"]!.GetValue<string>().ShouldBe("Example kommun");

            using var uploaded = await admin.PutAsJsonAsync("/api/settings/branding/logo", new { data = Convert.ToBase64String(Png) }, Ct);
            var logoUrl = (await AdminFixture.ReadAsync(uploaded))["logoUrl"]!.GetValue<string>();
            logoUrl.ShouldStartWith("/bff/branding/logo?v=");

            // The sign-in page reads it before anyone signs in; this instance shows a change at once.
            using var shown = await anonymous.GetAsync("/bff/branding", Ct);
            shown.StatusCode.ShouldBe(HttpStatusCode.OK);
            var branding = await AdminFixture.ReadAsync(shown);
            branding["organisationName"]!.GetValue<string>().ShouldBe("Example kommun");
            branding["logoUrl"]!.GetValue<string>().ShouldBe(logoUrl);

            using var image = await anonymous.GetAsync(logoUrl, Ct);
            image.StatusCode.ShouldBe(HttpStatusCode.OK);
            image.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
            (await image.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(Png);
            image.Headers.CacheControl!.ToString().ShouldContain("immutable");
            image.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("sandbox");

            using var stale = await anonymous.GetAsync("/bff/branding/logo?v=0000", Ct);
            stale.Headers.CacheControl!.NoCache.ShouldBeTrue();

            using var svg = await admin.PutAsJsonAsync("/api/settings/branding/logo", new { data = Convert.ToBase64String(Encoding.UTF8.GetBytes(Svg)) }, Ct);
            using var svgImage = await anonymous.GetAsync((await AdminFixture.ReadAsync(svg))["logoUrl"]!.GetValue<string>(), Ct);
            svgImage.Content.Headers.ContentType!.MediaType.ShouldBe("image/svg+xml");

            using var removed = await admin.DeleteAsync("/api/settings/branding/logo", Ct);
            (await AdminFixture.ReadAsync(removed))["logoUrl"].ShouldBeNull();
            using var gone = await anonymous.GetAsync(logoUrl, Ct);
            gone.StatusCode.ShouldBe(HttpStatusCode.NotFound);

            using var scope = fixture.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            // Other settings tests write entries in parallel, so look among the newest for ours.
            var audits = (await db.AuditLog.Where(a => a.EntityType == AuditEntities.Settings).OrderByDescending(a => a.Id).Take(50).ToListAsync(Ct))
                .Where(a => a.Details is not null && a.Details.Contains("logoSha256", StringComparison.OrdinalIgnoreCase)).ToList();
            audits.ShouldNotBeEmpty();
            audits.ShouldAllBe(a => !a.Details!.Contains(Convert.ToBase64String(Png), StringComparison.Ordinal));
        }
        finally
        {
            using var reset = await admin.PutAsJsonAsync("/api/settings/branding", new { organisationName = Branding.DefaultOrganisationName }, Ct);
            using var noLogo = await admin.DeleteAsync("/api/settings/branding/logo", Ct);
        }
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("SGVsbG8sIHdvcmxk")] // "Hello, world": not an image
    public async Task Logos_that_are_not_images_are_refused(string data)
    {
        using var admin = await fixture.ClientAsync();
        using var response = await admin.PutAsJsonAsync("/api/settings/branding/logo", new { data }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Names_longer_than_the_limit_and_oversized_logos_are_refused()
    {
        using var admin = await fixture.ClientAsync();
        using var name = await admin.PutAsJsonAsync("/api/settings/branding", new { organisationName = new string('x', Branding.MaxOrganisationNameLength + 1) }, Ct);
        name.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var big = new byte[Branding.MaxLogoBytes + 1];
        Png.CopyTo(big, 0);
        using var logo = await admin.PutAsJsonAsync("/api/settings/branding/logo", new { data = Convert.ToBase64String(big) }, Ct);
        logo.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg" onload="alert(1)"></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><a href="javascript:alert(1)"><circle r="4"/></a></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><foreignObject><p>html</p></foreignObject></svg>""")]
    public void Svg_logos_with_scripts_or_html_are_refused(string svg) =>
        Should.Throw<ApiFaultException>(() => LogoImage.Parse(Convert.ToBase64String(Encoding.UTF8.GetBytes(svg))));

    [Fact]
    public void Logo_types_come_from_the_content()
    {
        LogoImage.Detect(Png).ShouldBe(LogoImage.Png);
        LogoImage.Detect([0xFF, 0xD8, 0xFF, 0xE0, 0, 0]).ShouldBe(LogoImage.Jpeg);
        LogoImage.Detect("RIFF\0\0\0\0WEBPVP8 "u8).ShouldBe(LogoImage.WebP);
        LogoImage.Detect(Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?>\n" + Svg)).ShouldBe(LogoImage.Svg);
        LogoImage.Detect("GIF89a"u8).ShouldBeNull();
    }
}
