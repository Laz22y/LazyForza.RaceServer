using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace LazyForza.RaceServer.Web;

public sealed class RaceWebSocketRegistry
{
    private readonly ConcurrentDictionary<Guid, Connection> connections = new();

    public int Count => connections.Count;

    public Task RegisterAsync(Guid participantId, WebSocket socket, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var connection = new Connection(socket);
        Connection? previous = null;
        connections.AddOrUpdate(
            participantId,
            connection,
            (_, existing) =>
            {
                previous = existing;
                return connection;
            });
        previous?.Abort();
        return Task.CompletedTask;
    }

    public bool Unregister(Guid participantId, WebSocket socket)
    {
        if (!connections.TryGetValue(participantId, out var existing) || !ReferenceEquals(existing.Socket, socket))
            return false;
        return RemoveIfCurrent(participantId, existing);
    }

    public bool IsCurrent(Guid participantId, WebSocket socket) =>
        connections.TryGetValue(participantId, out var connection) &&
        ReferenceEquals(connection.Socket, socket);

    public Task<bool> SendAsync(
        Guid participantId,
        WebSocket expectedSocket,
        string message,
        CancellationToken cancellationToken) =>
        SendAsync(participantId, expectedSocket, Encoding.UTF8.GetBytes(message), cancellationToken);

    public async Task<bool> SendAsync(
        Guid participantId,
        WebSocket expectedSocket,
        ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken)
    {
        if (!connections.TryGetValue(participantId, out var connection) ||
            !ReferenceEquals(connection.Socket, expectedSocket))
            return false;
        return await connection.SendAsync(message, cancellationToken);
    }

    public Task<IReadOnlyList<Guid>> BroadcastAsync(string message, CancellationToken cancellationToken) =>
        BroadcastAsync(Encoding.UTF8.GetBytes(message), cancellationToken);

    public Task<IReadOnlyList<Guid>> BroadcastAsync(
        ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var disconnected = new List<Guid>();
        foreach (var pair in connections)
        {
            if (pair.Value.TryQueueBroadcast(message)) continue;
            if (RemoveIfCurrent(pair.Key, pair.Value)) disconnected.Add(pair.Key);
        }
        return Task.FromResult<IReadOnlyList<Guid>>(disconnected);
    }

    public async Task<bool> DisconnectAsync(
        Guid clientId,
        string description,
        CancellationToken cancellationToken)
    {
        if (!connections.TryRemove(clientId, out var connection)) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try { await connection.CloseAsync(description, timeout.Token); }
        catch (OperationCanceledException) { connection.Abort(); }
        return true;
    }

    private bool RemoveIfCurrent(Guid participantId, Connection expected)
    {
        var removed = ((ICollection<KeyValuePair<Guid, Connection>>)connections).Remove(
            new KeyValuePair<Guid, Connection>(participantId, expected));
        if (removed) expected.Complete();
        return removed;
    }

    private sealed class Connection
    {
        private static readonly TimeSpan SendTimeout = TimeSpan.FromMilliseconds(750);
        private readonly SemaphoreSlim sendLock = new(1, 1);
        private readonly Channel<ReadOnlyMemory<byte>> broadcasts = Channel.CreateBounded<ReadOnlyMemory<byte>>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

        public Connection(WebSocket socket)
        {
            Socket = socket;
            _ = Task.Run(BroadcastLoopAsync);
        }

        public WebSocket Socket { get; }

        public bool TryQueueBroadcast(ReadOnlyMemory<byte> message) =>
            Socket.State == WebSocketState.Open && broadcasts.Writer.TryWrite(message);

        public async Task<bool> SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
        {
            if (Socket.State != WebSocketState.Open) return false;
            var lockTaken = false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SendTimeout);
            try
            {
                await sendLock.WaitAsync(timeout.Token);
                lockTaken = true;
                if (Socket.State != WebSocketState.Open) return false;
                await Socket.SendAsync(message, WebSocketMessageType.Text, true, timeout.Token);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                Abort();
                return false;
            }
            catch
            {
                Abort();
                return false;
            }
            finally
            {
                if (lockTaken) sendLock.Release();
            }
        }

        public async Task CloseAsync(string description, CancellationToken cancellationToken)
        {
            broadcasts.Writer.TryComplete();
            if (Socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
            try
            {
                await Socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, description, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { }
        }

        public void Abort()
        {
            broadcasts.Writer.TryComplete();
            try { Socket.Abort(); }
            catch { }
        }

        public void Complete() => broadcasts.Writer.TryComplete();

        private async Task BroadcastLoopAsync()
        {
            try
            {
                while (await broadcasts.Reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    if (!broadcasts.Reader.TryRead(out var message)) continue;
                    while (broadcasts.Reader.TryRead(out var newer)) message = newer;
                    if (!await SendAsync(message, CancellationToken.None).ConfigureAwait(false)) return;
                }
            }
            catch
            {
                Abort();
            }
        }
    }
}
