using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace LazyForza.RaceServer.Web;

public sealed class RaceWebSocketRegistry
{
    private readonly ConcurrentDictionary<Guid, Connection> connections = new();

    public int Count => connections.Count;

    public async Task RegisterAsync(Guid participantId, WebSocket socket, CancellationToken cancellationToken)
    {
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
        if (previous is not null)
            await previous.CloseAsync("Replaced by a resumed connection", cancellationToken);
    }

    public bool Unregister(Guid participantId, WebSocket socket)
    {
        if (!connections.TryGetValue(participantId, out var existing) || !ReferenceEquals(existing.Socket, socket))
            return false;
        return RemoveIfCurrent(participantId, existing);
    }

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

    public async Task<IReadOnlyList<Guid>> BroadcastAsync(
        ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken)
    {
        var disconnected = new ConcurrentQueue<Guid>();
        var sends = connections.Select(async pair =>
        {
            try
            {
                if (await pair.Value.SendAsync(message, cancellationToken)) return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // A single broken client must never terminate the room-wide broadcast loop.
            }
            if (RemoveIfCurrent(pair.Key, pair.Value)) disconnected.Enqueue(pair.Key);
        });
        await Task.WhenAll(sends);
        return disconnected.ToArray();
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

    private bool RemoveIfCurrent(Guid participantId, Connection expected) =>
        ((ICollection<KeyValuePair<Guid, Connection>>)connections).Remove(
            new KeyValuePair<Guid, Connection>(participantId, expected));

    private sealed class Connection(WebSocket socket)
    {
        private static readonly TimeSpan SendTimeout = TimeSpan.FromMilliseconds(750);
        private readonly SemaphoreSlim sendLock = new(1, 1);
        public WebSocket Socket { get; } = socket;

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
                return false;
            }
            finally
            {
                if (lockTaken) sendLock.Release();
            }
        }

        public async Task CloseAsync(string description, CancellationToken cancellationToken)
        {
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
            try { Socket.Abort(); }
            catch { }
        }
    }
}
