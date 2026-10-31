using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Gateway.Realtime;
using Ume.LlmGateway.Gateway.Requests;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>The relay on its own, over real WebSockets, where the order of the two sides' frames can be forced.</summary>
public sealed class RealtimeRelayTests
{
    [Fact]
    public async Task A_stop_closes_the_client_with_its_own_status_when_the_providers_answer_comes_first()
    {
        var ct = TestContext.Current.CancellationToken;
        using var clientPair = await SocketPair.OpenAsync(ct);
        using var upstreamPair = await SocketPair.OpenAsync(ct);
        // The provider answers the gateway's close with 1000 at once, and the gateway's close to it returns only after the
        // relay has read that answer: the order that used to give the client the provider's 1000.
        using var upstream = new AnswerFirstWebSocket(upstreamPair.Client);
        var session = new ModelDeployment { Name = "p/realtime", UpstreamModel = "gpt-realtime", Kind = ModelKind.Realtime };
        using var relay = new RealtimeRelay(clientPair.Server, upstream, new RealtimeEventPolicy(session, _ => null, PiiPolicy.Off, onPrem: false),
            new RealtimeMeter(), 64 * 1024, NullLogger.Instance);

        var provider = AnswerCloseAsync(upstreamPair.Server, ct);
        var client = ReceiveCloseAsync(clientPair.Client, ct);
        var stop = new RealtimeStop(RealtimeEndReason.BudgetExceeded, new GatewayError(402, "budget_exceeded", "The budget is used up."),
            WebSocketCloseStatus.PolicyViolation);
        var reason = await relay.RunAsync(TimeSpan.FromMilliseconds(20), _ => Task.FromResult<RealtimeStop?>(stop), CancellationToken.None, ct);

        (await client).ShouldBe(WebSocketCloseStatus.PolicyViolation);
        reason.ShouldBe(RealtimeEndReason.BudgetExceeded);
        await provider;
    }

    private static async Task AnswerCloseAsync(WebSocket provider, CancellationToken ct)
    {
        var buffer = new byte[4096];
        while ((await provider.ReceiveAsync(buffer, ct)).MessageType != WebSocketMessageType.Close)
        {
        }

        await provider.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
    }

    private static async Task<WebSocketCloseStatus?> ReceiveCloseAsync(WebSocket client, CancellationToken ct)
    {
        var buffer = new byte[4096];
        while ((await client.ReceiveAsync(buffer, ct)).MessageType != WebSocketMessageType.Close)
        {
        }

        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
        return client.CloseStatus;
    }

    /// <summary>Two connected WebSockets over a loopback TCP connection.</summary>
    private sealed class SocketPair : IDisposable
    {
        private readonly TcpClient _client;
        private readonly TcpClient _server;

        private SocketPair(TcpClient client, TcpClient server)
        {
            (_client, _server) = (client, server);
            Client = WebSocket.CreateFromStream(client.GetStream(), new WebSocketCreationOptions { IsServer = false });
            Server = WebSocket.CreateFromStream(server.GetStream(), new WebSocketCreationOptions { IsServer = true });
        }

        public WebSocket Client { get; }
        public WebSocket Server { get; }

        public static async Task<SocketPair> OpenAsync(CancellationToken ct)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            var connect = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct);
            var server = await listener.AcceptTcpClientAsync(ct);
            await connect;
            return new SocketPair(client, server);
        }

        public void Dispose()
        {
            Client.Dispose();
            Server.Dispose();
            _client.Dispose();
            _server.Dispose();
        }
    }

    /// <summary>Holds back the return of the gateway's close until the provider's answering close has been read.</summary>
    private sealed class AnswerFirstWebSocket(WebSocket inner) : WebSocket
    {
        private readonly TaskCompletionSource _answerRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override WebSocketCloseStatus? CloseStatus => inner.CloseStatus;
        public override string? CloseStatusDescription => inner.CloseStatusDescription;
        public override WebSocketState State => inner.State;
        public override string? SubProtocol => inner.SubProtocol;

        public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            await inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
            await _answerRead.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await Task.Delay(200, cancellationToken); // time for the relay to act on the answer
        }

        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var result = await inner.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                _answerRead.TrySetResult();
            }

            return result;
        }

        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken);

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Abort() => inner.Abort();

        public override void Dispose() => inner.Dispose();
    }
}
