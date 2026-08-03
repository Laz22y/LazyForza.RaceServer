using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LazyForza.RaceServer.Core;
using LazyForza.RaceServer.Protocol;

namespace LazyForza.RaceServer.Web;

public sealed class RaceWebSocketHandler(
    RaceCoordinator coordinator,
    RaceWebSocketRegistry registry,
    RaceBroadcastService broadcasts,
    ILogger<RaceWebSocketHandler> logger)
{
    private long sequence;

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            await context.Response.WriteAsync("LazyForza race endpoint requires WebSocket upgrade.");
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        Guid? participantId = null;
        try
        {
            using var loginTimeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            loginTimeout.CancelAfter(TimeSpan.FromSeconds(12));
            var loginEnvelope = await ReceiveEnvelopeAsync(socket, loginTimeout.Token);
            if (loginEnvelope.ProtocolVersion != RaceProtocol.CurrentVersion || loginEnvelope.Type != RaceMessageTypes.Login)
            {
                await SendAsync(socket, RaceMessageTypes.LoginRejected,
                    new RaceLoginRejected("protocolMismatch", $"服务端协议版本为 {RaceProtocol.CurrentVersion}。"),
                    context.RequestAborted);
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Protocol mismatch", context.RequestAborted);
                return;
            }

            var login = RaceProtocolJson.DeserializePayload<RaceLoginRequest>(loginEnvelope);
            var result = coordinator.TryJoin(login);
            if (!result.IsAccepted)
            {
                await SendAsync(socket, RaceMessageTypes.LoginRejected, result.Rejected!, context.RequestAborted);
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, result.Rejected!.Code, context.RequestAborted);
                return;
            }

            participantId = result.Accepted!.ParticipantId;
            await registry.RegisterAsync(participantId.Value, socket, context.RequestAborted);
            await SendAsync(socket, RaceMessageTypes.LoginAccepted, result.Accepted, context.RequestAborted);
            broadcasts.Queue(result.Accepted.Snapshot);

            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                RaceEnvelope envelope;
                try
                {
                    envelope = await ReceiveEnvelopeAsync(socket, context.RequestAborted);
                }
                catch (WebSocketClosedException)
                {
                    break;
                }
                if (envelope.ProtocolVersion != RaceProtocol.CurrentVersion)
                {
                    await SendErrorAsync(socket, "protocolMismatch", "协议版本不一致。", context.RequestAborted);
                    continue;
                }

                var command = HandleMessage(participantId.Value, envelope, socket, context.RequestAborted);
                if (command is not null) await command;
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Race WebSocket login or message timed out from {RemoteAddress}.", context.Connection.RemoteIpAddress);
        }
        catch (JsonException exception)
        {
            logger.LogInformation(exception, "Race client sent malformed JSON from {RemoteAddress}.", context.Connection.RemoteIpAddress);
            if (socket.State == WebSocketState.Open)
                await SendErrorAsync(socket, "invalidMessage", "消息格式无效。", CancellationToken.None);
        }
        catch (WebSocketException exception)
        {
            logger.LogDebug(exception, "Race WebSocket closed unexpectedly.");
        }
        finally
        {
            if (participantId is Guid id)
            {
                registry.Unregister(id, socket);
                coordinator.Disconnect(id);
            }
        }
    }

    private Task? HandleMessage(
        Guid participantId,
        RaceEnvelope envelope,
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        switch (envelope.Type)
        {
            case RaceMessageTypes.Ready:
            {
                var update = RaceProtocolJson.DeserializePayload<RaceReadyUpdate>(envelope);
                return ReplyToResult(socket, coordinator.SetReady(participantId, update.IsReady), cancellationToken);
            }
            case RaceMessageTypes.Telemetry:
            {
                var update = RaceProtocolJson.DeserializePayload<RaceTelemetryUpdate>(envelope);
                var result = coordinator.UpdateTelemetry(participantId, update);
                return result.IsAccepted ? null : ReplyToResult(socket, result, cancellationToken);
            }
            case RaceMessageTypes.LapCompleted:
            {
                var completed = RaceProtocolJson.DeserializePayload<RaceLapCompleted>(envelope);
                return ReplyToResult(socket, coordinator.CompleteLap(participantId, completed), cancellationToken);
            }
            case RaceMessageTypes.Ping:
            {
                var ping = RaceProtocolJson.DeserializePayload<RaceClockPing>(envelope);
                return SendAsync(socket, RaceMessageTypes.Pong,
                    new RaceClockPong(ping.ClientMonotonicMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                    cancellationToken);
            }
            default:
                return SendErrorAsync(socket, "unsupportedMessage", $"不支持消息类型：{envelope.Type}", cancellationToken);
        }
    }

    private Task ReplyToResult(WebSocket socket, RaceCommandResult result, CancellationToken cancellationToken) =>
        result.IsAccepted
            ? Task.CompletedTask
            : SendErrorAsync(socket, "commandRejected", result.Error ?? "命令被拒绝。", cancellationToken);

    private Task SendErrorAsync(WebSocket socket, string code, string message, CancellationToken cancellationToken) =>
        SendAsync(socket, RaceMessageTypes.Error, new { code, message }, cancellationToken);

    private async Task SendAsync<T>(WebSocket socket, string type, T payload, CancellationToken cancellationToken)
    {
        var message = RaceProtocolJson.Serialize(type, Interlocked.Increment(ref sequence), payload);
        var bytes = Encoding.UTF8.GetBytes(message);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<RaceEnvelope> ReceiveEnvelopeAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var writer = new ArrayBufferWriter<byte>();
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            while (true)
            {
                var received = await socket.ReceiveAsync(buffer, cancellationToken);
                if (received.MessageType == WebSocketMessageType.Close) throw new WebSocketClosedException();
                if (received.MessageType != WebSocketMessageType.Text)
                    throw new JsonException("Only text WebSocket messages are supported.");
                if (writer.WrittenCount + received.Count > RaceProtocol.MaximumMessageBytes)
                    throw new JsonException("Race message exceeds the maximum size.");
                writer.Write(buffer.AsSpan(0, received.Count));
                if (received.EndOfMessage) break;
            }
            return RaceProtocolJson.DeserializeEnvelope(writer.WrittenSpan);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed class WebSocketClosedException : Exception;
}
