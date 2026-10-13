using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>
/// <c>subscriptions/listen</c> (<c>2026-07-28</c>): a long-lived SSE answer that tells the client when the tools, prompts
/// or resources it may use change. The gateway knows only its own configuration, so it compares what the key may see
/// before and after each configuration change (and every <see cref="McpOptions.ListenCheckSeconds"/>); a change the
/// servers make to their own prompt or resource lists is not seen. Streams count against a per-key cap shared by all
/// instances, and end when the key stops being usable or the gateway shuts down.
/// </summary>
public sealed partial class GatewayRequestHandler
{
    private const string McpSubscriptionMeta = "io.modelcontextprotocol/subscriptionId";

    private async Task ListenAsync(McpResponse response, VirtualKey key, string presentedKey, McpServer? only, JsonNode id, JsonObject? parameters, CancellationToken ct)
    {
        var filter = parameters?["notifications"] as JsonObject;
        var tools = Wants(filter, "toolsListChanged");
        var prompts = Wants(filter, "promptsListChanged");
        var resources = Wants(filter, "resourcesListChanged");
        var settings = options.CurrentValue.Mcp;
        var streamId = RequestIdMiddleware.Get(response.Http);
        if (!await TryOpenListenAsync(key.Id, streamId, settings, ct))
        {
            await response.ErrorAsync(id, McpErrors.RateLimited,
                $"Nyckeln har redan {settings.MaxListenStreamsPerKey.ToString(CultureInfo.InvariantCulture)} öppna MCP-prenumerationer. Stäng en och försök igen.",
                new JsonObject { ["code"] = "too_many_listen_streams" });
            return;
        }

        metrics.ListenStreamOpened();
        using var wake = new SemaphoreSlim(0);
        using var subscription = invalidation.Subscribe(_ =>
        {
            try
            {
                wake.Release();
            }
            catch (ObjectDisposedException)
            {
                // The stream has just ended.
            }
        });
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.ApplicationStopping);
        try
        {
            var acknowledged = new JsonObject();
            foreach (var (name, wanted) in (List<(string, bool)>)[("toolsListChanged", tools), ("promptsListChanged", prompts), ("resourcesListChanged", resources)])
            {
                if (wanted)
                {
                    acknowledged[name] = true;
                }
            }

            // Taken before the acknowledgement, so a change the client makes right after it is not missed.
            var seen = Fingerprints(await catalog.GetAsync(ct), key, only);
            await response.NotifyAsync("notifications/subscriptions/acknowledged",
                new JsonObject { ["_meta"] = SubscriptionMeta(id), ["notifications"] = acknowledged }, ct);
            while (true)
            {
                var signalled = await wake.WaitAsync(TimeSpan.FromSeconds(settings.ListenCheckSeconds), stopping.Token);
                await RenewListenAsync(key.Id, streamId, settings);
                var current = await keys.FindAsync(presentedKey, stopping.Token);
                if (current is null || !current.IsUsable(time.GetUtcNow()) || current.Team is not { IsActive: true } || current.Team.Department is not { IsActive: true })
                {
                    await EndListenAsync(response, id);
                    return;
                }

                var now = Fingerprints(await catalog.GetAsync(stopping.Token), current, only);
                foreach (var (method, wanted, before, after) in (List<(string, bool, string, string)>)
                [
                    ("notifications/tools/list_changed", tools, seen.Tools, now.Tools),
                    ("notifications/prompts/list_changed", prompts, seen.Prompts, now.Prompts),
                    ("notifications/resources/list_changed", resources, seen.Resources, now.Resources),
                ])
                {
                    if (wanted && before != after)
                    {
                        await response.NotifyAsync(method, new JsonObject { ["_meta"] = SubscriptionMeta(id) }, stopping.Token);
                    }
                }

                seen = now;
                if (!signalled)
                {
                    await response.KeepAliveAsync(stopping.Token);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client closed the stream.
        }
        catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
        {
            await EndListenAsync(response, id);
        }
        catch (IOException)
        {
            // The connection broke while writing.
        }
        finally
        {
            metrics.ListenStreamClosed();
            await CloseListenAsync(key.Id, streamId);
        }
    }

    private static bool Wants(JsonObject? filter, string name) => filter?[name] is JsonValue value && value.TryGetValue<bool>(out var wanted) && wanted;

    private static JsonObject SubscriptionMeta(JsonNode id) => new() { [McpSubscriptionMeta] = id.DeepClone() };

    /// <summary>A graceful end: the response to the <c>subscriptions/listen</c> request.</summary>
    private static Task EndListenAsync(McpResponse response, JsonNode id) =>
        response.ResultAsync(id, w =>
        {
            w.WriteStartObject();
            w.WriteString("resultType", "complete");
            w.WriteStartObject("_meta");
            w.WritePropertyName(McpSubscriptionMeta);
            id.WriteTo(w);
            w.WriteEndObject();
            w.WriteEndObject();
        }, CancellationToken.None);

    /// <summary>
    /// What the key may see, one string per kind: its tools (names and approved definitions), and the servers whose
    /// prompts and resources it may use. Circuits are left out: they open and close too often to announce.
    /// </summary>
    private static (string Tools, string Prompts, string Resources) Fingerprints(CatalogSnapshot snapshot, VirtualKey key, McpServer? only)
    {
        var servers = VisibleServers(snapshot, key, only);
        var tools = new StringBuilder();
        foreach (var entry in VisibleTools(snapshot, key, servers, new HashSet<Guid>()).OrderBy(t => t.Tool.Id))
        {
            tools.Append(entry.Tool.ExposedName).Append('\n').Append(entry.Tool.Title).Append('\n').Append(entry.Tool.ApprovedDefinition).Append('\n');
        }

        static string Servers(IEnumerable<McpServer> list) => string.Join(',', list.Select(s => s.Id.ToString("N") + s.Slug));
        return (tools.ToString(),
            Servers(servers.Where(s => McpAccess.CheckPrompts(key, s.Server, s.Grant, circuitOpen: false) == McpDenial.None).Select(s => s.Server)),
            Servers(servers.Where(s => McpAccess.CheckResources(key, s.Server, s.Grant, circuitOpen: false) == McpDenial.None).Select(s => s.Server)));
    }

    private async Task<bool> TryOpenListenAsync(Guid keyId, string streamId, McpOptions settings, CancellationToken ct)
    {
        if (settings.MaxListenStreamsPerKey <= 0)
        {
            return true;
        }

        try
        {
            return await listens.TryOpenAsync(keyId, streamId, settings.MaxListenStreamsPerKey, settings.ListenSlotLifetime, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogListenRegistryFailed(logger, ex, streamId);
            return true;
        }
    }

    private async Task RenewListenAsync(Guid keyId, string streamId, McpOptions settings)
    {
        try
        {
            await listens.RenewAsync(keyId, streamId, settings.ListenSlotLifetime, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogListenRegistryFailed(logger, ex, streamId);
        }
    }

    private async Task CloseListenAsync(Guid keyId, string streamId)
    {
        try
        {
            await listens.CloseAsync(keyId, streamId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogListenRegistryFailed(logger, ex, streamId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP listen stream registry unavailable for {RequestId}; the per-key stream limit is not enforced")]
    private static partial void LogListenRegistryFailed(ILogger logger, Exception ex, string requestId);
}
