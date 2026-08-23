using System.Buffers;
using System.Net.WebSockets;
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
        var isObserver = false;
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
            isObserver = result.Accepted.IsObserver;
            await registry.RegisterAsync(participantId.Value, socket, context.RequestAborted);
            await SendRegisteredAsync(participantId.Value, socket, RaceMessageTypes.LoginAccepted,
                result.Accepted with { Snapshot = broadcasts.WithOrganizerLogo(result.Accepted.Snapshot) },
                context.RequestAborted);
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
                    await SendRegisteredErrorAsync(
                        participantId.Value,
                        socket,
                        "protocolMismatch",
                        "协议版本不一致。",
                        context.RequestAborted);
                    continue;
                }

                var command = HandleMessage(participantId.Value, isObserver, envelope, socket, context.RequestAborted);
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
                if (registry.Unregister(id, socket)) coordinator.Disconnect(id);
            }
        }
    }

    private Task? HandleMessage(
        Guid participantId,
        bool isObserver,
        RaceEnvelope envelope,
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        switch (envelope.Type)
        {
            case RaceMessageTypes.Ready:
            {
                if (isObserver)
                    return SendRegisteredErrorAsync(
                        participantId, socket, "observerReadOnly", "OB 不参与准备与比赛流程。", cancellationToken);
                var update = RaceProtocolJson.DeserializePayload<RaceReadyUpdate>(envelope);
                return ReplyToResult(
                    participantId, socket, coordinator.SetReady(participantId, update.IsReady), cancellationToken);
            }
            case RaceMessageTypes.Telemetry:
            {
                if (isObserver)
                    return SendRegisteredErrorAsync(
                        participantId, socket, "observerReadOnly", "OB 不能上传车辆遥测。", cancellationToken);
                var update = RaceProtocolJson.DeserializePayload<RaceTelemetryUpdate>(envelope);
                var result = coordinator.UpdateTelemetry(participantId, update);
                return result.IsAccepted ? null : ReplyToResult(participantId, socket, result, cancellationToken);
            }
            case RaceMessageTypes.LapCompleted:
            {
                if (isObserver)
                    return SendRegisteredErrorAsync(
                        participantId, socket, "observerReadOnly", "OB 不能提交圈速。", cancellationToken);
                var completed = RaceProtocolJson.DeserializePayload<RaceLapCompleted>(envelope);
                var result = coordinator.CompleteLap(participantId, completed);
                return SendRegisteredAsync(
                    participantId,
                    socket,
                    RaceMessageTypes.LapAcknowledged,
                    new RaceLapAcknowledgement(completed.EventId, result.IsAccepted, result.Error),
                    cancellationToken);
            }
            case RaceMessageTypes.PitServiceCompleted:
            {
                if (isObserver)
                    return SendRegisteredErrorAsync(
                        participantId, socket, "observerReadOnly", "OB 不能提交维修停留。", cancellationToken);
                var completed = RaceProtocolJson.DeserializePayload<RacePitServiceCompleted>(envelope);
                var result = coordinator.CompletePitService(participantId, completed);
                return SendRegisteredAsync(
                    participantId,
                    socket,
                    RaceMessageTypes.PitServiceAcknowledged,
                    new RacePitServiceAcknowledgement(completed.EventId, result.IsAccepted, result.Error),
                    cancellationToken);
            }
            case RaceMessageTypes.Ping:
            {
                var ping = RaceProtocolJson.DeserializePayload<RaceClockPing>(envelope);
                return SendRegisteredAsync(participantId, socket, RaceMessageTypes.Pong,
                    new RaceClockPong(ping.ClientMonotonicMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                    cancellationToken);
            }
            default:
                return SendRegisteredErrorAsync(
                    participantId,
                    socket,
                    "unsupportedMessage",
                    $"不支持消息类型：{envelope.Type}",
                    cancellationToken);
        }
    }

    private Task ReplyToResult(
        Guid participantId,
        WebSocket socket,
        RaceCommandResult result,
        CancellationToken cancellationToken) =>
        result.IsAccepted
            ? Task.CompletedTask
            : SendRegisteredErrorAsync(
                participantId, socket, "commandRejected", result.Error ?? "命令被拒绝。", cancellationToken);

    private Task SendErrorAsync(WebSocket socket, string code, string message, CancellationToken cancellationToken) =>
        SendAsync(socket, RaceMessageTypes.Error, new { code, message }, cancellationToken);

    private Task SendRegisteredErrorAsync(
        Guid participantId,
        WebSocket socket,
        string code,
        string message,
        CancellationToken cancellationToken) =>
        SendRegisteredAsync(
            participantId, socket, RaceMessageTypes.Error, new { code, message }, cancellationToken);

    private async Task SendAsync<T>(WebSocket socket, string type, T payload, CancellationToken cancellationToken)
    {
        var message = RaceProtocolJson.SerializeToUtf8Bytes(type, Interlocked.Increment(ref sequence), payload);
        await socket.SendAsync(message, WebSocketMessageType.Text, true, cancellationToken);
    }

    private async Task SendRegisteredAsync<T>(
        Guid participantId,
        WebSocket socket,
        string type,
        T payload,
        CancellationToken cancellationToken)
    {
        var message = RaceProtocolJson.SerializeToUtf8Bytes(
            type,
            Interlocked.Increment(ref sequence),
            payload);
        if (!await registry.SendAsync(participantId, socket, message, cancellationToken))
            throw new WebSocketException("赛事客户端连接已经被替换或发送失败。");
    }

    private static async Task<RaceEnvelope> ReceiveEnvelopeAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(RaceProtocol.MaximumMessageBytes);
        var written = 0;
        try
        {
            while (true)
            {
                if (written >= RaceProtocol.MaximumMessageBytes)
                    throw new JsonException("Race message exceeds the maximum size.");
                var received = await socket.ReceiveAsync(
                    buffer.AsMemory(written, RaceProtocol.MaximumMessageBytes - written),
                    cancellationToken);
                if (received.MessageType == WebSocketMessageType.Close) throw new WebSocketClosedException();
                if (received.MessageType != WebSocketMessageType.Text)
                    throw new JsonException("Only text WebSocket messages are supported.");
                written += received.Count;
                if (received.EndOfMessage) break;
            }
            return RaceProtocolJson.DeserializeEnvelope(buffer.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed class WebSocketClosedException : Exception;
}
