using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.AdminApi.Security;

/// <summary>
/// Resolves the client address for every request, and reports what security teams need to see that the audit log does
/// not hold: refused requests (<c>admin.access.denied</c>, at most one event per user and minute with the count since the
/// last) and reads of sensitive lists (<c>admin.data.read</c>: the audit log, request lists, PII incidents, Data API keys).
/// </summary>
public sealed class AdminActivityMiddleware(RequestDelegate next, AdminClientAddresses addresses, AccessDeniedThrottle denied, ILoggerFactory loggers)
{
    private static readonly PathString[] SensitiveReads =
        ["/api/audit", "/api/usage/requests", "/api/usage/pii-incidents", "/api/data-access/keys"];

    private const int MaxQuery = 500;
    private readonly ILogger _security = SecurityEvents.CreateLogger(loggers);

    public async Task InvokeAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        await addresses.ResolveAsync(http);
        await next(http);

        if (http.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        if (http.Response.StatusCode == StatusCodes.Status403Forbidden && denied.ShouldLog(Subject(http), out var count))
        {
            var actor = AdminActor.Of(http);
            SecurityEvents.AdminAccessDenied(_security, actor.Subject, http.Request.Method, AdminActor.Printable(http.Request.Path.Value) ?? "",
                count, actor.SourceAddress, actor.Roles);
        }
        else if (http.Response.StatusCode == StatusCodes.Status200OK && HttpMethods.IsGet(http.Request.Method) &&
                 SensitiveReads.Any(p => http.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
        {
            var query = http.Request.QueryString.Value ?? "";
            var actor = AdminActor.Of(http);
            SecurityEvents.AdminDataRead(_security, actor.Subject, AdminActor.Printable(http.Request.Path.Value) ?? "",
                AdminActor.Printable(query.Length > MaxQuery ? query[..MaxQuery] : query) ?? "", actor.SourceAddress);
        }
    }

    private static string Subject(HttpContext http) =>
        http.User.FindFirstValue(AdminClaims.Subject) ?? http.User.Identity?.Name ?? "unknown";
}
