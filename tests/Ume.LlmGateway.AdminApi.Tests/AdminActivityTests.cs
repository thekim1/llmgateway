using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Ume.LlmGateway.AdminApi.Tests;

/// <summary>What admin users do is attributable: audit entries say who, from where and in which session, and security events
/// cover sign-in, sign-out, session extension, refused requests and reads of sensitive lists.</summary>
public sealed class AdminActivityTests(AdminFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Audit_entries_say_who_made_them_in_which_request_and_with_which_client()
    {
        using var client = await fixture.ClientAsync();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AuditTest/1.0");
        var org = await fixture.OrganisationAsync(client);

        using var audit = await client.GetAsync($"/api/audit?entityType=Team&pageSize=200", Ct);
        var entry = (await AdminFixture.ReadAsync(audit))["items"]!.AsArray().First(e => e!["entityId"]!.GetValue<string>() == org.Team.ToString())!;
        entry["actorName"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
        entry["actorRoles"]!.GetValue<string>().ShouldBe("gateway-admin");
        entry["requestId"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
        entry["userAgent"]!.GetValue<string>().ShouldBe("AuditTest/1.0");
    }

    [Fact]
    public async Task Exports_are_recorded_in_the_audit_log()
    {
        using var client = await fixture.ClientAsync();
        (await client.GetAsync("/api/ops/config/export", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/api/usage/export.csv", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var config = await client.GetAsync("/api/audit?entityType=Config", Ct);
        (await AdminFixture.ReadAsync(config))["items"]!.AsArray().ShouldContain(e => e!["action"]!.GetValue<string>() == "export");
        using var usage = await client.GetAsync("/api/audit?entityType=Usage", Ct);
        var export = (await AdminFixture.ReadAsync(usage))["items"]!.AsArray().First()!;
        export["action"]!.GetValue<string>().ShouldBe("export");
        export["details"]!.ToString().ShouldContain("rows");
    }

    [Fact]
    public async Task Refused_requests_are_logged_at_most_once_a_minute_per_user_with_a_count()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var loggers = new CapturingLoggerFactory();
        var middleware = Middleware(http => { http.Response.StatusCode = 403; return Task.CompletedTask; }, time, loggers);

        await middleware.InvokeAsync(Request("viewer", "/api/providers"));
        await middleware.InvokeAsync(Request("viewer", "/api/providers"));
        await middleware.InvokeAsync(Request("viewer", "/api/keys/1/reveal", "POST"));
        time.Advance(TimeSpan.FromMinutes(1));
        await middleware.InvokeAsync(Request("viewer", "/api/providers\nwarn: forged"));

        var denied = loggers.Messages.Where(m => m.Contains("|admin.access.denied|", StringComparison.Ordinal)).ToList();
        denied.Count.ShouldBe(2);
        denied[0].ShouldContain("Admin viewer-subject was refused GET /api/providers x1 from 192.0.2.10 (roles viewer)");
        denied[1].ShouldContain("refused GET /api/providers?warn: forged x3");
    }

    [Theory]
    [InlineData("/api/audit", true)]
    [InlineData("/api/usage/requests", true)]
    [InlineData("/api/usage/pii-incidents", true)]
    [InlineData("/api/data-access/keys", true)]
    [InlineData("/api/departments", false)]
    public async Task Reads_of_sensitive_lists_are_logged(string path, bool logged)
    {
        using var loggers = new CapturingLoggerFactory();
        var middleware = Middleware(_ => Task.CompletedTask, TimeProvider.System, loggers);

        var http = Request("admin", path);
        http.Request.QueryString = new QueryString("?departmentId=d1");
        await middleware.InvokeAsync(http);

        loggers.Messages.Any(m => m.Contains($"|admin.data.read|Admin admin-subject read {path}?departmentId=d1 from 192.0.2.10", StringComparison.Ordinal))
            .ShouldBe(logged);
    }

    [Fact]
    public async Task A_new_sign_in_gets_a_session_id_and_an_event_an_extension_keeps_both()
    {
        using var loggers = new CapturingLoggerFactory();
        var services = new ServiceCollection().AddSingleton<ILoggerFactory>(loggers).AddSingleton(TimeProvider.System)
            .AddSingleton(Options.Create(new AdminOptions())).BuildServiceProvider();
        var options = fixture.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var http = new DefaultHttpContext { RequestServices = services };
        http.Items[AdminClientAddresses.ItemKey] = "192.0.2.10";
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "new-user"), new Claim("name", "New User"), new Claim("roles", "department-admin"), new Claim("departmentCodes", "HR")], "oidc", "name", "roles"));

        await options.Events.SigningIn(SigningIn(http, options, principal));
        var session = principal.FindFirstValue(AdminClaims.Session).ShouldNotBeNull();
        loggers.Messages.ShouldHaveSingleItem().ShouldContain(
            $"|admin.signin|Admin new-user signed in from 192.0.2.10 with roles department-admin, departments HR; session {session}");

        // The extension signs in again with the same principal: same session, no second sign-in event.
        await options.Events.SigningIn(SigningIn(http, options, principal));
        principal.FindAll(AdminClaims.Session).ShouldHaveSingleItem().Value.ShouldBe(session);
        loggers.Messages.Count.ShouldBe(1);
    }

    [Fact]
    public void Actor_text_from_the_client_has_no_control_characters()
    {
        var http = Request("admin", "/api/departments");
        http.Request.Headers.UserAgent = "Agent\r\nwarn: forged" + new string('x', 300);

        var actor = AdminActor.Of(http);

        actor.UserAgent!.Length.ShouldBe(200);
        actor.UserAgent.ShouldStartWith("Agent??warn: forged");
        actor.SourceAddress.ShouldBe("192.0.2.10");
    }

    private AdminActivityMiddleware Middleware(RequestDelegate next, TimeProvider time, CapturingLoggerFactory loggers) => new(next,
        new AdminClientAddresses(fixture.Services.GetRequiredService<IServiceScopeFactory>(), time, Options.Create(new AdminOptions()),
            loggers.CreateLogger<AdminClientAddresses>()),
        new AccessDeniedThrottle(time), loggers);

    private static DefaultHttpContext Request(string role, string path, string method = "GET")
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", $"{role}-subject"), new Claim("roles", role)], "Test", "name", "roles")),
        };
        http.Request.Method = method;
        http.Request.Path = path;
        http.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        http.Items[AdminClientAddresses.ItemKey] = "192.0.2.10";
        return http;
    }

    private static CookieSigningInContext SigningIn(HttpContext http, CookieAuthenticationOptions options, ClaimsPrincipal principal) => new(http,
        new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler)),
        options, principal, new AuthenticationProperties(), new CookieOptions());
}
