using System.Collections.Immutable;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.AdminApi.Security;

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
