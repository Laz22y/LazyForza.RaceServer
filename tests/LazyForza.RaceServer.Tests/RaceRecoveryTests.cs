using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using LazyForza.RaceServer.Core;
using LazyForza.RaceServer.Protocol;
using LazyForza.RaceServer.Web;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LazyForza.RaceServer.Tests;

[TestClass]
public sealed class RaceRecoveryTests
{
    [TestMethod]
    public void RebuiltCoordinatorPreservesReviewReceiptAndLapSequence()
    {
        using var data = new TestData();
        var original = data.Create();
        var joined = original.TryJoin(Login()).Accepted!;
        StartRace(original);
        var lap = Lap() with { SectorSeconds = [20,20,25], StageId = original.Snapshot().StageId };
        Assert.AreEqual(RaceLapValidationStatus.PendingReview, original.CompleteLap(joined.ParticipantId, lap).LapValidationStatus);
        var rebuilt = data.Restore();
        Assert.AreEqual(lap.StageId, rebuilt.Snapshot().StageId);
        Assert.AreEqual(RaceLapValidationStatus.PendingReview, rebuilt.CompleteLap(joined.ParticipantId, lap).LapValidationStatus);
        Assert.IsTrue(rebuilt.ApplyFlagCommand(new(RaceControlFlag.Green, null)).IsAccepted);
        Assert.IsFalse(rebuilt.CompleteLap(joined.ParticipantId, lap with { EventId = Guid.NewGuid() }).IsAccepted);
        Assert.AreEqual(1, rebuilt.Snapshot().Participants.Single().CompletedLaps);
        Assert.HasCount(1, rebuilt.Snapshot().Investigations!);
    }

    [TestMethod]
    public void ReconstructedCoordinatorPreservesIdentityResultsPenaltiesAndDeduplication()
    {
        using var data = new TestData();
        var original = data.Create();
        var login = original.TryJoin(Login()).Accepted!;
        StartRace(original);
        var lap = Lap();
        Assert.IsTrue(original.CompleteLap(login.ParticipantId, lap).IsAccepted);
        Assert.IsTrue(original.ApplyPenalty(new(login.ParticipantId, RacePenaltyKind.Time, 6, null, "test penalty")).IsAccepted);
        var penalty = original.Snapshot().Penalties!.Single();
        var rebuilt = data.Restore();
        Assert.IsTrue(rebuilt.AwaitingRecoveryConfirmation);
        Assert.AreEqual(RaceSessionPhase.Suspended, rebuilt.Snapshot().Phase);
        var resumed = rebuilt.TryJoin(Login() with { ResumeToken = login.ResumeToken }).Accepted!;
        Assert.AreEqual(login.ParticipantId, resumed.ParticipantId);
        Assert.AreEqual(login.ResumeToken, resumed.ResumeToken);
        Assert.IsTrue(rebuilt.CompleteLap(login.ParticipantId, lap).IsAccepted);
        Assert.IsFalse(rebuilt.CompleteLap(login.ParticipantId, Lap()).IsAccepted);
        Assert.AreEqual(1, rebuilt.Snapshot().Participants.Single().CompletedLaps);
        Assert.AreEqual(penalty, rebuilt.Snapshot().Penalties!.Single());
        var elapsed = rebuilt.Snapshot().RaceElapsedSeconds;
        rebuilt.Tick(DateTimeOffset.UtcNow.AddHours(4));
        Assert.AreEqual(elapsed, rebuilt.Snapshot(DateTimeOffset.UtcNow.AddHours(4)).RaceElapsedSeconds);
        Assert.IsTrue(rebuilt.ApplyFlagCommand(new(RaceControlFlag.Green, null)).IsAccepted);
        Assert.IsTrue(rebuilt.CompleteLap(login.ParticipantId, lap).IsAccepted);
        Assert.IsTrue(rebuilt.CompleteLap(login.ParticipantId, Lap() with { LapNumber = 2 }).IsAccepted);
        var third = data.Restore();
        Assert.AreEqual(2, third.Snapshot().Participants.Single().CompletedLaps);
        Assert.AreEqual(6, third.Snapshot().Participants.Single().PendingTimePenaltySeconds);
        CollectionAssert.AreEqual(new double?[] { 20, 20, 20 }, third.Snapshot().Participants.Single().BestSectorSeconds.ToArray());
    }

    [TestMethod]
    public void FailedSaveCannotReturnSuccessEvenOnDuplicateRetry()
    {
        using var data = new TestData();
        var fault = new FaultPersistence(new FileRaceStatePersistence(data.Options));
        var coordinator = new RaceCoordinator(data.Options, fault);
        var joined = coordinator.TryJoin(Login()).Accepted!;
        StartRace(coordinator);
        var before = File.ReadAllBytes(data.StatePath);
        var lap = Lap();
        fault.Fail = true;
        Assert.ThrowsExactly<IOException>(() => coordinator.CompleteLap(joined.ParticipantId, lap));
        Assert.ThrowsExactly<IOException>(() => coordinator.CompleteLap(joined.ParticipantId, lap));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(data.StatePath));
        fault.Fail = false;
        Assert.IsTrue(coordinator.CompleteLap(joined.ParticipantId, lap).IsAccepted);
        Assert.AreEqual(1, data.Restore().Snapshot().Participants.Single().CompletedLaps);
    }

    [TestMethod]
    public void AtomicWriteFailureLeavesLastCommittedFileReadable()
    {
        using var data = new TestData();
        var coordinator = data.Create();
        coordinator.TryJoin(Login());
        var persistence = new FileRaceStatePersistence(data.Options);
        var committed = persistence.LoadRecoveryState()!;
        var bytes = File.ReadAllBytes(data.StatePath);
        Directory.CreateDirectory(data.StatePath + ".tmp");
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => persistence.SaveRecoveryState(committed));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(data.StatePath));
        Assert.AreEqual(committed.State.GetRawText(), persistence.LoadRecoveryState()!.State.GetRawText());
    }

    [TestMethod]
    public void LegacyCorruptAndUnknownVersionsAreNeverOverwrittenOnStartup()
    {
        using var data = new TestData();
        var legacy = JsonSerializer.Serialize(data.Create().Snapshot(), RaceProtocolJson.Options);
        foreach (var json in new[] { legacy, "{", "{\"version\":999,\"savedAt\":\"2026-09-07T00:00:00Z\",\"state\":{}}" })
        {
            File.WriteAllText(data.StatePath, json);
            try { data.Restore(); Assert.Fail("Invalid recovery file was accepted."); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException) { }
            Assert.AreEqual(json, File.ReadAllText(data.StatePath));
        }
    }

    [TestMethod]
    public void ArchivedResultsAndRevokedPenaltiesSurviveReconstruction()
    {
        using var data = new TestData();
        var coordinator = data.Create();
        var joined = coordinator.TryJoin(Login()).Accepted!;
        StartRace(coordinator);
        coordinator.CompleteLap(joined.ParticipantId, Lap());
        coordinator.ApplyPenalty(new(joined.ParticipantId, RacePenaltyKind.Time, 5, null, "revoked"));
        var penalty = coordinator.Snapshot().Penalties!.Single();
        coordinator.UpdatePenalty(new(penalty.Id, 5, "withdrawn", true));
        var rebuilt = data.Restore();
        Assert.IsTrue(rebuilt.Snapshot().Penalties!.Single().IsRevoked);
        rebuilt.ApplySessionCommand(new(RaceSessionPhase.Lobby, null, null, null, null));
        var archived = rebuilt.Results().Single();
        var again = data.Restore();
        Assert.AreEqual(archived.Id, again.Results().Single().Id);
        Assert.AreEqual(1, again.Results().Count);
    }

    [DataTestMethod]
    [DataRow(RaceSessionPhase.Practice)]
    [DataRow(RaceSessionPhase.Qualifying)]
    [DataRow(RaceSessionPhase.Countdown)]
    public void RestartFreezesTimedStagesUntilGlobalGreen(RaceSessionPhase phase)
    {
        using var data = new TestData();
        var original = data.Create();
        original.TryJoin(Login());
        original.ApplySessionCommand(new(phase, null, null, 10, 10, ForceStart: true));
        _ = data.Restore();
        var second = data.Restore();
        Assert.IsTrue(second.AwaitingRecoveryConfirmation);
        var before = second.Snapshot();
        var later = DateTimeOffset.UtcNow.AddHours(1);
        second.Tick(later);
        Assert.AreEqual(RaceSessionPhase.Suspended, second.Snapshot(later).Phase);
        Assert.IsFalse(second.ApplyFlagCommand(new(RaceControlFlag.Green, null, 0), later).IsAccepted);
        Assert.IsTrue(second.ApplyFlagCommand(new(RaceControlFlag.Green, null), later).IsAccepted);
        var after = second.Snapshot(later);
        Assert.AreEqual(phase, after.Phase);
        if (phase == RaceSessionPhase.Practice) Assert.IsTrue(after.PracticeEndsAt > before.PracticeEndsAt);
        if (phase == RaceSessionPhase.Qualifying) Assert.IsTrue(after.QualifyingEndsAt > before.QualifyingEndsAt);
        if (phase == RaceSessionPhase.Countdown) Assert.IsTrue(after.StartsAt > before.StartsAt);
    }

    [TestMethod]
    public void ServedPenaltyAndPitVisitDeduplicationSurviveRestart()
    {
        using var data = new TestData();
        var coordinator = data.Create();
        var joined = coordinator.TryJoin(Login()).Accepted!;
        StartRace(coordinator);
        coordinator.ApplyPenalty(new(joined.ParticipantId, RacePenaltyKind.Time, 2, null, "serve before restart"));
        var now = DateTimeOffset.UtcNow;
        var visitId = Guid.NewGuid();
        var stopped = new RaceTelemetryUpdate(1000, .5, 0, .5, .5, 0, 0, 1, 30,
            true, true, true, false, RaceGripCondition.Unknown, 0, false, 0,
            PitServiceVisitId: visitId);
        coordinator.UpdateTelemetry(joined.ParticipantId, stopped, now);
        coordinator.UpdateTelemetry(joined.ParticipantId, stopped, now.AddSeconds(1));
        coordinator.UpdateTelemetry(joined.ParticipantId, stopped, now.AddSeconds(2.1));
        Assert.IsTrue(coordinator.Snapshot().Penalties!.Single().IsServed);
        var completed = new RacePitServiceCompleted(Guid.NewGuid(), visitId, 1, 1, 1, 3100,
            coordinator.Snapshot().StartsAt!.Value.ToUnixTimeMilliseconds());
        Assert.IsTrue(coordinator.CompletePitService(joined.ParticipantId, completed).IsAccepted);
        var rebuilt = data.Restore();
        Assert.IsTrue(rebuilt.Snapshot().Penalties!.Single().IsServed);
        Assert.AreEqual(0, rebuilt.Snapshot().Participants.Single().PendingTimePenaltySeconds);
        Assert.IsTrue(rebuilt.CompletePitService(joined.ParticipantId, completed).IsAccepted);
        rebuilt.TryJoin(Login() with { ResumeToken = joined.ResumeToken });
        rebuilt.ApplyFlagCommand(new(RaceControlFlag.Green, null));
        Assert.IsTrue(rebuilt.CompletePitService(joined.ParticipantId, completed with { EventId = Guid.NewGuid() }).IsAccepted);
        Assert.AreEqual(1, data.Restore().Snapshot().Participants.Single().CompletedPitServices);
    }

    [TestMethod]
    public void RevokedDriverIdentityAndObserverResumeIdentitySurviveRestart()
    {
        using var data = new TestData();
        var coordinator = data.Create();
        var driver = coordinator.TryJoin(Login()).Accepted!;
        var observerLogin = Login() with { IsObserver = true, DisplayName = "Recovery observer" };
        var observer = coordinator.TryJoin(observerLogin).Accepted!;
        coordinator.DisconnectAndReleaseClient(driver.ParticipantId);
        var rebuilt = data.Restore();
        Assert.AreEqual("disconnectedByControl", rebuilt.TryJoin(Login() with { ResumeToken = driver.ResumeToken }).Rejected!.Code);
        var restoredObserver = rebuilt.TryJoin(observerLogin with { ResumeToken = observer.ResumeToken }).Accepted!;
        Assert.AreEqual(observer.ParticipantId, restoredObserver.ParticipantId);
        Assert.IsTrue(restoredObserver.IsObserver);
    }

    [TestMethod]
    public async Task KilledNativeProcessesRestoreAcknowledgedLapsAndPenaltiesWithoutDoubleCounting()
    {
        using var data = new TestData();
        var configuration = new RaceServerConfigurationStore(data.Options);
        Assert.IsTrue(configuration.ConfigureInitial(new("player-pass", "admin-pass", "Process recovery", 5, 3)).Success);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var token = timeout.Token;
        RaceLoginAccepted joined;
        var lap = Lap();
        await using (var host = await NativeHost.Start(data.Options.DataDirectory, token))
        {
            using var socket = await host.Connect(token);
            joined = await LoginSocket(socket, Login(), token);
            await host.Post("session", new RaceAdminSessionCommand(RaceSessionPhase.Race, null, 5, null, null), token);
            await host.Post("penalty", new RaceAdminPenaltyCommand(joined.ParticipantId, RacePenaltyKind.Time, 6, null, "persisted penalty"), token);
            await Send(socket, RaceMessageTypes.LapCompleted, lap, token);
            var ack = await Receive<RaceLapAcknowledgement>(socket, RaceMessageTypes.LapAcknowledged, token);
            Assert.IsTrue(ack.IsAccepted);
            // The file is already complete at receipt time; kill without graceful shutdown.
            var state = new FileRaceStatePersistence(data.Options).LoadRecoveryState()!;
            Assert.IsTrue(state.State.GetProperty("receivedLapEvents").EnumerateArray().Any(item => item.GetGuid() == lap.EventId));
            host.Kill();
        }
        await using (var host = await NativeHost.Start(data.Options.DataDirectory, token))
        {
            using var socket = await host.Connect(token);
            var resumed = await LoginSocket(socket, Login() with { ResumeToken = joined.ResumeToken }, token);
            Assert.AreEqual(joined.ParticipantId, resumed.ParticipantId);
            Assert.AreEqual(RaceSessionPhase.Suspended, resumed.Snapshot.Phase);
            Assert.AreEqual(1, resumed.Snapshot.Participants.Single().CompletedLaps);
            Assert.AreEqual(6, resumed.Snapshot.Participants.Single().PendingTimePenaltySeconds);
            await Send(socket, RaceMessageTypes.LapCompleted, lap, token);
            Assert.IsTrue((await Receive<RaceLapAcknowledgement>(socket, RaceMessageTypes.LapAcknowledged, token)).IsAccepted);
            var second = Lap() with { LapNumber = 2 };
            await Send(socket, RaceMessageTypes.LapCompleted, second, token);
            _ = await Receive<JsonElement>(socket, RaceMessageTypes.Error, token);
            await host.Post("flag", new RaceAdminFlagCommand(RaceControlFlag.Green, null), token);
            await Send(socket, RaceMessageTypes.LapCompleted, second, token);
            Assert.IsTrue((await Receive<RaceLapAcknowledgement>(socket, RaceMessageTypes.LapAcknowledged, token)).IsAccepted);
            host.Kill();
        }
        await using (var host = await NativeHost.Start(data.Options.DataDirectory, token))
        {
            using var socket = await host.Connect(token);
            var resumed = await LoginSocket(socket, Login() with { ResumeToken = joined.ResumeToken }, token);
            Assert.AreEqual(2, resumed.Snapshot.Participants.Single().CompletedLaps);
            Assert.AreEqual(1, resumed.Snapshot.Penalties!.Count);
            Assert.AreEqual(6, resumed.Snapshot.Participants.Single().PendingTimePenaltySeconds);
            await Send(socket, RaceMessageTypes.LapCompleted, lap, token);
            Assert.IsTrue((await Receive<RaceLapAcknowledgement>(socket, RaceMessageTypes.LapAcknowledged, token)).IsAccepted);
        }
    }

    private static void StartRace(RaceCoordinator coordinator) =>
        Assert.IsTrue(coordinator.ApplySessionCommand(new(RaceSessionPhase.Race, null, 5, null, null)).IsAccepted);
    private static RaceLapCompleted Lap() => new(Guid.NewGuid(), 1, 60, [20, 20, 20], true, null, 1000);
    private static RaceLoginRequest Login() => new("player-pass", "Recovery driver", "#336699", null, "test", null, null, null, null, null, "team-1");
    private static async Task<RaceLoginAccepted> LoginSocket(ClientWebSocket socket, RaceLoginRequest login, CancellationToken token)
    {
        await Send(socket, RaceMessageTypes.Login, login, token);
        return await Receive<RaceLoginAccepted>(socket, RaceMessageTypes.LoginAccepted, token);
    }
    private static Task Send<T>(ClientWebSocket socket, string type, T payload, CancellationToken token) =>
        socket.SendAsync(new ArraySegment<byte>(RaceProtocolJson.SerializeToUtf8Bytes(type, 1, payload)), WebSocketMessageType.Text, true, token);
    private static async Task<T> Receive<T>(ClientWebSocket socket, string type, CancellationToken token)
    {
        var buffer = new byte[RaceProtocol.MaximumMessageBytes];
        while (true)
        {
            var length = 0;
            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), token);
                if (received.MessageType == WebSocketMessageType.Close) throw new IOException("Socket closed before receipt.");
                length += received.Count;
            } while (!received.EndOfMessage);
            var envelope = RaceProtocolJson.DeserializeEnvelope(buffer.AsSpan(0, length));
            if (envelope.Type == RaceMessageTypes.LoginRejected) throw new IOException(envelope.Payload.GetRawText());
            if (envelope.Type == type) return RaceProtocolJson.DeserializePayload<T>(envelope);
        }
    }

    internal sealed class TestData : IDisposable
    {
        public RaceServerOptions Options { get; } = new()
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), $"race-recovery-{Guid.NewGuid():N}"),
            PlayerPassword = "player-pass", AdminPassword = "admin-pass", MinimumRequiredPitStops = 0
        };
        public string StatePath => Path.Combine(Options.DataDirectory, "current-race.json");
        public RaceCoordinator Create() => new(Options, new FileRaceStatePersistence(Options));
        public RaceCoordinator Restore() { var value = Create(); value.RestorePersistedState(); return value; }
        public void Dispose() { if (Directory.Exists(Options.DataDirectory)) Directory.Delete(Options.DataDirectory, true); }
    }
    private sealed class FaultPersistence(IRaceStatePersistence inner) : IRaceStatePersistence
    {
        public bool Fail { get; set; }
        public RaceRecoveryState? LoadRecoveryState() => inner.LoadRecoveryState();
        public void SaveRecoveryState(RaceRecoveryState state)
        {
            if (Fail) throw new IOException("Simulated disk failure");
            inner.SaveRecoveryState(state);
        }
        public void AppendAudit(RaceAuditEntry entry) => inner.AppendAudit(entry);
    }
    internal sealed class NativeHost(Process process, HttpClient client, Uri address, Task<string> stdout, Task<string> stderr) : IAsyncDisposable
    {
        public HttpClient Client => client;
        private Task<string> Output => stdout;
        private Task<string> Errors => stderr;
        public static async Task<NativeHost> Start(string directory, CancellationToken token, params string[] extraArguments)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var address = new Uri($"http://127.0.0.1:{port}");
            var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { typeof(FileRaceStatePersistence).Assembly.Location, "--urls", address.ToString(), "--RaceServer:DataDirectory", directory })
                info.ArgumentList.Add(arg);
            foreach (var argument in extraArguments) info.ArgumentList.Add(argument);
            var process = Process.Start(info)!;
            var host = new NativeHost(process, new HttpClient { BaseAddress = address }, address,
                process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
            try
            {
                while (true)
                {
                    if (process.HasExited) throw new IOException(await host.Output + await host.Errors);
                    try { if ((await host.Client.GetAsync("/health", token)).IsSuccessStatusCode) break; }
                    catch (HttpRequestException) { }
                    await Task.Delay(50, token);
                }
                using var login = await host.Client.PostAsJsonAsync("/api/admin/login", new { password = "admin-pass" }, token);
                login.EnsureSuccessStatusCode();
                return host;
            }
            catch { await host.DisposeAsync(); throw; }
        }
        public async Task<ClientWebSocket> Connect(CancellationToken token)
        {
            var socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(new UriBuilder(address) { Scheme = "ws", Path = "/ws" }.Uri, token);
                return socket;
            }
            catch { socket.Dispose(); throw; }
        }
        public async Task Post<T>(string path, T value, CancellationToken token)
        {
            using var response = await client.PostAsJsonAsync("/api/admin/" + path, value, RaceProtocolJson.Options, token);
            response.EnsureSuccessStatusCode();
        }
        public void Kill() { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        public async ValueTask DisposeAsync()
        {
            Kill();
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            process.Dispose();
            client.Dispose();
        }
    }
}
