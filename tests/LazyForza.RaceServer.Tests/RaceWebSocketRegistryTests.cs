using System.Net.WebSockets;
using System.Text;
using LazyForza.RaceServer.Web;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LazyForza.RaceServer.Tests;

[TestClass]
public sealed class RaceWebSocketRegistryTests
{
    [TestMethod]
    public async Task BrokenClientCannotStopBroadcastToHealthyClients()
    {
        var registry = new RaceWebSocketRegistry();
        var broken = new TestWebSocket { SendFailure = new InvalidOperationException("simulated send failure") };
        var healthy = new TestWebSocket();
        await registry.RegisterAsync(Guid.NewGuid(), broken, CancellationToken.None);
        await registry.RegisterAsync(Guid.NewGuid(), healthy, CancellationToken.None);

        await registry.BroadcastAsync("first", CancellationToken.None);
        await registry.BroadcastAsync("second", CancellationToken.None);

        Assert.AreEqual(1, registry.Count);
        CollectionAssert.AreEqual(new[] { "first", "second" }, healthy.Messages.ToArray());
    }

    [TestMethod]
    public async Task UnregisterDuringAnActiveSendCannotFaultTheBroadcast()
    {
        var registry = new RaceWebSocketRegistry();
        var socket = new TestWebSocket { BlockSend = true };
        var participantId = Guid.NewGuid();
        await registry.RegisterAsync(participantId, socket, CancellationToken.None);

        var broadcast = registry.BroadcastAsync("snapshot", CancellationToken.None);
        await socket.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(registry.Unregister(participantId, socket));
        socket.AllowSend.TrySetResult(true);

        await broadcast.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(0, registry.Count);
    }

    [TestMethod]
    public async Task FailedOldSendCannotRemoveAResumedConnection()
    {
        var registry = new RaceWebSocketRegistry();
        var participantId = Guid.NewGuid();
        var oldSocket = new TestWebSocket
        {
            BlockSend = true,
            SendFailure = new InvalidOperationException("old socket failed")
        };
        var resumedSocket = new TestWebSocket();
        await registry.RegisterAsync(participantId, oldSocket, CancellationToken.None);

        var oldBroadcast = registry.BroadcastAsync("old", CancellationToken.None);
        await oldSocket.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await registry.RegisterAsync(participantId, resumedSocket, CancellationToken.None);
        oldSocket.AllowSend.TrySetResult(true);
        await oldBroadcast.WaitAsync(TimeSpan.FromSeconds(2));

        await registry.BroadcastAsync("resumed", CancellationToken.None);
        Assert.AreEqual(1, registry.Count);
        CollectionAssert.AreEqual(new[] { "resumed" }, resumedSocket.Messages.ToArray());
    }

    private sealed class TestWebSocket : WebSocket
    {
        private WebSocketState state = WebSocketState.Open;

        public Exception? SendFailure { get; init; }
        public bool BlockSend { get; init; }
        public List<string> Messages { get; } = [];
        public TaskCompletionSource<bool> SendStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> AllowSend { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;

        public override void Abort() => state = WebSocketState.Aborted;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose() => state = WebSocketState.Closed;

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken) =>
            Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));

        public override async Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            SendStarted.TrySetResult(true);
            if (BlockSend) await AllowSend.Task.WaitAsync(cancellationToken);
            if (SendFailure is not null) throw SendFailure;
            Messages.Add(Encoding.UTF8.GetString(buffer));
        }
    }
}
