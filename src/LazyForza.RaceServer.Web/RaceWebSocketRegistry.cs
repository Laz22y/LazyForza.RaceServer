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

    public async Task SendAsync(Guid participantId, string message, CancellationToken cancellationToken)
    {
        if (!connections.TryGetValue(participantId, out var connection)) return;
        if (!await connection.SendAsync(message, cancellationToken))
            RemoveIfCurrent(participantId, connection);
    }

    public async Task BroadcastAsync(string message, CancellationToken cancellationToken)
    {
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
            RemoveIfCurrent(pair.Key, pair.Value);
        });
        await Task.WhenAll(sends);
    }

    private bool RemoveIfCurrent(Guid participantId, Connection expected) =>
        ((ICollection<KeyValuePair<Guid, Connection>>)connections).Remove(
            new KeyValuePair<Guid, Connection>(participantId, expected));

    private sealed class Connection(WebSocket socket)
    {
        private readonly SemaphoreSlim sendLock = new(1, 1);
        public WebSocket Socket { get; } = socket;

        public async Task<bool> SendAsync(string message, CancellationToken cancellationToken)
        {
            if (Socket.State != WebSocketState.Open) return false;
            var bytes = Encoding.UTF8.GetBytes(message);
            var lockTaken = false;
            try
            {
                await sendLock.WaitAsync(cancellationToken);
                lockTaken = true;
                if (Socket.State != WebSocketState.Open) return false;
                await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
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
    }
}
