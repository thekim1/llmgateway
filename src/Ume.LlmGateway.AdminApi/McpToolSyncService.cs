using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Mcp;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Security;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi;

/// <summary>What a server offered when the admin API connected: capabilities, instructions and tools, or why it could not.</summary>
public sealed record McpProbeResult(
    McpUpstreamResult Outcome, McpServerCapabilities Capabilities, string? Instructions, IReadOnlyList<McpDiscoveredTool> Tools, IReadOnlyList<string> Warnings);

/// <summary>
/// Connects to an MCP server with the gateway's upstream client and reads its tool list. Each probe has its own client and
/// OAuth token cache, so the server's capabilities and instructions are read fresh, and nothing (no token of a server that
/// was only tested) is kept between probes.
/// </summary>
public sealed class McpProbe(IHttpMessageHandlerFactory handlers, IHttpClientFactory httpClients, TimeProvider time, McpUpstreamOptions options)
{
    public const int MaxPages = 50;
    public const string CredentialUnreadable = "Autentiseringsuppgiften kunde inte läsas. Ange den på nytt.";

    public async Task<McpProbeResult> ProbeAsync(McpServer server, McpUpstreamAuth auth, CancellationToken ct)
    {
        await using var upstream = new McpUpstream(handlers, new McpTokenCache(httpClients, time), options);
        var description = await upstream.DescribeAsync(server, auth, ct);
        if (!description.Outcome.Succeeded)
        {
            return new McpProbeResult(description.Outcome, McpServerCapabilities.None, null, [], []);
        }

        var tools = new List<McpDiscoveredTool>();
        var warnings = new List<string>();
        if (description.Capabilities.HasFlag(McpServerCapabilities.Tools))
        {
            string? cursor = null;
            var pages = 0;
            do
            {
                var parameters = new JsonObject();
                if (cursor is not null)
                {
                    parameters["cursor"] = cursor;
                }

                var page = await upstream.SendAsync(new McpUpstreamCall(server, auth, "tools/list", parameters, [], MayRetry: true), ct);
                if (!page.Succeeded)
                {
                    return new McpProbeResult(page, description.Capabilities, description.Instructions, [], []);
                }

                foreach (var item in page.Result?["tools"] as JsonArray ?? [])
                {
                    if (item is JsonObject tool && tool["name"] is JsonValue name && name.TryGetValue<string>(out var text) && text.Length > 0)
                    {
                        tools.Add(new McpDiscoveredTool(text, McpToolDefinition.FromTool(tool)));
                    }
                    else
                    {
                        warnings.Add("Servern listade ett verktyg utan namn; det togs inte med.");
                    }
                }

                cursor = page.Result?["nextCursor"] is JsonValue next && next.TryGetValue<string>(out var value) && value.Length > 0 ? value : null;
            }
            while (cursor is not null && ++pages < MaxPages);

            if (cursor is not null)
            {
                warnings.Add($"Servern har fler än {MaxPages} sidor verktyg; resten togs inte med.");
            }
        }

        return new McpProbeResult(description.Outcome, description.Capabilities, description.Instructions, tools, warnings);
    }

    /// <summary>Why a server could not be used, for the admin (no content from the server).</summary>
    public static string Describe(McpUpstreamResult result) => result.Failure switch
    {
        McpUpstreamFailure.Timeout => "Servern svarade inte inom tidsgränsen.",
        McpUpstreamFailure.Unauthorized => $"Servern nekade gatewayens autentiseringsuppgifter (HTTP {result.HttpStatus}).",
        McpUpstreamFailure.ServerError => $"Servern svarade med ett serverfel (HTTP {result.HttpStatus}).",
        McpUpstreamFailure.Rejected when result.HttpStatus is { } status => $"Servern avvisade anropet (HTTP {status}). Kontrollera adressen.",
        McpUpstreamFailure.Rejected => "Adressen är inte tillåten i den här miljön.",
        McpUpstreamFailure.TooLarge => "Serverns verktygslista är större än serverns resultatgräns.",
        McpUpstreamFailure.JsonRpcError => $"Servern svarade med JSON-RPC-fel {result.ErrorCode}.",
        McpUpstreamFailure.TokenEndpoint when result.HttpStatus is { } status =>
            $"Tokenslutpunkten gav ingen åtkomsttoken (HTTP {status}). Kontrollera klient-ID, hemlighet och scope.",
        McpUpstreamFailure.TokenEndpoint => "Tokenslutpunkten kunde inte nås.",
        _ => "Servern kunde inte nås eller svarade inte med MCP.",
    };

    public static McpServerCapabilities[] Flags(McpServerCapabilities value) =>
        [.. Enum.GetValues<McpServerCapabilities>().Where(c => c != McpServerCapabilities.None && value.HasFlag(c))];
}

/// <summary>
/// Tool pinning (see <see cref="McpToolSync"/>): reads a server's tools and applies them to its stored tools, records
/// the server's status, and tells the gateways to reload when something they use changed. One instance at a time
/// (<see cref="IMcpSyncLock"/>).
/// </summary>
public sealed class McpToolSyncService(
    AdminContext ctx, McpServerService servers, McpProbe probe, McpUrlGuard guard, IMcpSyncLock syncLock, ILoggerFactory loggers)
{
    public static readonly TimeSpan LockTime = TimeSpan.FromMinutes(5);

    /// <summary>The actor of scheduled syncs in the audit log.</summary>
    public static ClaimsPrincipal System { get; } = new(new ClaimsIdentity([new Claim("sub", "system:mcp-sync")], "system"));

    private readonly ILogger _security = SecurityEvents.CreateLogger(loggers);

    /// <summary>An admin's sync of one server; 409 while another sync runs.</summary>
    public async Task<McpSyncResultDto> SyncNowAsync(ClaimsPrincipal user, Guid id, CancellationToken ct)
    {
        _ = await servers.ManageableAsync(user, id, ct);
        var token = await syncLock.TryAcquireAsync(LockTime, ct)
            ?? throw new ApiFaultException(409, "En synkronisering av MCP-servrar pågår redan. Försök igen om en stund.");
        try
        {
            return await SyncAsync(id, user, audit: true, ct);
        }
        finally
        {
            await syncLock.ReleaseAsync(token, CancellationToken.None);
        }
    }

    /// <summary>
    /// Syncs one server; the caller holds the sync lock. A sync that changes nothing is audited only when
    /// <paramref name="audit"/> is set (an admin asked for it).
    /// </summary>
    public async Task<McpSyncResultDto> SyncAsync(Guid id, ClaimsPrincipal actor, bool audit, CancellationToken ct)
    {
        var server = await ctx.Db.McpServers.Include(s => s.Tools).SingleOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new ApiFaultException(404, "MCP-servern finns inte.");
        var capabilities = server.Capabilities;
        var instructions = server.Instructions;
        var (outcome, error) = await ProbeAsync(server, ct);
        McpToolSyncResult? tools = null;
        if (outcome is not null)
        {
            var elsewhere = (await ctx.Db.McpTools.Where(t => t.McpServerId != server.Id).Select(t => t.ExposedName).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
            tools = McpToolSync.Apply(server, outcome.Tools, elsewhere, ctx.Now);
            ctx.Db.McpTools.RemoveRange(tools.Removed);
            ctx.Db.McpTools.AddRange(tools.Added);
            List<string> problems = [.. outcome.Warnings, .. tools.Errors];
            server.Capabilities = outcome.Capabilities;
            server.Instructions = outcome.Instructions is { Length: > McpServer.MaxInstructionsLength } text ? text[..McpServer.MaxInstructionsLength] : outcome.Instructions;
            server.Status = problems.Count > 0 ? McpServerStatus.Degraded : McpServerStatus.Healthy;
            server.LastError = problems.Count > 0 ? Cap(string.Join(" ", problems)) : null;
        }
        else
        {
            server.Status = McpServerStatus.Unavailable;
            server.LastError = error;
        }

        server.LastCheckedAt = ctx.Now;
        var changed = tools?.ToolsChanged == true || server.Capabilities != capabilities || server.Instructions != instructions;
        var result = new McpSyncResultDto(
            server.Status, server.LastCheckedAt, server.LastError, Names(tools?.Added), tools?.Changed ?? [], tools?.Reverted ?? [],
            tools?.Updated ?? [], Names(tools?.Removed), tools?.Errors ?? []);
        if (audit || changed)
        {
            ctx.StageAudit(actor, AuditActions.Sync, AuditEntities.McpServer, server.Id, null, new McpSyncAudit(
                result.Status, result.LastError, McpProbe.Flags(server.Capabilities), result.Added, result.Changed, result.Reverted, result.Updated, result.Removed));
        }

        await ctx.Db.SaveChangesAsync(ct);
        if (changed)
        {
            await ctx.PublishAsync([InvalidationKind.Config], ct);
        }

        foreach (var tool in tools?.Added.Where(t => t.ApprovalState == McpToolApprovalState.Pending) ?? [])
        {
            SecurityEvents.McpToolQuarantined(_security, tool.Name, server.Slug, server.Id, "new");
        }

        foreach (var name in tools?.Changed ?? [])
        {
            SecurityEvents.McpToolQuarantined(_security, name, server.Slug, server.Id, "changed");
        }

        return result;
    }

    /// <summary>The server's tools now, or null and why not (also when its address or credential cannot be used).</summary>
    private async Task<(McpProbeResult? Outcome, string? Error)> ProbeAsync(McpServer server, CancellationToken ct)
    {
        if (servers.Auth(server) is not { } auth)
        {
            return (null, McpProbe.CredentialUnreadable);
        }

        if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var url))
        {
            return (null, "Adressen är ogiltig.");
        }

        if (await guard.CheckAsync(url, allowPrivate: true, ct) is { } refused)
        {
            return (null, refused);
        }

        var result = await probe.ProbeAsync(server, auth, ct);
        if (result.Outcome.Succeeded)
        {
            return (result, null);
        }

        if (result.Outcome.Failure == McpUpstreamFailure.Unauthorized
            || result.Outcome is { Failure: McpUpstreamFailure.TokenEndpoint, HttpStatus: >= 400 and < 500 })
        {
            SecurityEvents.McpServerAuthFailed(_security, server.Slug, server.Id, result.Outcome.HttpStatus);
        }

        return (null, McpProbe.Describe(result.Outcome));
    }

    private static List<string> Names(IEnumerable<McpTool>? tools) => [.. (tools ?? []).Select(t => t.Name).Order(StringComparer.Ordinal)];

    private static string Cap(string text) => text.Length > 1000 ? text[..997] + "..." : text;
}

/// <summary>Syncs every enabled MCP server at start and then every <see cref="AdminOptions.McpSyncMinutes"/>, on one instance at a time.</summary>
public sealed partial class McpSyncWorker(IServiceScopeFactory scopes, IMcpSyncLock syncLock, IOptions<AdminOptions> options, TimeProvider time, ILogger<McpSyncWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.McpSyncMinutes == 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.McpSyncMinutes), time);
        try
        {
            do
            {
                await SyncAllAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>Syncs every enabled server unless another instance is syncing; returns how many were synced.</summary>
    public async Task<int> SyncAllAsync(CancellationToken ct)
    {
        string? token;
        try
        {
            token = await syncLock.TryAcquireAsync(McpToolSyncService.LockTime, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSyncFailed(logger, ex.GetType().Name);
            return 0;
        }

        if (token is null)
        {
            return 0;
        }

        var synced = 0;
        try
        {
            List<Guid> ids;
            using (var scope = scopes.CreateScope())
            {
                ids = await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().McpServers.AsNoTracking()
                    .Where(s => s.IsEnabled).OrderBy(s => s.Slug).Select(s => s.Id).ToListAsync(ct);
            }

            foreach (var id in ids)
            {
                if (!await syncLock.RenewAsync(token, McpToolSyncService.LockTime, ct))
                {
                    LogLockLost(logger);
                    break;
                }

                // A scope per server: a failed save must not leave its changes in the next server's unit of work.
                using var scope = scopes.CreateScope();
                try
                {
                    await scope.ServiceProvider.GetRequiredService<McpToolSyncService>().SyncAsync(id, McpToolSyncService.System, audit: false, ct);
                    synced++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogServerSyncFailed(logger, id, ex.GetType().Name);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSyncFailed(logger, ex.GetType().Name);
        }
        finally
        {
            try
            {
                await syncLock.ReleaseAsync(token, CancellationToken.None);
            }
            catch (Exception ex)
            {
                LogSyncFailed(logger, ex.GetType().Name);
            }
        }

        return synced;
    }

    [LoggerMessage(LogLevel.Warning, "MCP tool sync failed: {ExceptionType}")]
    private static partial void LogSyncFailed(ILogger logger, string exceptionType);

    [LoggerMessage(LogLevel.Warning, "MCP tool sync of server {ServerId} failed: {ExceptionType}")]
    private static partial void LogServerSyncFailed(ILogger logger, Guid serverId, string exceptionType);

    [LoggerMessage(LogLevel.Warning, "MCP tool sync stopped: the sync lock expired and is held by another instance")]
    private static partial void LogLockLost(ILogger logger);
}
