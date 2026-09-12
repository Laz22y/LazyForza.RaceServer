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
    ILogger<RaceWebSocketHandler> logger,
    IngressProtection ingress)
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

        var source = ingress.Source(context);
        using var pending = ingress.TryAcquireConnection(source);
        if (pending is null)
        {
            await IngressHttp.TooManyRequests(context, ingress.Options.LoginTimeoutSeconds).ExecuteAsync(context);
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var budget = new ConnectionMessageBudget(ingress.Options);
        Guid? participantId = null;
        var isObserver = false;
        try
        {
            using var loginTimeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var loginRead = ReceiveEnvelopeAsync(socket, budget, loginTimeout.Token);
            var deadline = Task.Delay(TimeSpan.FromSeconds(ingress.Options.LoginTimeoutSeconds), loginTimeout.Token);
            RaceEnvelope loginEnvelope;
            try
            {
                if (await Task.WhenAny(loginRead, deadline) != loginRead)
                {
                    // Cancelling ReceiveAsync aborts ManagedWebSocket; send the policy response first.
                    await CloseLimitedAsync(socket, null, "loginTimeout", "登录超时，请重新连接。", null, 1008);
                    loginTimeout.Cancel();
                    try { await loginRead; }
                    catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or WebSocketClosedException) { }
                    return;
                }
                loginEnvelope = await loginRead;
            }
            finally { loginTimeout.Cancel(); }
            if (loginEnvelope.ProtocolVersion != RaceProtocol.CurrentVersion || loginEnvelope.Type != RaceMessageTypes.Login)
            {
                await CloseLimitedAsync(socket, null, "protocolMismatch", $"服务端协议版本为 {RaceProtocol.CurrentVersion}。", null, 1008);
                return;
            }

            var login = RaceProtocolJson.DeserializePayload<RaceLoginRequest>(loginEnvelope);
            using var attempt = ingress.TryLogin(source, "player", IngressProtection.LoginIdentity(login.DisplayName, login.ResumeToken), out var retry);
            if (attempt is null)
            {
                await CloseLimitedAsync(socket, null, "rateLimited", IngressHttp.RetryMessage(retry), retry);
                return;
            }
            if (login.Password is not { Length: <= 128 })
            {
                await CloseLimitedAsync(socket, null, "invalidPassword", "赛事密码不正确。", null, 1008);
                return;
            }
            var result = coordinator.TryJoin(login);
            attempt.Complete(result.Rejected?.Code != "invalidPassword");
            if (!result.IsAccepted)
            {
                await CloseLimitedAsync(socket, null, result.Rejected!.Code, result.Rejected.Message, null, 1008);
                return;
            }

            pending.Dispose();
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
                    envelope = await ReceiveEnvelopeAsync(socket, budget, context.RequestAborted);
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

                if (!registry.IsCurrent(participantId.Value, socket)) break;

                var command = HandleMessage(participantId.Value, isObserver, envelope, socket, context.RequestAborted);
                if (command is not null) await command;
            }
        }
        catch (WebSocketClosedException) { }
        catch (IngressLimitException exception)
        {
            await CloseLimitedAsync(socket, participantId, exception.Code, exception.Message, exception.RetryAfterSeconds, exception.CloseCode);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Race WebSocket login or message timed out from {RemoteAddress}.", context.Connection.RemoteIpAddress);
            await CloseLimitedAsync(socket, participantId, "loginTimeout", "登录超时，请重新连接。", null, 1008);
        }
        catch (JsonException exception)
        {
            logger.LogInformation(exception, "Race client sent malformed JSON from {RemoteAddress}.", context.Connection.RemoteIpAddress);
            await CloseLimitedAsync(socket, participantId, "invalidMessage", "消息格式无效。", null, 1008);
        }
        catch (IOException exception)
        {
            logger.LogError(exception, "赛事状态未能持久保存；关闭连接，不发送成功回执。");
            socket.Abort();
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogError(exception, "赛事状态文件不可写；关闭连接，不发送成功回执。");
            socket.Abort();
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
            case RaceMessageTypes.Leave:
                return LeaveAsync(participantId, socket, cancellationToken);
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
                if (result.IsDeferred)
                    return SendRegisteredErrorAsync(participantId, socket, "commandRejected",
                        result.Error!, cancellationToken);
                return SendRegisteredAsync(
                    participantId,
                    socket,
                    RaceMessageTypes.LapAcknowledged,
                    new RaceLapAcknowledgement(completed.EventId, result.IsAccepted, result.Error,
                        result.LapValidationStatus ?? (result.IsAccepted ? RaceLapValidationStatus.InsufficientEvidence : RaceLapValidationStatus.Rejected)),
                    cancellationToken);
            }
            case RaceMessageTypes.PitServiceCompleted:
            {
                if (isObserver)
                    return SendRegisteredErrorAsync(
                        participantId, socket, "observerReadOnly", "OB 不能提交维修停留。", cancellationToken);
                var completed = RaceProtocolJson.DeserializePayload<RacePitServiceCompleted>(envelope);
                var result = coordinator.CompletePitService(participantId, completed);
                if (result.IsDeferred)
                    return SendRegisteredErrorAsync(participantId, socket, "commandRejected",
                        result.Error!, cancellationToken);
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

    private async Task LeaveAsync(Guid id, WebSocket socket, CancellationToken token)
    {
        var result = coordinator.DisconnectAndReleaseClient(id, voluntary: true);
        if (!result.IsAccepted) { await ReplyToResult(id, socket, result, token); return; }
        // Core persists the release before the client may discard its recovery identity.
        await SendRegisteredAsync(id, socket, RaceMessageTypes.Left, new { }, token);
        await registry.DisconnectAsync(id, "left room", token, WebSocketCloseStatus.NormalClosure);
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

    private static async Task<RaceEnvelope> ReceiveEnvelopeAsync(WebSocket socket, ConnectionMessageBudget budget, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(RaceProtocol.MaximumMessageBytes);
        var written = 0;
        var fragments = 0;
        try
        {
            while (true)
            {
                if (written >= RaceProtocol.MaximumMessageBytes)
                    throw new IngressLimitException("messageTooLarge", "消息超过大小上限。", null, 1009);
                var received = await socket.ReceiveAsync(
                    buffer.AsMemory(written, RaceProtocol.MaximumMessageBytes - written),
                    cancellationToken);
                if (received.MessageType == WebSocketMessageType.Close) throw new WebSocketClosedException();
                if (received.MessageType != WebSocketMessageType.Text)
                    throw new JsonException("Only text WebSocket messages are supported.");
                if (!budget.TryConsume(received.Count, fragments == 0, out var retry))
                    throw new IngressLimitException("rateLimited", IngressHttp.RetryMessage(retry), retry, 1013);
                if (++fragments > 256) throw new IngressLimitException("invalidMessage", "消息分片过多。", null, 1008);
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

    private async Task CloseLimitedAsync(WebSocket socket, Guid? participantId, string code, string message, int? retry, int closeCode = 1013)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            if (socket.State != WebSocketState.Open) return;
            if (participantId is Guid id)
                await SendRegisteredAsync(id, socket, RaceMessageTypes.Error, new RaceErrorPayload(code, message, retry), timeout.Token);
            else
                await SendAsync(socket, RaceMessageTypes.LoginRejected, new RaceLoginRejected(code, message, retry), timeout.Token);
            await socket.CloseOutputAsync((WebSocketCloseStatus)closeCode, code, timeout.Token);
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException) { socket.Abort(); }
    }

    private sealed class IngressLimitException(string code, string message, int? retry, int closeCode) : Exception(message)
    {
        public string Code { get; } = code;
        public int? RetryAfterSeconds { get; } = retry;
        public int CloseCode { get; } = closeCode;
    }
    private sealed class WebSocketClosedException : Exception;
}
