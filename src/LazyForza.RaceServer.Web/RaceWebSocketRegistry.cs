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
        if (connections.TryGetValue(participantId, out var previous))
        {
            connections[participantId] = connection;
            await previous.CloseAsync("Replaced by a resumed connection", cancellationToken);
            previous.Dispose();
            return;
        }
        connections[participantId] = connection;
    }

    public void Unregister(Guid participantId, WebSocket socket)
    {
        if (!connections.TryGetValue(participantId, out var existing) || !ReferenceEquals(existing.Socket, socket)) return;
        if (connections.TryRemove(participantId, out var removed)) removed.Dispose();
    }

    public async Task SendAsync(Guid participantId, string message, CancellationToken cancellationToken)
    {
        if (!connections.TryGetValue(participantId, out var connection)) return;
        if (!await connection.SendAsync(message, cancellationToken))
        {
            if (connections.TryRemove(participantId, out var removed)) removed.Dispose();
        }
    }

    public async Task BroadcastAsync(string message, CancellationToken cancellationToken)
    {
        var sends = connections.Select(async pair =>
        {
            if (await pair.Value.SendAsync(message, cancellationToken)) return;
            if (connections.TryRemove(pair.Key, out var removed)) removed.Dispose();
        });
        await Task.WhenAll(sends);
    }

    private sealed class Connection(WebSocket socket) : IDisposable
    {
        private readonly SemaphoreSlim sendLock = new(1, 1);
        public WebSocket Socket { get; } = socket;

        public async Task<bool> SendAsync(string message, CancellationToken cancellationToken)
        {
            if (Socket.State != WebSocketState.Open) return false;
            var bytes = Encoding.UTF8.GetBytes(message);
            await sendLock.WaitAsync(cancellationToken);
            try
            {
                if (Socket.State != WebSocketState.Open) return false;
                await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
                return true;
            }
            catch (WebSocketException)
            {
                return false;
            }
            finally
            {
                sendLock.Release();
            }
        }

        public async Task CloseAsync(string description, CancellationToken cancellationToken)
        {
            if (Socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
            try
            {
                await Socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, description, cancellationToken);
            }
            catch (WebSocketException) { }
        }

        public void Dispose() => sendLock.Dispose();
    }
}
