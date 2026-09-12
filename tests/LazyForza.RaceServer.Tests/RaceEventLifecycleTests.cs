using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using LazyForza.RaceServer.Core;
using LazyForza.RaceServer.Protocol;
using LazyForza.RaceServer.Web;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LazyForza.RaceServer.Tests;

[TestClass]
public sealed class RaceEventLifecycleTests
{
    [TestMethod]
    public async Task CompletingActiveProjectPreparesIndependentEventAndSurvivesHostRebuild()
    {
        using var data = new RaceRecoveryTests.TestData();
        Assert.IsTrue(new RaceServerConfigurationStore(data.Options).ConfigureInitial(new("player-pass", "admin-pass", "Lifecycle", 5, 3)).Success);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        Guid projectId;
        Guid? nextEvent;
        await using (var host = await RaceRecoveryTests.NativeHost.Start(data.Options.DataDirectory, token))
        {
            var response = await host.Client.PostAsJsonAsync("/api/admin/event-projects", new { name = "First" }, token);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(token);
            projectId = body.GetProperty("project").GetProperty("id").GetGuid();
            await host.Post($"event-projects/{projectId}/activate", new { }, token);
            await host.Post("session", new { phase = "practice" }, token);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await host.Client.PutAsJsonAsync("/api/admin/schedule", new { countdownSeconds = 90 }, token)).StatusCode);
            Assert.AreNotEqual(90, new RaceServerConfigurationStore(data.Options).Schedule.CountdownSeconds);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync($"/api/admin/event-projects/{projectId}/complete", new { }, token)).StatusCode);
            await host.Post("session", new { phase = "lobby" }, token);
            await host.Post($"event-projects/{projectId}/complete", new { }, token);
            var state = await host.Client.GetFromJsonAsync<RaceSessionSnapshot>("/api/admin/state", RaceProtocolJson.Options, token);
            nextEvent = state!.EventId;
            Assert.AreNotEqual(projectId, nextEvent);
            Assert.AreEqual(RaceSessionPhase.Lobby, state.Phase);
        }
        await using var rebuilt = await RaceRecoveryTests.NativeHost.Start(data.Options.DataDirectory, token);
        var after = await rebuilt.Client.GetFromJsonAsync<RaceSessionSnapshot>("/api/admin/state", RaceProtocolJson.Options, token);
        Assert.AreEqual(nextEvent, after!.EventId);
        Assert.AreEqual(RaceEventProjectStatus.Completed, new RaceEventProjectStore(data.Options).Find(projectId)!.Status);
    }

    [TestMethod]
    public async Task SwitchingProjectsKeepsOwnedResultsAndResetsConnectedDrivers()
    {
        using var data = new RaceRecoveryTests.TestData();
        var core = data.Create();
        var settings = Settings(data.Options);
        var projects = new RaceEventProjectStore(data.Options);
        var lifecycle = Lifecycle(data.Options, core, projects, settings);
        var first = Create(projects, core, "First event", 13);
        var second = Create(projects, core, "Second event", 21);
        var online = core.TryJoin(Login("Online")).Accepted!;
        await lifecycle.PrepareAsync(first.Id);
        StartPractice(core);
        Assert.IsFalse(core.ApplyRoomSettings(RaceEventLifecycle.ToCommand(core.RoomSettings()) with { TotalRaceLaps = 99 }).IsAccepted);
        var lap = Lap(core);
        Assert.IsTrue(core.CompleteLap(online.ParticipantId, lap).IsAccepted);
        Assert.IsTrue(core.ApplyPenalty(new(online.ParticipantId, RacePenaltyKind.Time, 6, null, "Review")).IsAccepted);
        core.ApplySessionCommand(new(RaceSessionPhase.Lobby, null, null, null, null));
        var offline = core.TryJoin(Login("Offline")).Accepted!;
        core.Disconnect(offline.ParticipantId);
        await lifecycle.PrepareAsync(second.Id);

        var snapshot = core.Snapshot();
        Assert.AreEqual(second.Id, snapshot.EventId);
        Assert.AreEqual(online.ParticipantId, snapshot.Participants.Single().Id);
        Assert.IsFalse(snapshot.Participants.Single().IsReady);
        Assert.AreEqual(0, snapshot.Participants.Single().CompletedLaps);
        Assert.HasCount(0, snapshot.Penalties!);
        var savedFirst = projects.Find(first.Id)!;
        Assert.AreEqual(RaceEventProjectStatus.Completed, savedFirst.Status);
        Assert.AreEqual(first.Id, savedFirst.Results.Single().EventId);
        Assert.AreEqual("First event", savedFirst.Results.Single().SessionName);
        Assert.AreEqual(1, savedFirst.Results.Single().Participants.Single().CompletedLaps);
        Assert.HasCount(1, savedFirst.Results.Single().Participants.Single().Penalties);
        Assert.HasCount(0, projects.Find(second.Id)!.Results);
        Assert.AreEqual(21, new RaceServerConfigurationStore(data.Options).Schedule.CountdownSeconds);
        StartPractice(core);
        Assert.IsTrue(core.CompleteLap(online.ParticipantId, lap).IsAccepted);
        Assert.IsFalse(core.CompleteLap(online.ParticipantId, lap with { EventId = Guid.NewGuid() }).IsAccepted);
        Assert.AreEqual(0, core.Snapshot().Participants.Single().CompletedLaps);
        core.ApplySessionCommand(new(RaceSessionPhase.Lobby, null, null, null, null));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => lifecycle.PrepareAsync(first.Id));
        var rebuilt = data.Restore();
        Assert.AreEqual(second.Id, rebuilt.Snapshot().EventId);
        Assert.IsTrue(rebuilt.Results().Any(result => result.EventId == first.Id));
    }

    [TestMethod]
    public async Task InterruptedActivationReplaysBeforeServingAndNeverDuplicatesEvent()
    {
        using var data = new RaceRecoveryTests.TestData();
        var settings = Settings(data.Options);
        var fault = new SaveFailure(new FileRaceStatePersistence(data.Options));
        var core = new RaceCoordinator(data.Options, fault);
        core.TryJoin(Login("Driver"));
        var projects = new RaceEventProjectStore(data.Options);
        var target = Create(projects, core, "Recover me", 19);
        var lifecycle = Lifecycle(data.Options, core, projects, settings);
        fault.Fail = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => lifecycle.PrepareAsync(target.Id));
        Assert.IsTrue(lifecycle.HasPending);
        Assert.AreEqual(RaceEventProjectStatus.Draft, projects.Find(target.Id)!.Status);
        // Rebuild every service from disk, as a fresh process does.
        var rebuilt = data.Restore();
        var recoveredProjects = new RaceEventProjectStore(data.Options);
        var recovered = Lifecycle(data.Options, rebuilt, recoveredProjects, new(data.Options));
        await recovered.RecoverAsync();
        Assert.IsFalse(recovered.HasPending);
        Assert.AreEqual(target.Id, rebuilt.Snapshot().EventId);
        Assert.AreEqual(RaceSessionPhase.Lobby, rebuilt.Snapshot().Phase);
        Assert.AreEqual(RaceEventProjectStatus.Active, recoveredProjects.Find(target.Id)!.Status);
        var revision = rebuilt.Snapshot().Revision;
        await recovered.RecoverAsync();
        await recovered.PrepareAsync(target.Id);
        Assert.AreEqual(revision, rebuilt.Snapshot().Revision);
        Assert.AreEqual(target.Id, data.Restore().Snapshot().EventId);
    }

    [TestMethod]
    public void SameProcessRetryOfPreparedEventStillRequiresDurableSave()
    {
        using var data = new RaceRecoveryTests.TestData();
        var persistence = new SaveFailure(new FileRaceStatePersistence(data.Options));
        var core = new RaceCoordinator(data.Options, persistence);
        core.TryJoin(Login("Driver"));
        var id = Guid.NewGuid();
        persistence.Fail = true;
        Assert.ThrowsExactly<IOException>(() => core.BeginEvent(id));
        Assert.ThrowsExactly<IOException>(() => core.BeginEvent(id));
        persistence.Fail = false;
        Assert.IsTrue(core.BeginEvent(id).IsAccepted);
        Assert.AreEqual(id, data.Restore().Snapshot().EventId);
    }

    [TestMethod]
    public void RepeatedPracticeLeavesKeepCompactHistoryAndNoReservedBuffersAfterRebuild()
    {
        using var data = new RaceRecoveryTests.TestData();
        var core = data.Create();
        StartPractice(core);
        for (var index = 0; index < 150; index++)
        {
            var driver = core.TryJoin(Login("Reusable name")).Accepted!;
            Assert.IsTrue(core.CompleteLap(driver.ParticipantId, Lap(core)).IsAccepted);
            Assert.IsTrue(core.DisconnectAndReleaseClient(driver.ParticipantId, voluntary: true).IsAccepted);
            Assert.HasCount(0, core.Snapshot().Participants);
        }
        var saved = new FileRaceStatePersistence(data.Options).LoadRecoveryState()!;
        Assert.AreEqual(0, saved.State.GetProperty("participants").GetArrayLength());
        var rebuilt = data.Restore();
        Assert.HasCount(150, rebuilt.Results().Single().Participants);
        Assert.IsTrue(rebuilt.Results().Single().Participants.All(p => p.DisplayName == "Reusable name" && p.CompletedLaps == 1));
        Assert.IsTrue(rebuilt.AwaitingRecoveryConfirmation);
        Assert.IsTrue(rebuilt.ApplyFlagCommand(new(RaceControlFlag.Green, null)).IsAccepted);
        Assert.IsTrue(rebuilt.TryJoin(Login("Reusable name")).IsAccepted);
    }

    private static RaceEventLifecycle Lifecycle(RaceServerOptions options, RaceCoordinator core, RaceEventProjectStore projects, RaceServerConfigurationStore settings) =>
        new(options, core, projects, settings, new(options), new(options));
    private static RaceServerConfigurationStore Settings(RaceServerOptions options)
    {
        var settings = new RaceServerConfigurationStore(options);
        Assert.IsTrue(settings.ConfigureInitial(new("player-pass", "admin-pass", "Tests", 5, 3)).Success);
        return settings;
    }
    private static RaceEventProjectSnapshot Create(RaceEventProjectStore projects, RaceCoordinator core, string name, int countdown) =>
        projects.Create(new(name, null, null, null, null, "Asia/Shanghai", new(CountdownSeconds: countdown)), core.RoomSettings(), [], [], null, null, null, null);
    private static RaceLoginRequest Login(string name) => new("player-pass", name, "#336699", null, "test", null, null, null, null, TeamId: "team-1");
    private static void StartPractice(RaceCoordinator core) => Assert.IsTrue(core.ApplySessionCommand(new(RaceSessionPhase.Practice, null, null, null, null)).IsAccepted);
    private static RaceLapCompleted Lap(RaceCoordinator core) => new(Guid.NewGuid(), 1, 60, [20, 20, 20], true, null, 60000, StageId: core.Snapshot().StageId);
    private sealed class SaveFailure(IRaceStatePersistence inner) : IRaceStatePersistence
    {
        public bool Fail { get; set; }
        public RaceRecoveryState? LoadRecoveryState() => inner.LoadRecoveryState();
        public void SaveRecoveryState(RaceRecoveryState state) { if (Fail) throw new IOException("Injected save failure"); inner.SaveRecoveryState(state); }

        public void AppendAudit(RaceAuditEntry entry) => inner.AppendAudit(entry);
    }
}
