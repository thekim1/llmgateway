using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.AdminApi;

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

/// <summary>
/// The admin user's address behind the reverse proxy. The connection comes from the proxy, so the gateway's trusted
/// proxies (Settings &gt; Trusted proxies) decide whether <c>X-Forwarded-For</c> is believed; they are read at most every
/// 30 seconds per instance. <c>Admin:AuditSourceAddress</c> decides how much of it is kept.
/// </summary>
public sealed partial class AdminClientAddresses(IServiceScopeFactory scopes, TimeProvider time, IOptions<AdminOptions> options,
    ILogger<AdminClientAddresses> logger)
{
    public const string ItemKey = "ume.clientAddress";
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    private ImmutableArray<IPNetwork> _trusted = [];
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;

    /// <summary>Puts the formatted client address of <paramref name="http"/> in <c>Items[ItemKey]</c>.</summary>
    public async Task ResolveAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var mode = options.Value.AuditSourceAddress;
        if (mode == SourceAddressMode.None)
        {
            return;
        }

        var client = ClientAddresses.Resolve(http.Connection.RemoteIpAddress, http.Request.Headers[ClientAddresses.ForwardedForHeader],
            (await TrustedAsync(http.RequestAborted)).AsSpan());
        http.Items[ItemKey] = ClientAddresses.Format(client, mode);
    }

    /// <summary>Forgets the cached list, after this instance saved new trusted proxies.</summary>
    public void Invalidate() => _readAt = DateTimeOffset.MinValue;

    private async ValueTask<ImmutableArray<IPNetwork>> TrustedAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (now - _readAt < CacheFor)
        {
            return _trusted;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var settings = await db.Settings.AsNoTracking().Select(s => s.TrustedProxies).SingleOrDefaultAsync(ct);
            _trusted = [.. NetworkRanges.ParseValid(settings ?? [])];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep the last list; the connection address is used until the database answers.
            LogTrustedProxiesUnavailable(logger, ex.GetType().Name);
        }

        _readAt = now;
        return _trusted;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Trusted proxies could not be read ({ExceptionType}); the last list read is used for admin client addresses")]
    private static partial void LogTrustedProxiesUnavailable(ILogger logger, string exceptionType);
}

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

/// <summary>
/// One <c>admin.access.denied</c> event per user and minute: the first refusal is logged at once, later ones in the same
/// minute are counted into the next event. Bounded, so a flood of users cannot grow it without limit.
/// </summary>
public sealed class AccessDeniedThrottle(TimeProvider time)
{
    private const int MaxUsers = 10_000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, Entry> _users = new(StringComparer.Ordinal);

    /// <summary>Whether to log this refusal now; <paramref name="count"/> is the refusals it stands for.</summary>
    public bool ShouldLog(string subject, out int count)
    {
        var now = time.GetUtcNow();
        if (_users.Count >= MaxUsers)
        {
            _users.Clear();
        }

        var entry = _users.GetOrAdd(subject, _ => new Entry());
        lock (entry)
        {
            entry.Pending++;
            if (now - entry.LoggedAt < Window)
            {
                count = 0;
                return false;
            }

            count = entry.Pending;
            entry.Pending = 0;
            entry.LoggedAt = now;
            return true;
        }
    }

    private sealed class Entry
    {
        public DateTimeOffset LoggedAt { get; set; } = DateTimeOffset.MinValue;
        public int Pending { get; set; }
    }
}
