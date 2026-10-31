using System.Security.Claims;
using System.Security.Cryptography;
using Ume.LlmGateway.Domain.Entities;

namespace Ume.LlmGateway.AdminApi.Security;

/// <summary>
/// Who acted, from where and in which session, as written to audit entries and admin security events. Never the cookie:
/// the session is a random id given at sign-in (<see cref="AdminClaims.Session"/>).
/// </summary>
public sealed record AdminActor(
    string Subject, string? Name, string Roles, string Departments, string? SourceAddress, string? SessionId, string RequestId, string? UserAgent)
{
    private const int MaxUserAgent = 200;

    public static AdminActor Of(HttpContext http) => Of(http, http?.User ?? throw new ArgumentNullException(nameof(http)));

    /// <summary>For <paramref name="user"/> while it is being signed in (<see cref="HttpContext.User"/> is not set yet).</summary>
    public static AdminActor Of(HttpContext http, ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(user);
        var agent = http.Request.Headers.UserAgent.ToString();
        return new AdminActor(
            user.FindFirstValue(AdminClaims.Subject) ?? user.Identity?.Name ?? "unknown",
            Printable(user.Identity?.Name ?? user.FindFirstValue("email")),
            string.Join(',', user.FindAll(AdminClaims.Roles).Select(c => c.Value).Order(StringComparer.Ordinal)),
            string.Join(',', user.FindAll(AdminClaims.DepartmentCodes).Select(c => c.Value).Order(StringComparer.Ordinal)),
            http.Items[AdminClientAddresses.ItemKey] as string,
            user.FindFirstValue(AdminClaims.Session),
            System.Diagnostics.Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier,
            agent.Length == 0 ? null : Printable(agent.Length > MaxUserAgent ? agent[..MaxUserAgent] : agent));
    }

    /// <summary>Sets the fields of <paramref name="entry"/> that say who made it.</summary>
    public void Stamp(AuditLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.ActorName = Name;
        entry.ActorRoles = Roles;
        entry.ActorDepartments = Departments;
        entry.SourceAddress = SourceAddress;
        entry.SessionId = SessionId;
        entry.RequestId = RequestId;
        entry.UserAgent = UserAgent;
    }

    /// <summary>A new session id: 16 random bytes, hex.</summary>
    public static string NewSessionId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>Client-supplied text with control characters replaced, so it cannot start a forged log line.</summary>
    public static string? Printable(string? value) =>
        value is null || !value.Any(char.IsControl) ? value : string.Concat(value.Select(c => char.IsControl(c) ? '?' : c));
}
