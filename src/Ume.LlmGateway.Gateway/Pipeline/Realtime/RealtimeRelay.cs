using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ume.LlmGateway.Gateway.Pipeline.Realtime;

/// <summary>How a live session ended.</summary>
public enum RealtimeEndReason
{
    /// <summary>The client closed the session (or the provider did, normally).</summary>
    Closed = 0,

    /// <summary>The client's connection dropped without a close handshake.</summary>
    ClientLost = 1,

    /// <summary>The provider dropped the connection or closed it with an error status.</summary>
    ProviderFailed = 2,

    /// <summary>The gateway ended it: a budget ran out.</summary>
    BudgetExceeded = 3,

    /// <summary>The gateway ended it: the session reached its maximum length.</summary>
    TimeLimit = 4,

    /// <summary>The gateway ended it: the instance is shutting down.</summary>
    Shutdown = 5,
}

/// <summary>A reason for the gateway to end a running session, with the error event the client gets first.</summary>
public sealed record RealtimeStop(RealtimeEndReason Reason, string Code, string Message, WebSocketCloseStatus CloseStatus);

/// <summary>
/// Relays one live session between the client and the provider. Messages pass through as received; only the few
/// events that need it are read (see <see cref="RealtimeEvents"/>) or rewritten (<see cref="RealtimeEventPolicy"/>).
/// Audio is metered on the way, never stored. A monitor runs the periodic budget check and stops the session when
/// the budget or the session time runs out, or the instance shuts down.
/// </summary>
public sealed partial class RealtimeRelay(
    WebSocket client,
    WebSocket upstream,
    RealtimeEventPolicy policy,
    RealtimeMeter meter,
    int maxMessageBytes,
    ILogger logger) : IDisposable
{
    private const int InitialBufferBytes = 16 * 1024;
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _clientSend = new(1, 1);
    private readonly SemaphoreSlim _upstreamSend = new(1, 1);
    private int _clientCloseSent;
    private int _upstreamCloseSent;

    /// <summary>
    /// Runs the session until either side closes or <paramref name="check"/> (called every
    /// <paramref name="checkInterval"/>) returns a stop. <paramref name="stopping"/> ends it with a going-away close.
    /// </summary>
    public async Task<RealtimeEndReason> RunAsync(TimeSpan checkInterval, Func<CancellationToken, Task<RealtimeStop?>> check,
        CancellationToken stopping, CancellationToken aborted)
    {
        ArgumentNullException.ThrowIfNull(check);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        var clientPump = PumpClientAsync(cts.Token);
        var upstreamPump = PumpUpstreamAsync(cts.Token);
        var monitor = MonitorAsync(checkInterval, check, stopping, cts.Token);

        var first = await Task.WhenAny(clientPump, upstreamPump, monitor);
        var reason = await first;

        // Let the other side finish its close handshake (and the provider deliver its last usage events), then abort.
        Task[] rest = first == monitor ? [clientPump, upstreamPump] : [first == clientPump ? upstreamPump : clientPump];
        await Task.WhenAny(Task.WhenAll(rest), Task.Delay(CloseGrace, CancellationToken.None));
        await cts.CancelAsync();
        try
        {
            await Task.WhenAll(clientPump, upstreamPump, monitor);
        }
        catch (OperationCanceledException)
        {
        }

        return reason;
    }

    private async Task<RealtimeEndReason> PumpClientAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(InitialBufferBytes);
        var length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length && !Grow(ref buffer, length))
                {
                    await SendToClientAsync(RealtimeEvents.Error(GatewayErrorCodes.RequestTooLarge,
                        $"Händelsen är större än {maxMessageBytes / 1024} kB. Skicka ljudet i mindre bitar.", null), ct);
                    await CloseBothAsync(WebSocketCloseStatus.MessageTooBig, "message_too_large");
                    return RealtimeEndReason.Closed;
                }

                var received = await client.ReceiveAsync(buffer.AsMemory(length), ct);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    await CloseUpstreamAsync(client.CloseStatus ?? WebSocketCloseStatus.NormalClosure, client.CloseStatusDescription);
                    await CloseClientAsync(client.CloseStatus ?? WebSocketCloseStatus.NormalClosure, null);
                    return RealtimeEndReason.Closed;
                }

                length += received.Count;
                if (!received.EndOfMessage)
                {
                    continue;
                }

                await FromClientAsync(buffer.AsMemory(0, length), received.MessageType, ct);
                length = 0;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return RealtimeEndReason.Closed;
        }
        catch (WebSocketException)
        {
            await CloseUpstreamAsync(WebSocketCloseStatus.NormalClosure, null);
            return RealtimeEndReason.ClientLost;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task FromClientAsync(ReadOnlyMemory<byte> message, WebSocketMessageType type, CancellationToken ct)
    {
        if (type != WebSocketMessageType.Text)
        {
            // The protocol is JSON text; binary audio would bypass metering.
            await SendToClientAsync(RealtimeEvents.Error(GatewayErrorCodes.InvalidRequest,
                "Binära meddelanden stöds inte. Skicka ljud som base64 i input_audio_buffer.append.", null), ct);
            return;
        }

        var (kind, audio) = RealtimeEvents.InspectClient(message.Span);
        if (kind is RealtimeClientEvent.Other or RealtimeClientEvent.AudioAppend)
        {
            meter.AddAudio(audio);
            await SendToUpstreamAsync(message, ct);
            return;
        }

        JsonObject? evt;
        try
        {
            evt = JsonNode.Parse(message.Span) as JsonObject;
        }
        catch (JsonException)
        {
            evt = null;
        }

        if (evt is null)
        {
            await SendToUpstreamAsync(message, ct);
            return;
        }

        var (rejection, eventAudio) = policy.Apply(evt, kind);
        if (rejection is not null)
        {
            var eventId = evt["event_id"] is JsonValue id && id.TryGetValue<string>(out var s) ? s : null;
            await SendToClientAsync(RealtimeEvents.Error(rejection.Code, rejection.Message, eventId), ct);
            return;
        }

        meter.AddAudio(eventAudio);
        await SendToUpstreamAsync(JsonSerializer.SerializeToUtf8Bytes(evt), ct);
    }

    private async Task<RealtimeEndReason> PumpUpstreamAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(InitialBufferBytes);
        var length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length && !Grow(ref buffer, length))
                {
                    LogUpstreamMessageTooLarge(logger, maxMessageBytes);
                    await CloseBothAsync(WebSocketCloseStatus.InternalServerError, "provider_message_too_large");
                    return RealtimeEndReason.ProviderFailed;
                }

                var received = await upstream.ReceiveAsync(buffer.AsMemory(length), ct);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    var status = upstream.CloseStatus ?? WebSocketCloseStatus.NormalClosure;
                    await CloseClientAsync(status, upstream.CloseStatusDescription);
                    await CloseUpstreamAsync(status, null);
                    return status is WebSocketCloseStatus.NormalClosure or WebSocketCloseStatus.EndpointUnavailable
                        ? RealtimeEndReason.Closed
                        : RealtimeEndReason.ProviderFailed;
                }

                length += received.Count;
                if (!received.EndOfMessage)
                {
                    continue;
                }

                var message = buffer.AsMemory(0, length);
                await SendToClientAsync(message, received.MessageType, ct);
                if (received.MessageType == WebSocketMessageType.Text)
                {
                    Meter(message.Span);
                }

                length = 0;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return RealtimeEndReason.Closed;
        }
        catch (WebSocketException ex)
        {
            LogUpstreamLost(logger, ex.WebSocketErrorCode.ToString());
            await SendToClientAsync(RealtimeEvents.Error("provider_disconnected", "Anslutningen till leverantören bröts.", null, "server_error"), CancellationToken.None);
            await CloseClientAsync(WebSocketCloseStatus.EndpointUnavailable, "provider_disconnected");
            return RealtimeEndReason.ProviderFailed;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Reads the input audio format and reported usage. Runs after the event was passed on, off the latency path.</summary>
    private void Meter(ReadOnlySpan<byte> message)
    {
        var kind = RealtimeEvents.InspectServer(message);
        if (kind == RealtimeServerEvent.AudioDelta || (kind == RealtimeServerEvent.Other && message.IndexOf("\"usage\""u8) < 0))
        {
            return;
        }

        JsonObject? evt;
        try
        {
            evt = JsonNode.Parse(message) as JsonObject;
        }
        catch (JsonException)
        {
            return;
        }

        if (evt is null)
        {
            return;
        }

        if (kind == RealtimeServerEvent.SessionState && RealtimeEvents.InputBytesPerSecond(evt["session"] as JsonObject) is { } rate)
        {
            meter.SetInputBytesPerSecond(rate);
        }

        if (RealtimeEvents.TryReadUsage(evt, out var usage))
        {
            meter.Add(usage, kind == RealtimeServerEvent.InputTranscriptionCompleted && policy.TranscriptionDeployment is not null);
        }
    }

    private async Task<RealtimeEndReason> MonitorAsync(TimeSpan interval, Func<CancellationToken, Task<RealtimeStop?>> check, CancellationToken stopping, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping);
        using var timer = new PeriodicTimer(interval);
        RealtimeStop? stop = null;
        try
        {
            while (stop is null && await timer.WaitForNextTickAsync(linked.Token))
            {
                stop = await check(linked.Token);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            stop = new RealtimeStop(RealtimeEndReason.Shutdown, "gateway_shutting_down",
                "Gatewayen startas om. Anslut igen för att fortsätta.", WebSocketCloseStatus.EndpointUnavailable);
        }
        catch (OperationCanceledException)
        {
            return RealtimeEndReason.Closed; // the session ended on its own
        }

        if (stop is null)
        {
            return RealtimeEndReason.Closed;
        }

        await SendToClientAsync(RealtimeEvents.Error(stop.Code, stop.Message, null,
            stop.Reason == RealtimeEndReason.BudgetExceeded ? "insufficient_quota" : "invalid_request_error"), CancellationToken.None);
        await CloseBothAsync(stop.CloseStatus, stop.Code);
        return stop.Reason;
    }

    private bool Grow(ref byte[] buffer, int length)
    {
        if (buffer.Length >= maxMessageBytes)
        {
            return false;
        }

        var larger = ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length * 2, maxMessageBytes));
        buffer.AsSpan(0, length).CopyTo(larger);
        ArrayPool<byte>.Shared.Return(buffer);
        buffer = larger;
        return true;
    }

    private Task SendToClientAsync(ReadOnlyMemory<byte> message, CancellationToken ct) => SendToClientAsync(message, WebSocketMessageType.Text, ct);

    private async Task SendToClientAsync(ReadOnlyMemory<byte> message, WebSocketMessageType type, CancellationToken ct)
    {
        await _clientSend.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref _clientCloseSent) == 0 && client.State == WebSocketState.Open)
            {
                await client.SendAsync(message, type, endOfMessage: true, ct);
            }
        }
        catch (WebSocketException)
        {
            // The client is gone; its pump notices and ends the session.
        }
        finally
        {
            _clientSend.Release();
        }
    }

    private async Task SendToUpstreamAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        await _upstreamSend.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref _upstreamCloseSent) == 0 && upstream.State == WebSocketState.Open)
            {
                await upstream.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, ct);
            }
        }
        catch (WebSocketException)
        {
            // The provider is gone; its pump notices and ends the session.
        }
        finally
        {
            _upstreamSend.Release();
        }
    }

    private async Task CloseBothAsync(WebSocketCloseStatus status, string description)
    {
        await CloseUpstreamAsync(status == WebSocketCloseStatus.MessageTooBig ? WebSocketCloseStatus.NormalClosure : status, null);
        await CloseClientAsync(status, description);
    }

    private Task CloseClientAsync(WebSocketCloseStatus status, string? description) =>
        CloseOnceAsync(client, _clientSend, ref _clientCloseSent, status, description);

    private Task CloseUpstreamAsync(WebSocketCloseStatus status, string? description) =>
        CloseOnceAsync(upstream, _upstreamSend, ref _upstreamCloseSent, status, description);

    private static Task CloseOnceAsync(WebSocket socket, SemaphoreSlim gate, ref int sent, WebSocketCloseStatus status, string? description) =>
        Interlocked.Exchange(ref sent, 1) == 1 ? Task.CompletedTask : CloseAsync(socket, gate, status, description);

    private static async Task CloseAsync(WebSocket socket, SemaphoreSlim gate, WebSocketCloseStatus status, string? description)
    {
        using var timeout = new CancellationTokenSource(CloseGrace);
        try
        {
            await gate.WaitAsync(timeout.Token);
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    // Only our half: the receiving pump reads the other side's close frame.
                    await socket.CloseOutputAsync(status, description is { Length: > 120 } ? description[..120] : description, timeout.Token);
                }
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        _clientSend.Dispose();
        _upstreamSend.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Realtime provider connection lost ({Error})")]
    private static partial void LogUpstreamLost(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Realtime provider sent a message larger than {MaxBytes} bytes; session closed")]
    private static partial void LogUpstreamMessageTooLarge(ILogger logger, int maxBytes);
}
