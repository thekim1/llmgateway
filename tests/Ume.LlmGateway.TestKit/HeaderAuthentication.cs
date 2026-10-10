using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ume.LlmGateway.TestKit;

public sealed class HeaderAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>The role of a request without <see cref="HeaderAuthenticationHandler.RoleHeader"/>; null leaves it anonymous.</summary>
    public string? DefaultRole { get; set; }

    public string Subject { get; set; } = "test-user";
    public string Name { get; set; } = "Test user";
}

/// <summary>
/// Signs requests in as the user their headers describe: <see cref="RoleHeader"/> (one role) and <see cref="DepartmentHeader"/>
/// (comma-separated department codes), with the same claim types the OIDC mapping produces.
/// </summary>
public sealed class HeaderAuthenticationHandler(IOptionsMonitor<HeaderAuthenticationOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<HeaderAuthenticationOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string RoleHeader = "X-Test-Role";
    public const string DepartmentHeader = "X-Test-Department";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var role = Request.Headers[RoleHeader].ToString();
        if (role.Length == 0)
        {
            role = Options.DefaultRole ?? string.Empty;
        }

        if (role.Length == 0)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new("sub", Options.Subject), new("name", Options.Name), new("roles", role) };
        foreach (var code in Request.Headers[DepartmentHeader].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            claims.Add(new Claim("departmentCodes", code));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name, "name", "roles"));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}

public static class HeaderAuthenticationExtensions
{
    /// <summary>Makes <see cref="HeaderAuthenticationHandler"/> the default for every authentication action (the host's OIDC/cookie schemes stay registered).</summary>
    public static IServiceCollection AddHeaderAuthentication(this IServiceCollection services, Action<HeaderAuthenticationOptions>? configure = null)
    {
        services.AddAuthentication(o =>
        {
            o.DefaultScheme = HeaderAuthenticationHandler.SchemeName;
            o.DefaultAuthenticateScheme = HeaderAuthenticationHandler.SchemeName;
            o.DefaultChallengeScheme = HeaderAuthenticationHandler.SchemeName;
            o.DefaultForbidScheme = HeaderAuthenticationHandler.SchemeName;
        }).AddScheme<HeaderAuthenticationOptions, HeaderAuthenticationHandler>(HeaderAuthenticationHandler.SchemeName, configure ?? (_ => { }));
        return services;
    }
}
