using System.Collections.Concurrent;
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
        await WaitUntilAsync(() => broken.State == WebSocketState.Aborted);
        await registry.BroadcastAsync("second", CancellationToken.None);
        await WaitUntilAsync(() => healthy.Messages.Count == 2);

        Assert.AreEqual(1, registry.Count);
        CollectionAssert.AreEqual(new[] { "first", "second" }, healthy.Messages.ToArray());
    }

    [TestMethod]
    public async Task StalledClientIsTimedOutWithoutHoldingTheRoomBroadcastLoop()
    {
        var registry = new RaceWebSocketRegistry();
        var stalledId = Guid.NewGuid();
        var stalled = new TestWebSocket { BlockSend = true };
        var healthy = new TestWebSocket();
        await registry.RegisterAsync(stalledId, stalled, CancellationToken.None);
        await registry.RegisterAsync(Guid.NewGuid(), healthy, CancellationToken.None);

        var broadcast = registry.BroadcastAsync("snapshot", CancellationToken.None);
        Assert.AreSame(
            broadcast,
            await Task.WhenAny(broadcast, Task.Delay(200)),
            "弱网连接不能阻塞整场广播队列。 ");
        await WaitUntilAsync(() => stalled.State == WebSocketState.Aborted);
        var disconnected = await registry.BroadcastAsync("after-timeout", CancellationToken.None);
        await WaitUntilAsync(() => healthy.Messages.Count == 2);

        Assert.AreEqual(1, registry.Count);
        CollectionAssert.Contains(disconnected.ToArray(), stalledId);
        Assert.AreEqual(WebSocketState.Aborted, stalled.State);
        CollectionAssert.AreEqual(new[] { "snapshot", "after-timeout" }, healthy.Messages.ToArray());
    }

    [TestMethod]
    public async Task RegisteredRepliesShareTheSameSendLockAsRoomBroadcasts()
    {
        var registry = new RaceWebSocketRegistry();
        var participantId = Guid.NewGuid();
        var socket = new TestWebSocket { BlockSend = true };
        await registry.RegisterAsync(participantId, socket, CancellationToken.None);

        var broadcast = registry.BroadcastAsync("snapshot", CancellationToken.None);
        await socket.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var direct = registry.SendAsync(participantId, socket, "pong", CancellationToken.None);
        await Task.Delay(50);
        Assert.IsFalse(direct.IsCompleted, "单播回复必须等待同一连接上的广播发送完成。 ");

        socket.AllowSend.TrySetResult(true);
        await broadcast.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(await direct.WaitAsync(TimeSpan.FromSeconds(2)));
        await WaitUntilAsync(() => socket.Messages.Count == 2);
        CollectionAssert.AreEqual(new[] { "snapshot", "pong" }, socket.Messages.ToArray());
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
        Assert.AreEqual(WebSocketState.Aborted, oldSocket.State);
        Assert.IsFalse(registry.IsCurrent(participantId, oldSocket));
        Assert.IsTrue(registry.IsCurrent(participantId, resumedSocket));
        oldSocket.AllowSend.TrySetResult(true);
        await oldBroadcast.WaitAsync(TimeSpan.FromSeconds(2));

        await registry.BroadcastAsync("resumed", CancellationToken.None);
        await WaitUntilAsync(() => resumedSocket.Messages.Count == 1);
        Assert.AreEqual(1, registry.Count);
        CollectionAssert.AreEqual(new[] { "resumed" }, resumedSocket.Messages.ToArray());
    }

    [TestMethod]
    public async Task SlowClientKeepsOnlyLatestPendingSnapshot()
    {
        var registry = new RaceWebSocketRegistry();
        var slow = new TestWebSocket { BlockSend = true };
        await registry.RegisterAsync(Guid.NewGuid(), slow, CancellationToken.None);

        await registry.BroadcastAsync("first", CancellationToken.None);
        await slow.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await registry.BroadcastAsync("stale", CancellationToken.None);
        await registry.BroadcastAsync("latest", CancellationToken.None);
        slow.AllowSend.TrySetResult(true);
        await WaitUntilAsync(() => slow.Messages.Count == 2);

        CollectionAssert.AreEqual(new[] { "first", "latest" }, slow.Messages.ToArray());
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!predicate() && DateTimeOffset.UtcNow < timeout)
            await Task.Delay(10);
        Assert.IsTrue(predicate(), "等待 WebSocket 测试状态超时。 ");
    }

    private sealed class TestWebSocket : WebSocket
    {
        private WebSocketState state = WebSocketState.Open;

        public Exception? SendFailure { get; init; }
        public bool BlockSend { get; init; }
        public ConcurrentQueue<string> Messages { get; } = [];
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
            Messages.Enqueue(Encoding.UTF8.GetString(buffer));
        }
    }
}
