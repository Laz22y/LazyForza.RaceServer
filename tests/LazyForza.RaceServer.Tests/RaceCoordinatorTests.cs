using System.Diagnostics;
using System.Text;
using LazyForza.RaceServer.Core;
using LazyForza.RaceServer.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LazyForza.RaceServer.Tests;

[TestClass]
public sealed class RaceCoordinatorTests
{
    [TestMethod]
    public void CollisionInvestigationRequiresRaceToggleFreshNearbyPeerAndPreservesEvidenceForPostRaceReview()
    {
        var coordinator = CreateCoordinator();
        var reporter = Join(coordinator, 1).Accepted!;
        var other = Join(coordinator, 2).Accepted!;
        var settings = coordinator.RoomSettings();
        Assert.IsTrue(coordinator.ApplyRoomSettings(new RaceAdminRoomSettingsCommand(
            settings.SessionName, 1, settings.SectorCount, settings.AutomaticYellowEnabled,
            settings.SlowSpeedKph, settings.SlowDurationSeconds, settings.SevereLateralOffsetMeters,
            settings.RecoveryDurationSeconds, settings.AllowTeams, settings.TrackName, settings.TrackId,
            settings.TrackRevision, settings.TrackPackageHash, settings.TeamCount, settings.DriversPerTeam,
            settings.Teams, settings.TrackLimitMode, 0, true)).IsAccepted);

        var now = DateTimeOffset.UtcNow;
        Assert.IsTrue(coordinator.UpdateTelemetry(other.ParticipantId,
            CollisionTelemetry(1, 101.5, 10, 0, 0), now).IsAccepted);
        Assert.IsTrue(coordinator.UpdateTelemetry(reporter.ParticipantId,
            CollisionTelemetry(2, 100, 20, 1, 4.4) with
            {
                ImpactSpeedLossMps = 2.2,
                ImpactWorldX = 100,
                ImpactWorldY = 0,
                ImpactWorldZ = 50,
                ImpactAgeMilliseconds = 80
            }, now.AddMilliseconds(80)).IsAccepted);
        Assert.IsEmpty(coordinator.Snapshot().Investigations!, "大厅不得创建碰撞调查。");

        Assert.IsTrue(coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Race, null, 1, 0, null), now.AddSeconds(1)).IsAccepted);
        Assert.IsTrue(coordinator.UpdateTelemetry(reporter.ParticipantId,
            CollisionTelemetry(3, 100, 20, 0, 0), now.AddMilliseconds(1_700)).IsAccepted);
        Assert.IsTrue(coordinator.UpdateTelemetry(other.ParticipantId,
            CollisionTelemetry(4, 105, 10, 0, 0), now.AddMilliseconds(1_700)).IsAccepted);
        Assert.IsTrue(coordinator.UpdateTelemetry(other.ParticipantId,
            CollisionTelemetry(5, 101.5, 10, 0, 0), now.AddSeconds(2)).IsAccepted);
        Assert.IsTrue(coordinator.UpdateTelemetry(reporter.ParticipantId,
            CollisionTelemetry(6, 100, 20, 3, 4.4) with
            {
                ImpactSpeedLossMps = 2.2,
                ImpactWorldX = 100,
                ImpactWorldY = 0,
                ImpactWorldZ = 50,
                ImpactAgeMilliseconds = 80
            }, now.AddSeconds(2).AddMilliseconds(80)).IsAccepted);

        var pending = coordinator.Snapshot().Investigations!.Single();
        CollectionAssert.AreEquivalent(
            new[] { reporter.ParticipantId, other.ParticipantId },
            pending.RelatedParticipantIds!.ToArray());
        Assert.IsNotNull(pending.CollisionEvidence);
        Assert.AreEqual(1.5, pending.CollisionEvidence.HorizontalDistanceMeters, .15);
        Assert.IsFalse(pending.CollisionEvidence.BothDriversReportedImpact,
            "服务端不应再要求双方客户端同时识别冲击。");
        Assert.IsTrue(pending.CollisionEvidence.ApproachDistanceReductionMeters >= 3);
        Assert.IsTrue(pending.Offense.Contains("不代表责任判定", StringComparison.Ordinal));
        Assert.IsTrue(coordinator.SetAutomaticCollisionInvestigations(false).IsAccepted);
        Assert.IsFalse(coordinator.RoomSettings().AutomaticCollisionInvestigationsEnabled);
        Assert.HasCount(1, coordinator.Snapshot().Investigations!, "赛中关闭检测不得删除已有调查。");

        CompleteLap(coordinator, reporter.ParticipantId, 1, 60, now.AddMinutes(1));
        coordinator.Disconnect(other.ParticipantId);
        Assert.AreEqual(RaceSessionPhase.Finished, coordinator.Snapshot().Phase);
        Assert.IsTrue(coordinator.ResolveInvestigation(new RaceAdminInvestigationCommand(
            pending.Id, true, RacePenaltyKind.Time, 4, "赛后复核确认责任", other.ParticipantId)).IsAccepted);
        var finished = coordinator.Snapshot();
        Assert.AreEqual(RaceInvestigationStatus.Penalized, finished.Investigations!.Single().Status);
        Assert.IsTrue(finished.Penalties!.Any(item =>
            item.ParticipantId == other.ParticipantId && item.IsPostRaceAdjustment && item.ValueSeconds == 4));
    }

    [TestMethod]
    public void CollisionInvestigationRejectsFarStalePitAndDuplicateEvidence()
    {
        var coordinator = CreateCoordinator();
        var reporter = Join(coordinator, 1).Accepted!;
        var other = Join(coordinator, 2).Accepted!;
        var settings = coordinator.RoomSettings();
        Assert.IsTrue(coordinator.ApplyRoomSettings(new RaceAdminRoomSettingsCommand(
            settings.SessionName, settings.TotalRaceLaps, settings.SectorCount, settings.AutomaticYellowEnabled,
            settings.SlowSpeedKph, settings.SlowDurationSeconds, settings.SevereLateralOffsetMeters,
            settings.RecoveryDurationSeconds, settings.AllowTeams, settings.TrackName, settings.TrackId,
            settings.TrackRevision, settings.TrackPackageHash, settings.TeamCount, settings.DriversPerTeam,
            settings.Teams, settings.TrackLimitMode, 0, true)).IsAccepted);
        Assert.IsTrue(coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Race, null, 10, 0, null)).IsAccepted);
        var now = DateTimeOffset.UtcNow;
        coordinator.UpdateTelemetry(other.ParticipantId, CollisionTelemetry(1, 150, 0, 0, 0), now);
        coordinator.UpdateTelemetry(reporter.ParticipantId, CollisionTelemetry(2, 100, 20, 1, 5) with
        {
            ImpactWorldX = 100, ImpactWorldZ = 50, ImpactAgeMilliseconds = 50
        }, now.AddMilliseconds(50));
        Assert.IsEmpty(coordinator.Snapshot().Investigations!);

        coordinator.UpdateTelemetry(other.ParticipantId, CollisionTelemetry(3, 101, 0, 0, 0) with
        {
            IsInPitLane = true, IsOnPitRoute = true
        }, now.AddSeconds(1));
        coordinator.UpdateTelemetry(reporter.ParticipantId, CollisionTelemetry(4, 100, 20, 2, 5) with
        {
            ImpactWorldX = 100, ImpactWorldZ = 50, ImpactAgeMilliseconds = 50
        }, now.AddSeconds(1).AddMilliseconds(50));
        Assert.IsEmpty(coordinator.Snapshot().Investigations!);

        coordinator.UpdateTelemetry(other.ParticipantId, CollisionTelemetry(5, 101, 0, 1, 3.4) with
        {
            ImpactWorldX = 101, ImpactWorldZ = 50, ImpactAgeMilliseconds = 40
        }, now.AddSeconds(2));
        var impact = CollisionTelemetry(6, 100, 20, 3, 5) with
        {
            ImpactWorldX = 100, ImpactWorldZ = 50, ImpactAgeMilliseconds = 50
        };
        coordinator.UpdateTelemetry(reporter.ParticipantId, impact, now.AddSeconds(2).AddMilliseconds(50));
        coordinator.UpdateTelemetry(reporter.ParticipantId, impact, now.AddSeconds(2).AddMilliseconds(150));
        Assert.HasCount(1, coordinator.Snapshot().Investigations!);
    }

    [TestMethod]
    public void RepeatedContactsForTheSameDriverPairAreGroupedIntoOneInvestigation()
    {
        var coordinator = CreateCoordinator();
        var reporter = Join(coordinator, 1).Accepted!;
        var other = Join(coordinator, 2).Accepted!;
        var settings = coordinator.RoomSettings();
        Assert.IsTrue(coordinator.ApplyRoomSettings(new RaceAdminRoomSettingsCommand(
            settings.SessionName, settings.TotalRaceLaps, settings.SectorCount, settings.AutomaticYellowEnabled,
            settings.SlowSpeedKph, settings.SlowDurationSeconds, settings.SevereLateralOffsetMeters,
            settings.RecoveryDurationSeconds, settings.AllowTeams, settings.TrackName, settings.TrackId,
            settings.TrackRevision, settings.TrackPackageHash, settings.TeamCount, settings.DriversPerTeam,
            settings.Teams, settings.TrackLimitMode, 0, true)).IsAccepted);
        var started = DateTimeOffset.Parse("2026-08-20T11:00:00Z");
        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Race, null, 10, null, null), started);

        coordinator.UpdateTelemetry(other.ParticipantId,
            CollisionTelemetry(1, 101, 10, 1, 3.5), started.AddSeconds(1));
        coordinator.UpdateTelemetry(reporter.ParticipantId,
            CollisionTelemetry(2, 100, 20, 1, 4.5) with { ImpactAgeMilliseconds = 50 },
            started.AddMilliseconds(1_050));
        var first = coordinator.Snapshot().Investigations!.Single();
        var firstBannerId = coordinator.Snapshot().Banner?.Id;

        coordinator.UpdateTelemetry(other.ParticipantId,
            CollisionTelemetry(3, 100.8, 9, 2, 4), started.AddSeconds(3));
        coordinator.UpdateTelemetry(reporter.ParticipantId,
            CollisionTelemetry(4, 100, 21, 2, 6) with { ImpactAgeMilliseconds = 50 },
            started.AddMilliseconds(3_050));

        var grouped = coordinator.Snapshot().Investigations!.Single();
        Assert.AreEqual(first.Id, grouped.Id);
        Assert.IsNotNull(grouped.CollisionEvidence);
        Assert.AreEqual(2, grouped.CollisionEvidence.ContactCount);
        Assert.AreEqual(6, grouped.CollisionEvidence.ImpactMagnitudeMps, .001);
        Assert.AreEqual(.8, grouped.CollisionEvidence.HorizontalDistanceMeters, .15);
        Assert.IsTrue(grouped.CollisionEvidence.LastIncidentAt > grouped.CollisionEvidence.IncidentAt);
        Assert.IsTrue(grouped.Offense.Contains("连续疑似车辆接触（2 次", StringComparison.Ordinal));
        Assert.AreEqual(firstBannerId, coordinator.Snapshot().Banner?.Id,
            "归并后不得再次创建通知横幅。");
        Assert.AreEqual(1, coordinator.Events().Count(item => item.Type == "collisionInvestigationOpened"),
            "归并后的连续接触不得重复写入新的调查事件。");

        coordinator.UpdateTelemetry(other.ParticipantId,
            CollisionTelemetry(5, 101, 10, 3, 4), started.AddSeconds(16));
        coordinator.UpdateTelemetry(reporter.ParticipantId,
            CollisionTelemetry(6, 100, 20, 3, 5) with { ImpactAgeMilliseconds = 50 },
            started.AddMilliseconds(16_050));
        Assert.HasCount(2, coordinator.Snapshot().Investigations!,
            "超过 12 秒分组窗口后的再次接触应建立新调查。");
        Assert.AreEqual(2, coordinator.Events().Count(item => item.Type == "collisionInvestigationOpened"));
    }

    [TestMethod]
    public void CollisionInvestigationRejectsOfficialSmashableObjectEvidence()
    {
        var coordinator = CreateCoordinator();
        var reporter = Join(coordinator, 1).Accepted!;
        var other = Join(coordinator, 2).Accepted!;
        var settings = coordinator.RoomSettings();
        Assert.IsTrue(coordinator.ApplyRoomSettings(new RaceAdminRoomSettingsCommand(
            settings.SessionName, settings.TotalRaceLaps, settings.SectorCount, settings.AutomaticYellowEnabled,
            settings.SlowSpeedKph, settings.SlowDurationSeconds, settings.SevereLateralOffsetMeters,
            settings.RecoveryDurationSeconds, settings.AllowTeams, settings.TrackName, settings.TrackId,
            settings.TrackRevision, settings.TrackPackageHash, settings.TeamCount, settings.DriversPerTeam,
            settings.Teams, settings.TrackLimitMode, 0, true)).IsAccepted);
        var now = DateTimeOffset.Parse("2026-08-20T12:00:00Z");
        Assert.IsTrue(coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Race, null, 5, null, null), now).IsAccepted);

        coordinator.UpdateTelemetry(reporter.ParticipantId,
            CollisionTelemetry(1, 100, 20, 0, 0), now.AddMilliseconds(100));
        coordinator.UpdateTelemetry(other.ParticipantId,
            CollisionTelemetry(2, 105, 10, 0, 0), now.AddMilliseconds(100));
        coordinator.UpdateTelemetry(other.ParticipantId,
            CollisionTelemetry(3, 101.5, 10, 0, 0), now.AddMilliseconds(400));
        coordinator.UpdateTelemetry(reporter.ParticipantId,
            CollisionTelemetry(4, 100, 20, 1, 5) with
            {
                ImpactAgeMilliseconds = 80,
                ImpactSpeedLossMps = 2,
                ImpactSmashableVelDiff = 5,
                ImpactSmashableMass = 25
            },
            now.AddMilliseconds(480));

        Assert.IsEmpty(coordinator.Snapshot().Investigations!,
            "官方可破坏物碰撞字段存在时，不得把事件标记为车碰车。");
    }

    [TestMethod]
    public void CollisionInvestigationRejectsParallelBrakingWithoutAnApproachTrajectory()
    {
        var coordinator = CreateCoordinator();
        var reporter = Join(coordinator, 1).Accepted!;
        var other = Join(coordinator, 2).Accepted!;
        var settings = coordinator.RoomSettings();
        Assert.IsTrue(coordinator.ApplyRoomSettings(new RaceAdminRoomSettingsCommand(
            settings.SessionName, settings.TotalRaceLaps, settings.SectorCount, settings.AutomaticYellowEnabled,
            settings.SlowSpeedKph, settings.SlowDurationSeconds, settings.SevereLateralOffsetMeters,
            settings.RecoveryDurationSeconds, settings.AllowTeams, settings.TrackName, settings.TrackId,
            settings.TrackRevision, settings.TrackPackageHash, settings.TeamCount, settings.DriversPerTeam,
            settings.Teams, settings.TrackLimitMode, 0, true)).IsAccepted);
        var now = DateTimeOffset.Parse("2026-08-20T12:10:00Z");
        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Race, null, 5, null, null), now);

        coordinator.UpdateTelemetry(reporter.ParticipantId,
            CollisionTelemetry(1, 100, 20, 0, 0), now.AddMilliseconds(100));
        coordinator.UpdateTelemetry(other.ParticipantId,
            CollisionTelemetry(2, 102, 20, 0, 0), now.AddMilliseconds(100));
        coordinator.UpdateTelemetry(other.ParticipantId,
            CollisionTelemetry(3, 102, 20, 0, 0), now.AddMilliseconds(400));
        coordinator.UpdateTelemetry(reporter.ParticipantId,
            CollisionTelemetry(4, 100, 20, 1, 4) with
            {
                ImpactAgeMilliseconds = 80,
                ImpactSpeedLossMps = 2
            },
            now.AddMilliseconds(480));

        Assert.IsEmpty(coordinator.Snapshot().Investigations!,
            "两车平行行驶且距离未收窄时，单车制动突变不能构成碰撞调查。");
    }

    [TestMethod]
    public void TwelveDriverSnapshotFitsQuarterSecondBroadcastBudget()
    {
        var coordinator = CreateCoordinator();
        var participants = Enumerable.Range(1, RaceProtocol.MaximumParticipants)
            .Select(index => Join(coordinator, index).Accepted!)
            .ToArray();
        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Race, null, 10, null, null));
        for (var index = 0; index < participants.Length; index++)
            coordinator.UpdateTelemetry(participants[index].ParticipantId,
                Telemetry(2, index / (double)participants.Length));

        var message = RaceProtocolJson.Serialize(RaceMessageTypes.Snapshot, 1, coordinator.Snapshot());
        var bytes = Encoding.UTF8.GetByteCount(message);
        var egressBytesPerSecond = bytes * RaceProtocol.MaximumParticipants * 4L;
        Console.WriteLine(
            $"12-driver snapshot={bytes} bytes; 4Hz room egress={egressBytesPerSecond} bytes/s");

        Assert.IsTrue(bytes < 64 * 1024, "单个 12 人快照必须保持在协议消息上限内。");
        Assert.IsTrue(egressBytesPerSecond < 3 * 1024 * 1024,
            "每秒四次向 12 个客户端广播不应产生不可接受的单房间带宽。");
    }

    [TestMethod]
    public void HighFrequencyTelemetryCoalescesRoomSnapshotsWithoutDroppingStateUpdates()
    {
        var coordinator = CreateCoordinator();
        var participants = Enumerable.Range(1, RaceProtocol.MaximumParticipants)
            .Select(index => Join(coordinator, index).Accepted!)
            .ToArray();
        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Race, null, 10, null, null));
        var published = 0;
        coordinator.SnapshotChanged += _ => Interlocked.Increment(ref published);

        var allocationStart = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        for (var sample = 0; sample < 10; sample++)
        for (var index = 0; index < participants.Length; index++)
            coordinator.UpdateTelemetry(
                participants[index].ParticipantId,
                Telemetry(0, (sample * participants.Length + index) / 120d));
        timer.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        Console.WriteLine(
            $"12-driver telemetry burst: 120 packets in {timer.Elapsed.TotalMilliseconds:0.###} ms, " +
            $"{allocated / 120d:0.##} B/packet, {Volatile.Read(ref published)} room snapshots");

        Assert.IsTrue(Volatile.Read(ref published) < 12,
            "120 个紧邻遥测包不应触发 120 次整房快照重建。 ");
        Assert.IsTrue(timer.Elapsed < TimeSpan.FromMilliseconds(500),
            "12 人各 10 个遥测包必须在宽松的实时预算内处理完成。 ");
        Assert.IsTrue(allocated < 16 * 1024 * 1024,
            "高频遥测合并路径不应产生大规模瞬时分配。 ");
        var beforeNextWindow = Volatile.Read(ref published);
        Thread.Sleep(120);
        coordinator.UpdateTelemetry(participants[0].ParticipantId, Telemetry(0, .75));
        Assert.IsTrue(Volatile.Read(ref published) > beforeNextWindow,
            "进入下一广播窗口后必须发布包含全部累计状态的快照。 ");
        Assert.AreEqual(.75, coordinator.Snapshot().Participants
            .Single(item => item.Id == participants[0].ParticipantId).TrackProgress, 0.0001);
    }

    [TestMethod]
    public void SupportsEveryRoomSizeFromTwoThroughTwelveAndRejectsThirteenthDriver()
    {
        for (var expected = 2; expected <= RaceProtocol.MaximumParticipants; expected++)
        {
            var coordinator = CreateCoordinator(expected);
            for (var index = 1; index <= expected; index++)
            {
                var joined = Join(coordinator, index);
                Assert.IsTrue(joined.IsAccepted, $"Driver {index} should join a {expected}-driver room.");
            }

            Assert.AreEqual(expected, coordinator.Snapshot().Participants.Count);
            var overflow = Join(coordinator, expected + 1);
            Assert.IsFalse(overflow.IsAccepted);
            Assert.AreEqual("roomFull", overflow.Rejected?.Code);
        }
    }

    [TestMethod]
    public void ObserverJoinsDuringRaceWithoutTakingDriverSlotAndIsRemovedOnDisconnect()
    {
        var coordinator = CreateCoordinator();
        for (var index = 1; index <= RaceProtocol.MaximumParticipants; index++)
            Assert.IsTrue(Join(coordinator, index).IsAccepted);
        Assert.IsTrue(coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Race, null, 5, null, null)).IsAccepted);

        var observer = coordinator.TryJoin(Login(99) with
        {
            DisplayName = "转播席 A",
            TeamName = null,
            TeamId = null,
            IsObserver = true
        });

        Assert.IsTrue(observer.IsAccepted);
        Assert.IsTrue(observer.Accepted!.IsObserver);
        var snapshot = coordinator.Snapshot();
        Assert.HasCount(RaceProtocol.MaximumParticipants, snapshot.Participants);
        Assert.HasCount(1, snapshot.Observers!);
        Assert.AreEqual("转播席 A", snapshot.Observers![0].DisplayName);
        Assert.IsFalse(coordinator.SetReady(observer.Accepted.ParticipantId, true).IsAccepted);

        var resumed = coordinator.TryJoin(Login(100) with
        {
            DisplayName = "转播席 A",
            TeamName = null,
            TeamId = null,
            ResumeToken = observer.Accepted.ResumeToken,
            IsObserver = true
        });
        Assert.IsTrue(resumed.IsAccepted);
        Assert.HasCount(1, coordinator.Snapshot().Observers!);

        coordinator.Disconnect(observer.Accepted.ParticipantId);
        Assert.IsEmpty(coordinator.Snapshot().Observers!);
        Assert.IsTrue(coordinator.Events().Any(item => item.Type == "observerDisconnected"));
    }

    [TestMethod]
    public void EventFeedCanReturnOnlyItemsAfterTheLastRenderedSequence()
    {
        var coordinator = CreateCoordinator();
        Join(coordinator, 1);
        var firstSequence = coordinator.Events().Max(item => item.Sequence);

        Join(coordinator, 2);
        var incremental = coordinator.Events(200, firstSequence);

        Assert.IsNotEmpty(incremental);
        Assert.IsTrue(incremental.All(item => item.Sequence > firstSequence));
        Assert.AreEqual(coordinator.Events().Max(item => item.Sequence), incremental.Max(item => item.Sequence));
    }

    [TestMethod]
    public void PasswordProfileDuplicateNameAndVisualTeamAreValidated()
    {
        var coordinator = CreateCoordinator();
        var invalidPassword = coordinator.TryJoin(Login(1) with { Password = "bad-password" });
        Assert.IsFalse(invalidPassword.IsAccepted);
        Assert.AreEqual("invalidPassword", invalidPassword.Rejected?.Code);

        var first = coordinator.TryJoin(Login(1) with { ThemeColor = "#19bde0" });
        Assert.IsTrue(first.IsAccepted);
        var participant = coordinator.Snapshot().Participants.Single();
        Assert.AreEqual("#19BDE0", participant.ThemeColor);
        Assert.AreEqual("车队 1", participant.TeamName);
        Assert.AreEqual("team-1", participant.TeamId);
        Assert.AreEqual("#42D7E8", participant.TeamColor);

        var duplicate = coordinator.TryJoin(Login(2) with { DisplayName = "车手 1" });
        Assert.IsFalse(duplicate.IsAccepted);
        Assert.AreEqual("duplicateName", duplicate.Rejected?.Code);
    }

    [TestMethod]
    public void RoomCanDisableTeamsAndRequireConfiguredTrackIdentity()
    {
        var coordinator = CreateCoordinator();
        var trackId = Guid.NewGuid();
        var hash = new string('A', 64);
        Assert.IsTrue(coordinator.ApplyRoomSettings(new RaceAdminRoomSettingsCommand(
            "指定赛道", 5, 3, true, 12, 3, 25, 3,
            false, "测试环道", trackId.ToString("D"), null, hash)).IsAccepted);

        var wrong = coordinator.TryJoin(Login(1));
        Assert.IsFalse(wrong.IsAccepted);
        Assert.AreEqual("trackMismatch", wrong.Rejected?.Code);
        var joined = coordinator.TryJoin(Login(1) with
        {
            TrackId = trackId.ToString("D"),
            TrackPackageHash = hash,
            SectorCount = 3,
            TeamName = "不应保留"
        });
        Assert.IsTrue(joined.IsAccepted);
        var snapshot = coordinator.Snapshot();
        Assert.IsFalse(snapshot.AllowTeams);
        Assert.AreEqual("测试环道", snapshot.TrackName);
        Assert.IsNull(snapshot.Participants.Single().TeamName);
    }

    [TestMethod]
    public void SoloRaceAndAutomaticBlueFlagAreSupported()
    {
        var solo = CreateCoordinator();
        Join(solo, 1);
        Assert.IsTrue(solo.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Race, null, 3, null, null)).IsAccepted);

        var coordinator = CreateCoordinator();
        var approaching = Join(coordinator, 1).Accepted!;
        var recipient = Join(coordinator, 2).Accepted!;
        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(RaceSessionPhase.Race, null, 5, null, null));
        CompleteLap(coordinator, approaching.ParticipantId, 1, 60);
        coordinator.UpdateTelemetry(approaching.ParticipantId, Telemetry(0, .40));
        coordinator.UpdateTelemetry(recipient.ParticipantId, Telemetry(0, .48));
        var blue = coordinator.Snapshot().BlueFlags!.Single();
        Assert.AreEqual(recipient.ParticipantId, blue.RecipientParticipantId);
        Assert.AreEqual(approaching.ParticipantId, blue.ApproachingParticipantId);
    }

    [TestMethod]
    public void QualifyingUsesAutomaticYellowWithoutCreatingFlagBanner()
    {
        var coordinator = CreateCoordinator();
        var participant = Join(coordinator, 1).Accepted!;
        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Qualifying, null, null, null, 10));
        var started = DateTimeOffset.Parse("2026-08-03T12:00:00Z");
        var slow = Telemetry(0, .3) with { SpeedKph = 5, CurrentSector = 1 };
        coordinator.UpdateTelemetry(participant.ParticipantId, slow, started);
        coordinator.UpdateTelemetry(participant.ParticipantId, slow, started.AddSeconds(3.1));
        var snapshot = coordinator.Snapshot();
        Assert.AreEqual(RaceControlFlag.Yellow, snapshot.Flag);
        Assert.AreNotEqual(RaceBannerKind.YellowFlag, snapshot.Banner?.Kind);
        Assert.IsNotNull(snapshot.QualifyingEndsAt);
    }

    [TestMethod]
    public void QualifyingRanksByBestLapAndPublishesSessionFastestBanner()
    {
        var coordinator = CreateCoordinator();
        var first = Join(coordinator, 1).Accepted!;
        var second = Join(coordinator, 2).Accepted!;
        Assert.IsTrue(coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Qualifying, "排位", 8, null, 10)).IsAccepted);

        CompleteLap(coordinator, first.ParticipantId, 1, 70.250);
        CompleteLap(coordinator, second.ParticipantId, 1, 69.800);

        var snapshot = coordinator.Snapshot();
        Assert.AreEqual(second.ParticipantId, snapshot.Participants[0].Id);
        Assert.IsNotNull(snapshot.FastestLapSeconds);
        Assert.AreEqual(69.800, snapshot.FastestLapSeconds.GetValueOrDefault(), 0.0001);
        Assert.AreEqual(RaceBannerKind.FastestLap, snapshot.Banner?.Kind);
        Assert.AreEqual(second.ParticipantId, snapshot.FastestParticipantId);
        Assert.AreEqual(3, snapshot.FastestSectorSeconds.Count);
        Assert.AreEqual(69.800 / 3, snapshot.FastestSectorSeconds[0].GetValueOrDefault(), 0.0001);
        Assert.AreEqual(3, snapshot.Participants[0].BestSectorSeconds.Count);
        Assert.IsNotNull(snapshot.Participants[1].GapToLeaderSeconds);
        Assert.AreEqual(0.450, snapshot.Participants[1].GapToLeaderSeconds.GetValueOrDefault(), 0.0001);
    }

    [TestMethod]
    public void ThreeSessionQualifyingUsesDefaultEliminationsAndBuildsCompleteGrid()
    {
        var coordinator = CreateCoordinator(6);
        var drivers = Enumerable.Range(1, 6)
            .Select(index => Join(coordinator, index).Accepted!)
            .ToArray();
        var started = DateTimeOffset.Parse("2026-08-12T10:00:00Z");
        var qualifyingCommand = new RaceAdminSessionCommand(
            RaceSessionPhase.Qualifying, "三节排位", null, null, 10,
            3, [1, 2, 3], [null, null]);
        Assert.IsTrue(coordinator.ApplySessionCommand(qualifyingCommand, started).IsAccepted);

        var q1 = coordinator.Snapshot(started);
        Assert.AreEqual(1, q1.QualifyingSessionNumber);
        Assert.AreEqual(3, q1.QualifyingSessionCount);
        CollectionAssert.AreEqual(new[] { 2, 1 }, q1.QualifyingEliminationCounts!.ToArray());
        for (var index = 0; index < drivers.Length; index++)
            CompleteLap(coordinator, drivers[index].ParticipantId, 1, 66 - index, started.AddSeconds(20 + index));
        coordinator.Tick(q1.QualifyingEndsAt!.Value.AddMilliseconds(1));

        var q1Complete = coordinator.Snapshot(q1.QualifyingEndsAt!.Value.AddMilliseconds(1));
        Assert.AreEqual(RaceSessionPhase.Qualifying, q1Complete.Phase);
        Assert.AreEqual(1, q1Complete.QualifyingSessionNumber);
        Assert.IsTrue(q1Complete.QualifyingTimeExpired);
        Assert.IsNull(q1Complete.QualifyingEndsAt);
        Assert.AreEqual(4, q1Complete.Participants.Count(item => item.QualifyingEligible));
        Assert.AreEqual(2, q1Complete.Participants.Count(item => item.QualifyingEliminatedInSession == 1));

        var q2StartedAt = q1.QualifyingEndsAt.Value.AddSeconds(5);
        Assert.IsTrue(coordinator.ApplySessionCommand(qualifyingCommand, q2StartedAt).IsAccepted);
        var q2 = coordinator.Snapshot(q2StartedAt);
        Assert.AreEqual(2, q2.QualifyingSessionNumber);
        Assert.IsFalse(q2.QualifyingTimeExpired);
        Assert.IsTrue(q2.Participants.Where(item => item.QualifyingEligible).All(item => item.BestLapSeconds is null));

        var q2Drivers = drivers.Skip(2).ToArray();
        var q2EndsAt = q2.QualifyingEndsAt.GetValueOrDefault();
        for (var index = 0; index < q2Drivers.Length; index++)
            CompleteLap(coordinator, q2Drivers[index].ParticipantId, 1, 70 - index,
                q2EndsAt.AddMinutes(-1));
        coordinator.Tick(q2EndsAt.AddMilliseconds(1));

        var q2Complete = coordinator.Snapshot(q2EndsAt.AddMilliseconds(1));
        Assert.AreEqual(2, q2Complete.QualifyingSessionNumber);
        Assert.IsTrue(q2Complete.QualifyingTimeExpired);
        Assert.IsNull(q2Complete.QualifyingEndsAt);
        Assert.AreEqual(3, q2Complete.Participants.Count(item => item.QualifyingEligible));
        Assert.AreEqual(drivers[2].ParticipantId,
            q2Complete.Participants.Single(item => item.QualifyingEliminatedInSession == 2).Id);

        var q3StartedAt = q2EndsAt.AddSeconds(5);
        Assert.IsTrue(coordinator.ApplySessionCommand(qualifyingCommand, q3StartedAt).IsAccepted);
        var q3 = coordinator.Snapshot(q3StartedAt);
        Assert.AreEqual(3, q3.QualifyingSessionNumber);

        var q3Drivers = drivers.Skip(3).ToArray();
        var q3EndsAt = q3.QualifyingEndsAt.GetValueOrDefault();
        for (var index = 0; index < q3Drivers.Length; index++)
            CompleteLap(coordinator, q3Drivers[index].ParticipantId, 1, 64 - index,
                q3EndsAt.AddMinutes(-1));
        coordinator.Tick(q3EndsAt.AddMilliseconds(1));

        var grid = coordinator.Snapshot(q3EndsAt.AddMilliseconds(1));
        Assert.AreEqual(RaceSessionPhase.Grid, grid.Phase);
        CollectionAssert.AreEqual(
            new[]
            {
                drivers[5].ParticipantId, drivers[4].ParticipantId, drivers[3].ParticipantId,
                drivers[2].ParticipantId, drivers[1].ParticipantId, drivers[0].ParticipantId
            },
            grid.Participants.Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(new double?[] { 61, 67, 62 },
            grid.Participants[0].QualifyingSessionBestLapSeconds!.ToArray());
    }

    [DataTestMethod]
    [DataRow(2, 0, 0)]
    [DataRow(3, 1, 0)]
    [DataRow(4, 1, 1)]
    [DataRow(5, 1, 1)]
    [DataRow(6, 2, 1)]
    [DataRow(7, 2, 1)]
    [DataRow(8, 2, 2)]
    [DataRow(9, 2, 2)]
    [DataRow(10, 3, 2)]
    [DataRow(11, 3, 2)]
    [DataRow(12, 3, 3)]
    public void DefaultThreeSessionEliminationRuleCoversEverySupportedFieldSize(
        int participants, int q1Eliminated, int q2Eliminated)
    {
        CollectionAssert.AreEqual(
            new[] { q1Eliminated, q2Eliminated },
            RaceCoordinator.DefaultQualifyingEliminations(participants, 3).ToArray());
    }

    [TestMethod]
    public void SingleSessionQualifyingRetainsLegacyTenMinuteFlow()
    {
        var coordinator = CreateCoordinator();
        Join(coordinator, 1);
        var started = DateTimeOffset.Parse("2026-08-12T11:00:00Z");
        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Qualifying, null, null, null, 10), started);
        var snapshot = coordinator.Snapshot(started);
        Assert.AreEqual(1, snapshot.QualifyingSessionCount);
        Assert.AreEqual(1, snapshot.QualifyingSessionNumber);
        Assert.AreEqual(started.AddMinutes(10), snapshot.QualifyingEndsAt);
        Assert.IsEmpty(snapshot.QualifyingEliminationCounts!);
        coordinator.Tick(snapshot.QualifyingEndsAt!.Value.AddMilliseconds(1));
        Assert.AreEqual(RaceSessionPhase.Grid, coordinator.Snapshot().Phase);
    }

    [TestMethod]
    public void PracticeDefaultsEveryConfiguredSessionToSixtyMinutes()
    {
        var coordinator = CreateCoordinator();
        Join(coordinator, 1);
        var started = DateTimeOffset.Parse("2026-08-12T12:00:00Z");

        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Practice, null, null, null, null), started);

        var snapshot = coordinator.Snapshot(started);
        Assert.AreEqual(RaceSessionPhase.Practice, snapshot.Phase);
        Assert.AreEqual(1, snapshot.PracticeSessionNumber);
        Assert.AreEqual(1, snapshot.PracticeSessionCount);
        CollectionAssert.AreEqual(new[] { 60 }, snapshot.PracticeSessionMinutes!.ToArray());
        Assert.AreEqual(started.AddMinutes(60), snapshot.PracticeEndsAt);

        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Practice, null, null, null, null,
            PracticeSessionCount: 3), started);
        snapshot = coordinator.Snapshot(started);
        CollectionAssert.AreEqual(new[] { 60, 60, 60 }, snapshot.PracticeSessionMinutes!.ToArray());
    }

    [TestMethod]
    public void ThreeSessionPracticeUsesCustomDurationsWithoutEliminatingDrivers()
    {
        var coordinator = CreateCoordinator();
        var first = Join(coordinator, 1).Accepted!;
        var second = Join(coordinator, 2).Accepted!;
        var started = DateTimeOffset.Parse("2026-08-12T13:00:00Z");
        var practiceCommand = new RaceAdminSessionCommand(
            RaceSessionPhase.Practice, "三节练习赛", null, null, null,
            PracticeSessionCount: 3,
            PracticeSessionMinutes: [1, 2, 3]);
        coordinator.ApplySessionCommand(practiceCommand, started);

        var fp1 = coordinator.Snapshot(started);
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, fp1.PracticeSessionMinutes!.ToArray());
        CompleteLap(coordinator, first.ParticipantId, 1, 70, started.AddSeconds(20));
        CompleteLap(coordinator, second.ParticipantId, 1, 71, started.AddSeconds(21));
        coordinator.Tick(fp1.PracticeEndsAt!.Value.AddMilliseconds(1));

        var fp1Complete = coordinator.Snapshot(fp1.PracticeEndsAt.Value.AddMilliseconds(1));
        Assert.AreEqual(1, fp1Complete.PracticeSessionNumber);
        Assert.IsTrue(fp1Complete.PracticeTimeExpired);
        Assert.IsNull(fp1Complete.PracticeEndsAt);
        Assert.AreEqual(70d, fp1Complete.Participants.Single(item => item.Id == first.ParticipantId)
            .PracticeSessionBestLapSeconds![0].GetValueOrDefault(), 0.0001);

        var fp2StartedAt = fp1.PracticeEndsAt.Value.AddSeconds(5);
        Assert.IsTrue(coordinator.ApplySessionCommand(practiceCommand, fp2StartedAt).IsAccepted);
        var fp2 = coordinator.Snapshot(fp2StartedAt);
        Assert.AreEqual(2, fp2.PracticeSessionNumber);
        Assert.AreEqual(2, fp2.Participants.Count);
        Assert.IsTrue(fp2.Participants.All(item => item.BestLapSeconds is null));
        CompleteLap(coordinator, first.ParticipantId, 1, 69, fp2.PracticeEndsAt!.Value.AddMinutes(-1));
        CompleteLap(coordinator, second.ParticipantId, 1, 68, fp2.PracticeEndsAt.Value.AddMinutes(-1));
        coordinator.Tick(fp2.PracticeEndsAt.Value.AddMilliseconds(1));

        var fp2Complete = coordinator.Snapshot(fp2.PracticeEndsAt.Value.AddMilliseconds(1));
        Assert.AreEqual(2, fp2Complete.PracticeSessionNumber);
        Assert.IsTrue(fp2Complete.PracticeTimeExpired);
        Assert.IsNull(fp2Complete.PracticeEndsAt);

        var fp3StartedAt = fp2.PracticeEndsAt.Value.AddSeconds(5);
        Assert.IsTrue(coordinator.ApplySessionCommand(practiceCommand, fp3StartedAt).IsAccepted);
        var fp3 = coordinator.Snapshot(fp3StartedAt);
        Assert.AreEqual(3, fp3.PracticeSessionNumber);
        Assert.IsTrue(fp3.Participants.All(item => item.BestLapSeconds is null));
        CompleteLap(coordinator, first.ParticipantId, 1, 67, fp3.PracticeEndsAt!.Value.AddMinutes(-1));
        coordinator.Tick(fp3.PracticeEndsAt.Value.AddMilliseconds(1));

        var completed = coordinator.Snapshot(fp3.PracticeEndsAt.Value.AddMilliseconds(1));
        Assert.AreEqual(RaceSessionPhase.Practice, completed.Phase);
        Assert.IsTrue(completed.PracticeTimeExpired);
        Assert.IsNull(completed.PracticeEndsAt);
        Assert.IsTrue(completed.Participants.All(item => item.Status == RaceParticipantStatus.Ready));
        CollectionAssert.AreEqual(new double?[] { 70, 69, 67 },
            completed.Participants.Single(item => item.Id == first.ParticipantId)
                .PracticeSessionBestLapSeconds!.ToArray());
    }

    [TestMethod]
    public void StageResultsRemainAvailableAfterReturningToLobby()
    {
        var coordinator = CreateCoordinator();
        var driver = Join(coordinator, 1).Accepted!;
        var started = DateTimeOffset.Parse("2026-08-12T14:00:00Z");

        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Practice, "周末赛事", null, null, null,
            PracticeSessionMinutes: [1]), started);
        var practice = coordinator.Snapshot(started);
        CompleteLap(coordinator, driver.ParticipantId, 1, 70, started.AddSeconds(30));
        coordinator.Tick(practice.PracticeEndsAt!.Value.AddMilliseconds(1));

        var fp = coordinator.Results().Single();
        Assert.AreEqual(RaceSessionPhase.Practice, fp.Phase);
        Assert.AreEqual("练习赛", fp.Label);
        Assert.IsTrue(fp.IsComplete);
        Assert.AreEqual(70d, fp.Participants.Single().BestLapSeconds.GetValueOrDefault(), .0001);
        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Lobby, null, null, null, null), started.AddMinutes(2));
        Assert.HasCount(1, coordinator.Results());

        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Qualifying, null, null, null, 1), started.AddMinutes(3));
        var qualifying = coordinator.Snapshot(started.AddMinutes(3));
        CompleteLap(coordinator, driver.ParticipantId, 1, 68, started.AddMinutes(3).AddSeconds(30));
        coordinator.Tick(qualifying.QualifyingEndsAt!.Value.AddMilliseconds(1));
        Assert.AreEqual(RaceSessionPhase.Grid, coordinator.Snapshot().Phase);
        Assert.AreEqual(RaceSessionPhase.Qualifying, coordinator.Results()[0].Phase);
        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Lobby, null, null, null, null), started.AddMinutes(5));

        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Race, null, 1, 0, null), started.AddMinutes(6));
        CompleteLap(coordinator, driver.ParticipantId, 1, 75,
            started.AddMinutes(7).AddSeconds(15));
        Assert.AreEqual(RaceSessionPhase.Finished, coordinator.Snapshot().Phase);
        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Lobby, null, null, null, null), started.AddMinutes(8));

        var results = coordinator.Results();
        Assert.HasCount(3, results);
        CollectionAssert.AreEqual(
            new[] { RaceSessionPhase.Race, RaceSessionPhase.Qualifying, RaceSessionPhase.Practice },
            results.Select(item => item.Phase).ToArray());
        Assert.AreEqual(75d,
            results[0].Participants.Single().AdjustedRaceTotalSeconds.GetValueOrDefault(), .0001);
    }

    [TestMethod]
    public void RaceOrdersProgressAndShowsPitServiceFlagAndPenalty()
    {
        var coordinator = CreateCoordinator();
        var first = Join(coordinator, 1).Accepted!;
        var second = Join(coordinator, 2).Accepted!;
        Assert.IsTrue(coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Race, "正赛", 5, null, null)).IsAccepted);

        coordinator.UpdateTelemetry(first.ParticipantId, Telemetry(2, 0.15));
        coordinator.UpdateTelemetry(second.ParticipantId, Telemetry(1, 0.92) with
        {
            IsInPitLane = true,
            IsInServiceZone = true
        });

        var snapshot = coordinator.Snapshot();
        Assert.AreEqual(second.ParticipantId, snapshot.Participants[0].Id,
            "普通遥测中的圈数不能改变服务端权威排名。");
        var pitting = snapshot.Participants.Single(item => item.Id == second.ParticipantId);
        Assert.AreEqual(RaceParticipantStatus.InService, pitting.Status);
        Assert.IsTrue(pitting.IsInPitLane);

        coordinator.UpdateTelemetry(second.ParticipantId, Telemetry(1, 0.1) with
        {
            MapX = 9_999,
            MapY = 9_999,
            IsInPitLane = true,
            IsInServiceZone = true,
            IsTelemetryValid = false,
            IsPausedOrRewinding = true,
            PitServiceElapsedSeconds = 5,
            PitServiceRequirementMet = true,
            CompletedPitServices = 1
        });
        pitting = coordinator.Snapshot().Participants.Single(item => item.Id == second.ParticipantId);
        Assert.AreEqual(0.92, pitting.MapX, 0.0001,
            "暂停帧不能覆盖最后可信地图位置。");
        Assert.AreEqual(5, pitting.PitServiceElapsedSeconds, 0.0001,
            "暂停期间的可信维修计时应继续同步。");
        Assert.AreEqual(1, pitting.CompletedPitServices);

        var penalty = coordinator.ApplyPenalty(new RaceAdminPenaltyCommand(
            second.ParticipantId,
            RacePenaltyKind.Time,
            5,
            null,
            "维修区超速"));
        Assert.IsTrue(penalty.IsAccepted);
        snapshot = coordinator.Snapshot();
        Assert.AreEqual(RaceBannerKind.Penalty, snapshot.Banner?.Kind);
        Assert.AreEqual(5, snapshot.Participants.Single(item => item.Id == second.ParticipantId).Penalties.Single().ValueSeconds);

        Assert.IsTrue(coordinator.ApplyPenalty(new RaceAdminPenaltyCommand(
            first.ParticipantId, RacePenaltyKind.Time, 1, null, "轻微违规")).IsAccepted);
        Assert.IsTrue(coordinator.ApplyPenalty(new RaceAdminPenaltyCommand(
            first.ParticipantId, RacePenaltyKind.Time, 6, null, "严重违规")).IsAccepted);
        var manualTimeValues = coordinator.Snapshot().Participants
            .Single(item => item.Id == first.ParticipantId).Penalties
            .Where(item => item.Kind == RacePenaltyKind.Time)
            .Select(item => item.ValueSeconds)
            .ToArray();
        CollectionAssert.AreEqual(new double?[] { 1, 6 }, manualTimeValues);
    }

    [TestMethod]
    public void OnlyUniqueValidLapEventsAdvanceAuthoritativeRaceLaps()
    {
        var coordinator = CreateCoordinator();
        var participant = Join(coordinator, 1).Accepted!;
        _ = Join(coordinator, 2).Accepted!;
        Assert.IsTrue(coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Race, "权威计圈", 2, null, null)).IsAccepted);
        coordinator.UpdateTelemetry(participant.ParticipantId, Telemetry(99, 0.8));
        Assert.AreEqual(0, coordinator.Snapshot().Participants.Single(item => item.Id == participant.ParticipantId).CompletedLaps);

        var invalid = new RaceLapCompleted(
            Guid.NewGuid(), 99, 61, [20, 20, 21], false, "paused", 50_000);
        Assert.IsTrue(coordinator.CompleteLap(participant.ParticipantId, invalid).IsAccepted);
        Assert.AreEqual(0, coordinator.Snapshot().Participants.Single(item => item.Id == participant.ParticipantId).CompletedLaps);

        var eventId = Guid.NewGuid();
        var valid = invalid with { EventId = eventId, IsValid = true, InvalidReason = null };
        Assert.IsTrue(coordinator.CompleteLap(participant.ParticipantId, valid).IsAccepted);
        Assert.IsTrue(coordinator.CompleteLap(participant.ParticipantId, valid).IsAccepted);
        Assert.AreEqual(1, coordinator.Snapshot().Participants.Single(item => item.Id == participant.ParticipantId).CompletedLaps);
        Assert.AreNotEqual(RaceParticipantStatus.Finished,
            coordinator.Snapshot().Participants.Single(item => item.Id == participant.ParticipantId).Status);

        Assert.IsTrue(coordinator.CompleteLap(participant.ParticipantId,
            valid with { EventId = Guid.NewGuid(), LapNumber = 500 }).IsAccepted);
        var finished = coordinator.Snapshot().Participants.Single(item => item.Id == participant.ParticipantId);
        Assert.AreEqual(2, finished.CompletedLaps);
        Assert.AreEqual(RaceParticipantStatus.Finished, finished.Status);
    }

    [TestMethod]
    public void PitServiceProgressIsClampedAndCreditsAtMostOneServicePerIncrement()
    {
        var coordinator = CreateCoordinator();
        var participant = Join(coordinator, 1).Accepted!;
        var update = Telemetry(0, 0.2) with
        {
            IsInPitLane = true,
            IsInServiceZone = true,
            PitServiceElapsedSeconds = 3,
            PitServiceRequirementMet = true,
            CompletedPitServices = 1
        };
        coordinator.UpdateTelemetry(participant.ParticipantId, update);
        coordinator.UpdateTelemetry(participant.ParticipantId, update with { CompletedPitServices = 12 });
        var service = coordinator.Snapshot().Participants.Single();
        Assert.AreEqual(1, service.CompletedPitServices);
        Assert.AreEqual(3, service.PitServiceElapsedSeconds, 0.001);
        Assert.IsTrue(service.PitServiceRequirementMet);

        coordinator.UpdateTelemetry(participant.ParticipantId, update with
        {
            IsInServiceZone = false,
            PitServiceElapsedSeconds = 30,
            CompletedPitServices = 1
        });
        service = coordinator.Snapshot().Participants.Single();
        Assert.AreEqual(0, service.PitServiceElapsedSeconds, 0.001);
        Assert.IsFalse(service.PitServiceRequirementMet);
    }

    [TestMethod]
    public void RedFlagSuspendsRaceAndGreenFlagResumesPreviousPhase()
    {
        var coordinator = CreateCoordinator();
        Join(coordinator, 1);
        Join(coordinator, 2);
        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(RaceSessionPhase.Race, null, 5, null, null));

        coordinator.ApplyFlagCommand(new RaceAdminFlagCommand(RaceControlFlag.Red, "事故清理"));
        Assert.AreEqual(RaceSessionPhase.Suspended, coordinator.Snapshot().Phase);
        Assert.AreEqual(RaceControlFlag.Red, coordinator.Snapshot().Flag);
        Assert.AreNotEqual(RaceBannerKind.RedFlag, coordinator.Snapshot().Banner?.Kind);

        coordinator.ApplyFlagCommand(new RaceAdminFlagCommand(RaceControlFlag.Green, "赛道恢复"));
        Assert.AreEqual(RaceSessionPhase.Race, coordinator.Snapshot().Phase);
        Assert.AreEqual(RaceControlFlag.Green, coordinator.Snapshot().Flag);
    }

    [TestMethod]
    public void ChequeredFlagIsAutomaticAndEachFollowingDriverFinishesAtTheLine()
    {
        var coordinator = CreateCoordinator();
        var leader = Join(coordinator, 1).Accepted!;
        var second = Join(coordinator, 2).Accepted!;
        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(RaceSessionPhase.Race, null, 2, null, null));

        Assert.IsFalse(coordinator.ApplyFlagCommand(
            new RaceAdminFlagCommand(RaceControlFlag.Chequered, "manual")).IsAccepted);
        CompleteLap(coordinator, second.ParticipantId, 1, 65);
        CompleteLap(coordinator, leader.ParticipantId, 1, 64);
        CompleteLap(coordinator, leader.ParticipantId, 2, 64);

        var snapshot = coordinator.Snapshot();
        Assert.AreEqual(RaceControlFlag.Chequered, snapshot.Flag);
        Assert.AreEqual(RaceParticipantStatus.Finished,
            snapshot.Participants.Single(item => item.Id == leader.ParticipantId).Status);
        Assert.AreNotEqual(RaceParticipantStatus.Finished,
            snapshot.Participants.Single(item => item.Id == second.ParticipantId).Status);

        CompleteLap(coordinator, second.ParticipantId, 2, 65);
        snapshot = coordinator.Snapshot();
        Assert.AreEqual(RaceParticipantStatus.Finished,
            snapshot.Participants.Single(item => item.Id == second.ParticipantId).Status);
        Assert.AreEqual(RaceSessionPhase.Finished, snapshot.Phase);
    }

    [TestMethod]
    public void AutomaticSectorYellowRecoversWithoutClearingManualSectorYellow()
    {
        var coordinator = CreateCoordinator();
        var first = Join(coordinator, 1).Accepted!;
        _ = Join(coordinator, 2).Accepted!;
        Assert.IsTrue(coordinator.ApplyRoomSettings(new RaceAdminRoomSettingsCommand(
            "黄旗测试", 5, 3, true, 12, 3, 25, 3, false)).IsAccepted);
        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(RaceSessionPhase.Race, null, 5, null, null));
        var started = DateTimeOffset.Parse("2026-08-03T12:00:00Z");
        var slow = Telemetry(0, .3) with { SpeedKph = 5, CurrentSector = 1 };
        coordinator.UpdateTelemetry(first.ParticipantId, slow, started);
        coordinator.UpdateTelemetry(first.ParticipantId, slow, started.AddSeconds(3.1));
        var snapshot = coordinator.Snapshot();
        Assert.AreEqual(RaceControlFlag.Yellow, snapshot.Flag);
        Assert.IsTrue(snapshot.YellowZones!.Any(zone => zone.IsAutomatic && zone.SectorIndex == 1));

        coordinator.ApplyFlagCommand(new RaceAdminFlagCommand(RaceControlFlag.Yellow, "人工管制", 2));
        var recovered = slow with { SpeedKph = 100, LateralOffsetMeters = 0 };
        coordinator.UpdateTelemetry(first.ParticipantId, recovered, started.AddSeconds(4));
        coordinator.UpdateTelemetry(first.ParticipantId, recovered, started.AddSeconds(7.1));
        snapshot = coordinator.Snapshot();
        Assert.IsFalse(snapshot.YellowZones!.Any(zone => zone.IsAutomatic));
        Assert.IsTrue(snapshot.YellowZones!.Any(zone => !zone.IsAutomatic && zone.SectorIndex == 2));
        Assert.AreEqual(RaceControlFlag.Yellow, snapshot.Flag);

        coordinator.ApplyFlagCommand(new RaceAdminFlagCommand(RaceControlFlag.Green, "分区恢复", 2));
        Assert.AreEqual(RaceControlFlag.Green, coordinator.Snapshot().Flag);
    }

    [TestMethod]
    public void HazardsInTwoSectorsEscalateAutomaticYellowToFullCourse()
    {
        var coordinator = CreateCoordinator();
        var first = Join(coordinator, 1).Accepted!;
        var second = Join(coordinator, 2).Accepted!;
        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Race, null, 5, null, null));
        var started = DateTimeOffset.Parse("2026-08-03T12:00:00Z");
        var slow = Telemetry(0, .3) with { SpeedKph = 5 };

        coordinator.UpdateTelemetry(first.ParticipantId, slow with { CurrentSector = 0 }, started);
        coordinator.UpdateTelemetry(second.ParticipantId, slow with { CurrentSector = 2 }, started);
        coordinator.UpdateTelemetry(first.ParticipantId, slow with { CurrentSector = 0 }, started.AddSeconds(3.1));
        coordinator.UpdateTelemetry(second.ParticipantId, slow with { CurrentSector = 2 }, started.AddSeconds(3.1));

        var snapshot = coordinator.Snapshot();
        Assert.AreEqual(RaceControlFlag.Yellow, snapshot.Flag);
        Assert.IsTrue(snapshot.YellowZones!.Any(zone =>
            zone.IsAutomatic && zone.SectorIndex is null));
        Assert.IsTrue(snapshot.YellowZones!.Any(zone => zone.SectorIndex == 0));
        Assert.IsTrue(snapshot.YellowZones!.Any(zone => zone.SectorIndex == 2));
    }

    [TestMethod]
    public void PausedOrRewindingTelemetryDoesNotReplaceLastValidPosition()
    {
        var coordinator = CreateCoordinator();
        var participant = Join(coordinator, 1).Accepted!;
        coordinator.UpdateTelemetry(participant.ParticipantId, Telemetry(0, 0.42));
        coordinator.UpdateTelemetry(participant.ParticipantId, Telemetry(0, 0.90) with
        {
            IsPausedOrRewinding = true,
            IsTelemetryValid = false
        });
        Assert.AreEqual(0.42, coordinator.Snapshot().Participants.Single().TrackProgress, 0.0001);
    }

    [TestMethod]
    public void ResumeTokenRestoresSameParticipantInsteadOfConsumingRoomSlot()
    {
        var coordinator = CreateCoordinator();
        var joined = Join(coordinator, 1).Accepted!;
        coordinator.Disconnect(joined.ParticipantId);
        var resumed = coordinator.TryJoin(Login(1) with
        {
            ResumeToken = joined.ResumeToken,
            ThemeColor = "#AA55CC"
        });

        Assert.IsTrue(resumed.IsAccepted);
        Assert.AreEqual(joined.ParticipantId, resumed.Accepted?.ParticipantId);
        Assert.AreEqual(1, coordinator.Snapshot().Participants.Count);
        Assert.AreEqual("#AA55CC", coordinator.Snapshot().Participants.Single().ThemeColor);
    }

    [TestMethod]
    public void DisconnectAfterChequeredDoesNotBlockRaceFinishOrReopenFinishedDriver()
    {
        var coordinator = CreateCoordinator();
        var leader = Join(coordinator, 1).Accepted!;
        var trailing = Join(coordinator, 2).Accepted!;
        var started = DateTimeOffset.Parse("2026-08-13T10:00:00Z");
        Assert.IsTrue(coordinator.ApplySessionCommand(
            new RaceAdminSessionCommand(RaceSessionPhase.Race, "掉线完赛测试", 2, null, null),
            started).IsAccepted);
        CompleteLap(coordinator, leader.ParticipantId, 1, 60, started.AddSeconds(60));
        CompleteLap(coordinator, trailing.ParticipantId, 1, 61, started.AddSeconds(61));
        CompleteLap(coordinator, leader.ParticipantId, 2, 60, started.AddSeconds(120));

        coordinator.Disconnect(trailing.ParticipantId);
        var finished = coordinator.Snapshot(started.AddSeconds(122));
        Assert.AreEqual(RaceSessionPhase.Finished, finished.Phase);
        Assert.AreEqual(RaceParticipantStatus.Finished,
            finished.Participants.Single(item => item.Id == leader.ParticipantId).Status);

        var resumed = coordinator.TryJoin(Login(2) with { ResumeToken = trailing.ResumeToken });
        Assert.IsTrue(resumed.IsAccepted);
        Assert.AreEqual(RaceParticipantStatus.DidNotFinish,
            coordinator.Snapshot().Participants.Single(item => item.Id == trailing.ParticipantId).Status,
            "方格旗后的掉线重连不能重新进入待完赛集合。 ");
        Assert.AreEqual(RaceSessionPhase.Finished, coordinator.Snapshot().Phase);
    }

    [TestMethod]
    public void RaceControlDisconnectReleasesNameAndSlotWithoutDeletingAuditHistory()
    {
        var coordinator = CreateCoordinator(maximumParticipants: 1);
        var joined = Join(coordinator, 1).Accepted!;
        Assert.IsTrue(coordinator.DisconnectAndReleaseClient(joined.ParticipantId).IsAccepted);
        Assert.AreEqual(0, coordinator.Snapshot().Participants.Count);

        var kickedClientRetry = coordinator.TryJoin(Login(1) with { ResumeToken = joined.ResumeToken });
        Assert.IsFalse(kickedClientRetry.IsAccepted,
            "被总控断开的旧客户端不能依靠自动重连立即夺回名称。 ");
        Assert.AreEqual("disconnectedByControl", kickedClientRetry.Rejected?.Code);

        var replacement = coordinator.TryJoin(Login(1));
        Assert.IsTrue(replacement.IsAccepted,
            "总控主动断开后，相同显示名应能立即重新加入且不再占用房间席位。 ");
        Assert.AreNotEqual(joined.ParticipantId, replacement.Accepted?.ParticipantId);
        Assert.IsTrue(coordinator.Events().Any(item =>
            item.Type == "participantRemoved" && item.ParticipantId == joined.ParticipantId));
    }

    [TestMethod]
    public void RacePreparationRunsOutLapFormationLapAndFiveLightSequence()
    {
        var coordinator = CreateCoordinator();
        _ = Join(coordinator, 1).Accepted!;
        Assert.IsTrue(coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.OutLap, "出场圈", 5, null, null)).IsAccepted);
        Assert.AreEqual(RaceSessionPhase.OutLap, coordinator.Snapshot().Phase);
        Assert.IsTrue(coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.FormationLap, "暖胎圈", 5, null, null)).IsAccepted);

        Assert.IsTrue(coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Countdown, "起跑程序", 5, 0, null)).IsAccepted);
        var snapshot = coordinator.Snapshot();
        var issuedAt = snapshot.StartSequenceAt!.Value;
        Assert.AreEqual(RaceSessionPhase.Countdown, snapshot.Phase);
        Assert.AreEqual(issuedAt, snapshot.StartSequenceAt);
        Assert.IsNotNull(snapshot.StartsAt);
        var sequenceDuration = snapshot.StartsAt!.Value - snapshot.StartSequenceAt!.Value;
        Assert.IsTrue(sequenceDuration >= TimeSpan.FromSeconds(5));
        Assert.IsTrue(sequenceDuration <= TimeSpan.FromSeconds(8));

        coordinator.Tick(issuedAt);
        Assert.AreEqual(1, coordinator.Snapshot().IlluminatedStartLights);
        coordinator.Tick(issuedAt.AddSeconds(1.1));
        Assert.AreEqual(2, coordinator.Snapshot().IlluminatedStartLights);
        coordinator.Tick(issuedAt.AddSeconds(4.1));
        Assert.AreEqual(5, coordinator.Snapshot().IlluminatedStartLights);
        coordinator.Tick(snapshot.StartsAt.Value.AddMilliseconds(1));
        snapshot = coordinator.Snapshot();
        Assert.AreEqual(RaceSessionPhase.Race, snapshot.Phase);
        Assert.IsTrue(snapshot.StartLightsOut);
        Assert.AreEqual(0, snapshot.IlluminatedStartLights);
    }

    [TestMethod]
    public void MovingAfterFirstRedLightIsAutomaticallyPenalizedAsFalseStart()
    {
        var coordinator = CreateCoordinator();
        var participant = Join(coordinator, 1).Accepted!;
        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Countdown, null, 5, 0, null));
        var issuedAt = coordinator.Snapshot().StartSequenceAt!.Value;
        coordinator.Tick(issuedAt);

        coordinator.UpdateTelemetry(participant.ParticipantId,
            Telemetry(0, .30) with { SpeedKph = 18 }, issuedAt.AddMilliseconds(50));
        var snapshot = coordinator.Snapshot();
        var penalty = snapshot.Participants.Single().Penalties.Single();
        Assert.AreEqual(RacePenaltyKind.Time, penalty.Kind);
        Assert.AreEqual(5, penalty.ValueSeconds);
        StringAssert.Contains(penalty.Reason, "抢跑");

        coordinator.UpdateTelemetry(participant.ParticipantId,
            Telemetry(0, .35) with { SpeedKph = 30 }, issuedAt.AddMilliseconds(500));
        Assert.AreEqual(1, coordinator.Snapshot().Participants.Single().Penalties.Count);
    }

    [TestMethod]
    public void QualifyingTimeoutLetsAnAlreadyStartedFlyingLapFinish()
    {
        var coordinator = CreateCoordinator();
        var participant = Join(coordinator, 1).Accepted!;
        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Qualifying, null, null, null, 1));
        var issuedAt = coordinator.Snapshot().QualifyingEndsAt!.Value.AddMinutes(-1);
        coordinator.UpdateTelemetry(participant.ParticipantId,
            Telemetry(0, .42) with { CurrentLapSeconds = 31 }, issuedAt.AddSeconds(59));

        coordinator.Tick(issuedAt.AddMinutes(1).AddMilliseconds(1));
        var snapshot = coordinator.Snapshot();
        Assert.AreEqual(RaceSessionPhase.Qualifying, snapshot.Phase);
        Assert.IsTrue(snapshot.QualifyingTimeExpired);
        Assert.IsTrue(snapshot.Participants.Single().QualifyingFinalLapPending);

        CompleteLap(coordinator, participant.ParticipantId, 1, 71.250);
        snapshot = coordinator.Snapshot();
        Assert.AreEqual(RaceSessionPhase.Grid, snapshot.Phase);
        Assert.AreEqual(71.250, snapshot.Participants.Single().BestLapSeconds!.Value, .0001);
    }

    [TestMethod]
    public void PracticeTimeoutLetsAnAlreadyStartedLapFinishBeforeManualNextSessionStart()
    {
        var coordinator = CreateCoordinator();
        var participant = Join(coordinator, 1).Accepted!;
        var issuedAt = DateTimeOffset.Parse("2026-08-12T14:00:00Z");
        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Practice, null, null, null, null,
            PracticeSessionCount: 2,
            PracticeSessionMinutes: [1, 1]), issuedAt);
        coordinator.UpdateTelemetry(participant.ParticipantId,
            Telemetry(0, .42) with { CurrentLapSeconds = 31 }, issuedAt.AddSeconds(59));

        coordinator.Tick(issuedAt.AddMinutes(1).AddMilliseconds(1));
        var snapshot = coordinator.Snapshot();
        Assert.AreEqual(1, snapshot.PracticeSessionNumber);
        Assert.IsTrue(snapshot.PracticeTimeExpired);
        Assert.IsTrue(snapshot.Participants.Single().PracticeFinalLapPending);

        CompleteLap(coordinator, participant.ParticipantId, 1, 71.250);
        snapshot = coordinator.Snapshot();
        Assert.AreEqual(RaceSessionPhase.Practice, snapshot.Phase);
        Assert.AreEqual(1, snapshot.PracticeSessionNumber);
        Assert.IsTrue(snapshot.PracticeTimeExpired);
        Assert.IsNull(snapshot.PracticeEndsAt);
        Assert.AreEqual(71.250,
            snapshot.Participants.Single().PracticeSessionBestLapSeconds![0]!.Value, .0001);

        Assert.IsTrue(coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Practice, null, null, null, null,
            PracticeSessionCount: 2,
            PracticeSessionMinutes: [1, 1]), issuedAt.AddMinutes(2)).IsAccepted);
        snapshot = coordinator.Snapshot();
        Assert.AreEqual(2, snapshot.PracticeSessionNumber);
        Assert.IsFalse(snapshot.PracticeTimeExpired);
        Assert.IsNotNull(snapshot.PracticeEndsAt);
    }

    [TestMethod]
    public void RecordedPitBranchDoesNotTriggerYellowOrTrackLimitWarning()
    {
        var coordinator = CreateCoordinator();
        var participant = Join(coordinator, 1).Accepted!;
        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Qualifying, null, null, null, 10));
        var started = DateTimeOffset.Parse("2026-08-10T10:00:00Z");
        var pitBranch = Telemetry(0, .8) with
        {
            LateralOffsetMeters = 80,
            SpeedKph = 4,
            TrackLengthMeters = 2_000,
            IsOnPitRoute = true
        };

        coordinator.UpdateTelemetry(participant.ParticipantId, pitBranch, started);
        coordinator.UpdateTelemetry(participant.ParticipantId,
            pitBranch with { ClientMonotonicMilliseconds = 14_000 }, started.AddSeconds(4));

        var snapshot = coordinator.Snapshot(started.AddSeconds(4));
        Assert.AreEqual(RaceControlFlag.Green, snapshot.Flag);
        Assert.AreEqual(0, snapshot.Participants.Single().TrackLimitWarnings);
        Assert.HasCount(0, snapshot.Participants.Single().Penalties);
    }

    [TestMethod]
    public void PitApproachBeforeEntryLineDoesNotTriggerYellowOrTrackLimitWarning()
    {
        var coordinator = CreateCoordinator();
        var participant = Join(coordinator, 1).Accepted!;
        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Qualifying, null, null, null, 10));
        var started = DateTimeOffset.Parse("2026-08-10T10:00:00Z");
        var pitApproach = Telemetry(0, .8) with
        {
            LateralOffsetMeters = 80,
            SpeedKph = 4,
            TrackLengthMeters = 2_000,
            IsApproachingPit = true,
            IsOnPitRoute = false
        };

        coordinator.UpdateTelemetry(participant.ParticipantId, pitApproach, started);
        coordinator.UpdateTelemetry(participant.ParticipantId,
            pitApproach with { ClientMonotonicMilliseconds = 14_000 }, started.AddSeconds(4));

        var snapshot = coordinator.Snapshot(started.AddSeconds(4));
        Assert.AreEqual(RaceControlFlag.Green, snapshot.Flag);
        Assert.AreEqual(0, snapshot.Participants.Single().TrackLimitWarnings);
        Assert.HasCount(0, snapshot.Participants.Single().Penalties);
    }

    [TestMethod]
    public void LapEventsAreIgnoredOutsideQualifyingAndRace()
    {
        var coordinator = CreateCoordinator();
        var participant = Join(coordinator, 1).Accepted!;
        var lobbyLap = coordinator.CompleteLap(participant.ParticipantId, new RaceLapCompleted(
            Guid.NewGuid(), 1, 61, [20, 20, 21], true, null, 50_000));
        Assert.IsFalse(lobbyLap.IsAccepted);
        Assert.AreEqual(0, coordinator.Snapshot().Participants.Single().CompletedLaps);

        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Countdown, null, 5, 0, null));
        var countdownLap = coordinator.CompleteLap(participant.ParticipantId, new RaceLapCompleted(
            Guid.NewGuid(), 2, 60, [20, 20, 20], true, null, 50_000));
        Assert.IsFalse(countdownLap.IsAccepted);
        Assert.AreEqual(0, coordinator.Snapshot().Participants.Single().CompletedLaps);
    }

    [TestMethod]
    public void RacePublishesTotalTimeAndSignedDeltasAndRanksFinishedDriversByAdjustedTime()
    {
        var coordinator = CreateCoordinator();
        var first = Join(coordinator, 1).Accepted!;
        var second = Join(coordinator, 2).Accepted!;
        var started = DateTimeOffset.Parse("2026-08-09T10:00:00Z");
        Assert.IsTrue(coordinator.ApplySessionCommand(
            new RaceAdminSessionCommand(RaceSessionPhase.Race, "总时间测试", 2, null, null),
            started).IsAccepted);

        CompleteLap(coordinator, first.ParticipantId, 1, 60, started.AddSeconds(60));
        CompleteLap(coordinator, second.ParticipantId, 1, 61, started.AddSeconds(61));
        var live = coordinator.Snapshot();
        Assert.AreEqual(0, live.Participants[0].GapToLeaderSeconds.GetValueOrDefault(), 0.0001);
        Assert.AreEqual(1, live.Participants[1].GapToLeaderSeconds.GetValueOrDefault(), 0.0001);

        CompleteLap(coordinator, first.ParticipantId, 2, 60, started.AddSeconds(120));
        CompleteLap(coordinator, second.ParticipantId, 2, 60, started.AddSeconds(121));
        Assert.AreEqual(RaceSessionPhase.Finished, coordinator.Snapshot().Phase);
        Assert.IsTrue(coordinator.ApplyPenalty(new RaceAdminPenaltyCommand(
            first.ParticipantId,
            RacePenaltyKind.Time,
            5,
            null,
            "赛后加罚")).IsAccepted);

        var classified = coordinator.Snapshot();
        Assert.AreEqual(second.ParticipantId, classified.Participants[0].Id,
            "相同完赛圈数时，应按含罚时的正赛总时间排名。");
        Assert.AreEqual(121, classified.Participants[0].AdjustedRaceTotalSeconds.GetValueOrDefault(), 0.0001);
        Assert.AreEqual(125, classified.Participants[1].AdjustedRaceTotalSeconds.GetValueOrDefault(), 0.0001);
        Assert.AreEqual(0, classified.Participants[0].GapToLeaderSeconds.GetValueOrDefault(), 0.0001);
        Assert.AreEqual(4, classified.Participants[1].GapToLeaderSeconds.GetValueOrDefault(), 0.0001);
        Assert.AreEqual(121, classified.RaceElapsedSeconds.GetValueOrDefault(), 0.0001);
    }

    [TestMethod]
    public void RaceDeltaUpdatesAtCommonTrackProgressWithoutWaitingForALapEvent()
    {
        var coordinator = CreateCoordinator();
        var leader = Join(coordinator, 1).Accepted!;
        var trailing = Join(coordinator, 2).Accepted!;
        var started = DateTimeOffset.Parse("2026-08-09T10:30:00Z");
        Assert.IsTrue(coordinator.ApplySessionCommand(
            new RaceAdminSessionCommand(RaceSessionPhase.Race, "实时秒差测试", 5, null, null),
            started).IsAccepted);

        coordinator.UpdateTelemetry(leader.ParticipantId, Telemetry(0, .10), started.AddSeconds(10));
        coordinator.UpdateTelemetry(trailing.ParticipantId, Telemetry(0, .10), started.AddSeconds(11));
        var firstGap = coordinator.Snapshot(started.AddSeconds(11)).Participants[1];
        Assert.AreEqual(1, firstGap.GapToLeaderSeconds.GetValueOrDefault(), 0.0001);

        coordinator.UpdateTelemetry(leader.ParticipantId, Telemetry(0, .30), started.AddSeconds(20));
        coordinator.UpdateTelemetry(trailing.ParticipantId, Telemetry(0, .30), started.AddSeconds(23));
        var refreshedGap = coordinator.Snapshot(started.AddSeconds(23)).Participants[1];
        Assert.AreEqual(3, refreshedGap.GapToLeaderSeconds.GetValueOrDefault(), 0.0001,
            "秒差应随共同赛道进度刷新，不能等到下一次冲线。 ");
        Assert.AreEqual(3, refreshedGap.IntervalSeconds.GetValueOrDefault(), 0.0001);
        Assert.AreEqual(0, refreshedGap.CompletedLaps);
    }

    [TestMethod]
    public void LeaderCrossingTheLineDoesNotImmediatelyTurnASecondsGapIntoOneLap()
    {
        var coordinator = CreateCoordinator();
        var leader = Join(coordinator, 1).Accepted!;
        var trailing = Join(coordinator, 2).Accepted!;
        var started = DateTimeOffset.Parse("2026-08-09T10:45:00Z");
        Assert.IsTrue(coordinator.ApplySessionCommand(
            new RaceAdminSessionCommand(RaceSessionPhase.Race, "冲线秒差测试", 5, null, null),
            started).IsAccepted);

        coordinator.UpdateTelemetry(leader.ParticipantId, Telemetry(0, .90), started.AddSeconds(50));
        coordinator.UpdateTelemetry(trailing.ParticipantId, Telemetry(0, .90), started.AddSeconds(55));
        CompleteLap(coordinator, leader.ParticipantId, 1, 60, started.AddSeconds(60));

        var snapshot = coordinator.Snapshot(started.AddSeconds(60));
        var trailingSnapshot = snapshot.Participants.Single(item => item.Id == trailing.ParticipantId);
        Assert.AreEqual(0, trailingSnapshot.CompletedLaps);
        Assert.AreEqual(5, trailingSnapshot.GapToLeaderSeconds.GetValueOrDefault(), .001,
            "前车先冲线不等于已经完整套后车一圈，服务端应继续保留共同进度上的秒差。");
    }

    [TestMethod]
    public void RedFlagFreezesServerAuthoritativeRaceElapsedTime()
    {
        var coordinator = CreateCoordinator();
        var participant = Join(coordinator, 1).Accepted!;
        var started = DateTimeOffset.Parse("2026-08-09T11:00:00Z");
        coordinator.ApplySessionCommand(
            new RaceAdminSessionCommand(RaceSessionPhase.Race, null, 5, null, null),
            started);
        coordinator.ApplyFlagCommand(
            new RaceAdminFlagCommand(RaceControlFlag.Red, "事故处理"),
            started.AddSeconds(10));
        var redFlagSnapshot = coordinator.Snapshot(started.AddSeconds(30));
        Assert.AreEqual(RaceSessionPhase.Race, redFlagSnapshot.SuspendedFromPhase);
        Assert.AreEqual(10, redFlagSnapshot.RaceElapsedSeconds.GetValueOrDefault(), 0.05);
        coordinator.ApplyFlagCommand(
            new RaceAdminFlagCommand(RaceControlFlag.Green, null),
            started.AddSeconds(40));
        coordinator.UpdateTelemetry(
            participant.ParticipantId,
            Telemetry(0, .2),
            started.AddSeconds(50));
        Assert.AreEqual(20, coordinator.Snapshot(started.AddSeconds(50)).RaceElapsedSeconds.GetValueOrDefault(), 0.05);
    }

    [TestMethod]
    public void SuspendedQualifyingDoesNotPublishARaceClock()
    {
        var coordinator = CreateCoordinator();
        var started = DateTimeOffset.Parse("2026-08-09T11:30:00Z");
        coordinator.ApplySessionCommand(
            new RaceAdminSessionCommand(RaceSessionPhase.Qualifying, null, null, null, 10),
            started);
        coordinator.ApplyFlagCommand(
            new RaceAdminFlagCommand(RaceControlFlag.Red, "排位赛红旗"),
            started.AddSeconds(10));

        var snapshot = coordinator.Snapshot(started.AddSeconds(30));
        Assert.AreEqual(RaceSessionPhase.Qualifying, snapshot.SuspendedFromPhase);
        Assert.IsNull(snapshot.RaceElapsedSeconds);
        Assert.IsTrue(snapshot.Participants.All(participant => participant.RaceTotalSeconds is null));
    }

    [TestMethod]
    public void RaceTrackLimitsWarnThreeTimesThenAddTimeAndSevereCutPenalizesImmediately()
    {
        var coordinator = CreateCoordinator();
        SetTrackLimitMode(coordinator, TrackLimitEnforcementMode.Automatic);
        var participant = Join(coordinator, 1).Accepted!;
        var started = DateTimeOffset.Parse("2026-08-09T12:00:00Z");
        coordinator.ApplySessionCommand(
            new RaceAdminSessionCommand(RaceSessionPhase.Race, null, 5, null, null),
            started);

        for (var incident = 0; incident < 4; incident++)
        {
            var incidentAt = started.AddSeconds(incident * 2 + 1);
            var startProgress = .10 + incident * .05;
            var monotonic = 10_000L + incident * 2_000;
            var outside = Telemetry(0, startProgress) with
            {
                ClientMonotonicMilliseconds = monotonic,
                LateralOffsetMeters = 20,
                SpeedKph = 36,
                TrackLengthMeters = 1_000
            };
            coordinator.UpdateTelemetry(participant.ParticipantId, outside, incidentAt);
            coordinator.UpdateTelemetry(participant.ParticipantId,
                outside with
                {
                    ClientMonotonicMilliseconds = monotonic + 300,
                    TrackProgress = startProgress + .015
                }, incidentAt.AddMilliseconds(300));
            coordinator.UpdateTelemetry(participant.ParticipantId,
                outside with
                {
                    ClientMonotonicMilliseconds = monotonic + 500,
                    TrackProgress = startProgress + .020,
                    LateralOffsetMeters = 0
                }, incidentAt.AddMilliseconds(500));
            coordinator.UpdateTelemetry(participant.ParticipantId,
                outside with
                {
                    ClientMonotonicMilliseconds = monotonic + 950,
                    TrackProgress = startProgress + .025,
                    LateralOffsetMeters = 0
                }, incidentAt.AddMilliseconds(950));
        }

        var afterMinorCuts = coordinator.Snapshot().Participants.Single();
        Assert.AreEqual(0, afterMinorCuts.TrackLimitWarnings,
            "三次警告后的下一次轻微切弯应罚时，并开始新的警告周期。");
        Assert.AreEqual(3, afterMinorCuts.Penalties.Count(item => item.Kind == RacePenaltyKind.Warning));
        Assert.AreEqual(1, afterMinorCuts.Penalties.Count(item => item.Kind == RacePenaltyKind.Time));
        Assert.AreEqual(5, afterMinorCuts.TimePenaltySeconds, 0.0001);

        var severe = Telemetry(0, .60) with
        {
            ClientMonotonicMilliseconds = 20_000,
            LateralOffsetMeters = 30,
            SpeedKph = 36,
            TrackLengthMeters = 1_000
        };
        coordinator.UpdateTelemetry(participant.ParticipantId, severe, started.AddSeconds(10));
        coordinator.UpdateTelemetry(participant.ParticipantId,
            severe with { ClientMonotonicMilliseconds = 20_300, TrackProgress = .65 },
            started.AddSeconds(10.3));
        coordinator.UpdateTelemetry(participant.ParticipantId,
            severe with { ClientMonotonicMilliseconds = 20_500, TrackProgress = .67, LateralOffsetMeters = 0 },
            started.AddSeconds(10.5));
        coordinator.UpdateTelemetry(participant.ParticipantId,
            severe with { ClientMonotonicMilliseconds = 20_950, TrackProgress = .68, LateralOffsetMeters = 0 },
            started.AddSeconds(10.95));
        var afterSevereCut = coordinator.Snapshot().Participants.Single();
        Assert.AreEqual(2, afterSevereCut.Penalties.Count(item => item.Kind == RacePenaltyKind.Time));
        Assert.AreEqual(10, afterSevereCut.TimePenaltySeconds, 0.0001);
        StringAssert.Contains(afterSevereCut.Penalties.Last().Reason, "严重切弯");
    }

    [TestMethod]
    public void ProgressDiscontinuityDetectsLargeShortcutAndPitSpeedingOnlyOncePerVisit()
    {
        var coordinator = CreateCoordinator();
        SetTrackLimitMode(coordinator, TrackLimitEnforcementMode.Automatic);
        var participant = Join(coordinator, 1).Accepted!;
        var started = DateTimeOffset.Parse("2026-08-09T12:30:00Z");
        coordinator.ApplySessionCommand(
            new RaceAdminSessionCommand(RaceSessionPhase.Race, null, 5, null, null), started);

        coordinator.UpdateTelemetry(participant.ParticipantId,
            Telemetry(0, .10) with
            {
                ClientMonotonicMilliseconds = 10_000,
                TrackLengthMeters = 1_000
            }, started.AddSeconds(1));
        coordinator.UpdateTelemetry(participant.ParticipantId,
            Telemetry(0, .52) with
            {
                ClientMonotonicMilliseconds = 10_100,
                TrackLengthMeters = 1_000
            }, started.AddSeconds(1.1));

        var afterShortcut = coordinator.Snapshot().Participants.Single();
        Assert.AreEqual(1, afterShortcut.Penalties.Count(item =>
            item.Kind == RacePenaltyKind.Time && item.Reason.Contains("跨越约", StringComparison.Ordinal)));

        var speeding = Telemetry(0, .60) with
        {
            ClientMonotonicMilliseconds = 11_000,
            IsInPitLane = true,
            SpeedKph = 92,
            PitSpeedLimitKph = 80,
            TrackLengthMeters = 1_000
        };
        coordinator.UpdateTelemetry(participant.ParticipantId, speeding, started.AddSeconds(2));
        coordinator.UpdateTelemetry(participant.ParticipantId,
            speeding with { ClientMonotonicMilliseconds = 11_500 }, started.AddSeconds(2.5));
        coordinator.UpdateTelemetry(participant.ParticipantId,
            speeding with { ClientMonotonicMilliseconds = 12_000 }, started.AddSeconds(3));

        var afterSpeeding = coordinator.Snapshot().Participants.Single();
        Assert.AreEqual(1, afterSpeeding.Penalties.Count(item => item.Reason.Contains("维修区超速", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void TrackLimitModesRequireAdvantageAndNeverTreatPitApproachAsCutting()
    {
        var coordinator = CreateCoordinator();
        var participant = Join(coordinator, 1).Accepted!;
        var started = DateTimeOffset.Parse("2026-08-09T12:40:00Z");
        coordinator.ApplySessionCommand(
            new RaceAdminSessionCommand(RaceSessionPhase.Qualifying, null, null, null, 10), started);

        var outside = Telemetry(0, .20) with
        {
            ClientMonotonicMilliseconds = 10_000,
            LateralOffsetMeters = 24,
            SpeedKph = 36,
            TrackLengthMeters = 1_000
        };
        coordinator.UpdateTelemetry(participant.ParticipantId, outside, started.AddSeconds(1));
        coordinator.UpdateTelemetry(participant.ParticipantId,
            outside with { ClientMonotonicMilliseconds = 11_000, TrackProgress = .21 },
            started.AddSeconds(2));
        coordinator.UpdateTelemetry(participant.ParticipantId,
            outside with { ClientMonotonicMilliseconds = 11_200, TrackProgress = .211, LateralOffsetMeters = 0 },
            started.AddSeconds(2.2));
        coordinator.UpdateTelemetry(participant.ParticipantId,
            outside with { ClientMonotonicMilliseconds = 11_700, TrackProgress = .212, LateralOffsetMeters = 0 },
            started.AddSeconds(2.7));
        Assert.HasCount(0, coordinator.Snapshot().Participants.Single().Penalties,
            "偏离后没有获得距离优势时只能使本圈失去最快圈资格，不能自动处罚。");

        var approach = outside with
        {
            ClientMonotonicMilliseconds = 12_000,
            TrackProgress = .30,
            LateralOffsetMeters = 40,
            IsApproachingPit = true
        };
        coordinator.UpdateTelemetry(participant.ParticipantId, approach, started.AddSeconds(3));
        coordinator.UpdateTelemetry(participant.ParticipantId,
            approach with { ClientMonotonicMilliseconds = 12_500, TrackProgress = .50 },
            started.AddSeconds(3.5));
        Assert.HasCount(0, coordinator.Snapshot().Participants.Single().Penalties,
            "合法驶入维修区的入口与通道不能被判为切弯。");

        var gained = outside with
        {
            ClientMonotonicMilliseconds = 20_000,
            TrackProgress = .60,
            LateralOffsetMeters = 20
        };
        coordinator.UpdateTelemetry(participant.ParticipantId, gained, started.AddSeconds(5));
        coordinator.UpdateTelemetry(participant.ParticipantId,
            gained with { ClientMonotonicMilliseconds = 20_300, TrackProgress = .62 },
            started.AddSeconds(5.3));
        coordinator.UpdateTelemetry(participant.ParticipantId,
            gained with { ClientMonotonicMilliseconds = 20_500, TrackProgress = .625, LateralOffsetMeters = 0 },
            started.AddSeconds(5.5));
        coordinator.UpdateTelemetry(participant.ParticipantId,
            gained with { ClientMonotonicMilliseconds = 20_950, TrackProgress = .63, LateralOffsetMeters = 0 },
            started.AddSeconds(5.95));
        var warningOnly = coordinator.Snapshot().Participants.Single();
        Assert.AreEqual(1, warningOnly.TrackLimitWarnings);
        Assert.HasCount(0, warningOnly.Penalties,
            "仅调查模式不能先创建一条实际上已经下发的警告处罚。");
        var pendingInvestigation = coordinator.Snapshot().Investigations?.Single();
        Assert.IsNotNull(pendingInvestigation);
        Assert.AreEqual(RaceInvestigationStatus.Pending, pendingInvestigation.Status);
        Assert.AreEqual(1, pendingInvestigation.LapNumber);
        Assert.AreEqual(0, warningOnly.PendingTimePenaltySeconds, 0.0001);

        SetTrackLimitMode(coordinator, TrackLimitEnforcementMode.Disabled);
        var disabled = gained with
        {
            ClientMonotonicMilliseconds = 30_000,
            TrackProgress = .70,
            LateralOffsetMeters = 25
        };
        coordinator.UpdateTelemetry(participant.ParticipantId, disabled, started.AddSeconds(7));
        coordinator.UpdateTelemetry(participant.ParticipantId,
            disabled with { ClientMonotonicMilliseconds = 30_300, TrackProgress = .75 },
            started.AddSeconds(7.3));
        coordinator.UpdateTelemetry(participant.ParticipantId,
            disabled with { ClientMonotonicMilliseconds = 30_500, TrackProgress = .77, LateralOffsetMeters = 0 },
            started.AddSeconds(7.5));
        coordinator.UpdateTelemetry(participant.ParticipantId,
            disabled with { ClientMonotonicMilliseconds = 30_950, TrackProgress = .78, LateralOffsetMeters = 0 },
            started.AddSeconds(7.95));
        Assert.HasCount(0, coordinator.Snapshot().Participants.Single().Penalties);
        Assert.HasCount(1, coordinator.Snapshot().Investigations!);
    }

    [TestMethod]
    public void CutRaceLapCountsButCannotBecomeFastestAndPendingPenaltyIsServedBeforeTires()
    {
        var coordinator = CreateCoordinator();
        var participant = Join(coordinator, 1).Accepted!;
        var started = DateTimeOffset.Parse("2026-08-09T12:42:00Z");
        coordinator.ApplySessionCommand(
            new RaceAdminSessionCommand(RaceSessionPhase.Race, null, 2, null, null), started);
        Assert.IsTrue(coordinator.ApplyPenalty(new RaceAdminPenaltyCommand(
            participant.ParticipantId, RacePenaltyKind.Time, 2, null, "测试停车罚时")).IsAccepted);

        var cutLap = coordinator.CompleteLap(participant.ParticipantId, new RaceLapCompleted(
            Guid.NewGuid(), 1, 50, [16, 17, 17], true, "estate-track-deviation", 50_000, false),
            started.AddSeconds(50));
        Assert.IsTrue(cutLap.IsAccepted, cutLap.Error);
        var afterLap = coordinator.Snapshot(started.AddSeconds(50)).Participants.Single();
        Assert.AreEqual(1, afterLap.CompletedLaps);
        Assert.IsNull(afterLap.BestLapSeconds);
        Assert.IsNull(coordinator.Snapshot(started.AddSeconds(50)).FastestLapSeconds);
        Assert.AreEqual(2, afterLap.PendingTimePenaltySeconds, 0.0001);
        Assert.AreEqual(afterLap.RaceTotalSeconds, afterLap.AdjustedRaceTotalSeconds,
            "未完赛前只能显示待执行罚时，不能提前写入正赛总时间。");

        var stopped = Telemetry(1, .55) with
        {
            IsInPitLane = true,
            IsInServiceZone = true,
            SpeedKph = 0,
            PitServiceElapsedSeconds = 1.5,
            PitServiceRequirementMet = false
        };
        coordinator.UpdateTelemetry(participant.ParticipantId, stopped, started.AddSeconds(60));
        coordinator.UpdateTelemetry(participant.ParticipantId, stopped, started.AddSeconds(61));
        var serving = coordinator.Snapshot(started.AddSeconds(61)).Participants.Single();
        Assert.IsTrue(serving.IsServingTimePenalty);
        Assert.AreEqual(0, serving.PitServiceElapsedSeconds, 0.0001,
            "停车罚时完成前不得同时累计换胎计时。");
        coordinator.UpdateTelemetry(participant.ParticipantId, stopped, started.AddSeconds(62.1));
        var served = coordinator.Snapshot(started.AddSeconds(62.1)).Participants.Single();
        Assert.IsFalse(served.IsServingTimePenalty);
        Assert.AreEqual(0, served.PendingTimePenaltySeconds, 0.0001);
        Assert.IsTrue(served.PenaltyServiceCompleted);
        Assert.IsTrue(coordinator.Events(50).Any(item => item.Type == "penaltyServiceCompleted"));
    }

    [TestMethod]
    public void DriveThroughCountsTwoOnTrackCrossingsThenBecomesTwentySecondAdjustment()
    {
        var coordinator = CreateCoordinator();
        var participant = Join(coordinator, 1).Accepted!;
        var started = DateTimeOffset.Parse("2026-08-09T13:00:00Z");
        Assert.IsTrue(coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Race, null, 10, null, null), started).IsAccepted);
        Assert.IsTrue(coordinator.ApplyPenalty(new RaceAdminPenaltyCommand(
            participant.ParticipantId, RacePenaltyKind.DriveThrough, null, null, "测试通过维修区")).IsAccepted);

        var issued = coordinator.Snapshot(started);
        Assert.AreEqual(2, issued.Participants.Single().DriveThroughLapsRemaining);
        CompleteLap(coordinator, participant.ParticipantId, 1, 60, started.AddSeconds(60));
        var firstCrossing = coordinator.Snapshot(started.AddSeconds(60));
        Assert.AreEqual(1, firstCrossing.Participants.Single().DriveThroughLapsRemaining);
        Assert.IsTrue(coordinator.Events(50).Any(item => item.Type == "driveThroughReminder"));

        CompleteLap(coordinator, participant.ParticipantId, 2, 61, started.AddSeconds(121));
        var secondCrossing = coordinator.Snapshot(started.AddSeconds(121));
        Assert.AreEqual(0, secondCrossing.Participants.Single().DriveThroughLapsRemaining);
        Assert.IsTrue(secondCrossing.Participants.Single().HasPendingDriveThrough);

        CompleteLap(coordinator, participant.ParticipantId, 3, 62, started.AddSeconds(183));
        var overdue = coordinator.Snapshot(started.AddSeconds(183)).Participants.Single();
        Assert.IsFalse(overdue.HasPendingDriveThrough);
        Assert.IsTrue(overdue.DriveThroughOverdue);
        Assert.AreEqual(0, overdue.PendingTimePenaltySeconds, 0.0001,
            "逾期替换的 20 秒是完赛加时，不能再次作为进站停车罚时执行。");
        Assert.AreEqual(20, overdue.TimePenaltySeconds, 0.0001);
        Assert.IsTrue(overdue.Penalties.Any(item => item.IsPostRaceAdjustment && !item.IsServed));
        Assert.IsTrue(coordinator.Events(50).Any(item => item.Type == "driveThroughOverdue"));
    }

    [TestMethod]
    public void DriveThroughIsServedOnlyByContinuousPitLaneTransitWithoutStopping()
    {
        var coordinator = CreateCoordinator();
        var participant = Join(coordinator, 1).Accepted!;
        var started = DateTimeOffset.Parse("2026-08-09T13:10:00Z");
        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Race, null, 10, null, null), started);
        coordinator.ApplyPenalty(new RaceAdminPenaltyCommand(
            participant.ParticipantId, RacePenaltyKind.DriveThrough, null, null, "测试通过维修区"));

        var inPit = Telemetry(0, .45) with { IsInPitLane = true, SpeedKph = 55 };
        coordinator.UpdateTelemetry(participant.ParticipantId, inPit, started.AddSeconds(10));
        Assert.IsTrue(coordinator.Snapshot(started.AddSeconds(10)).Participants.Single().IsServingDriveThrough);
        coordinator.UpdateTelemetry(participant.ParticipantId,
            inPit with { TrackProgress = .55, SpeedKph = 48 }, started.AddSeconds(12));
        coordinator.UpdateTelemetry(participant.ParticipantId,
            inPit with { IsInPitLane = false, TrackProgress = .60, SpeedKph = 80 }, started.AddSeconds(14));

        var served = coordinator.Snapshot(started.AddSeconds(14)).Participants.Single();
        Assert.IsFalse(served.HasPendingDriveThrough);
        Assert.IsTrue(served.PenaltyServiceCompleted);
        Assert.IsTrue(coordinator.Events(50).Any(item => item.Type == "driveThroughServed"));
    }

    [TestMethod]
    public void DriveThroughStopInvalidatesVisitAndFinalThreeLapsUseTimeAdjustment()
    {
        var coordinator = CreateCoordinator();
        var participant = Join(coordinator, 1).Accepted!;
        var started = DateTimeOffset.Parse("2026-08-09T13:20:00Z");
        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Race, null, 10, null, null), started);
        coordinator.ApplyPenalty(new RaceAdminPenaltyCommand(
            participant.ParticipantId, RacePenaltyKind.DriveThrough, null, null, "测试停车失败"));
        var stopped = Telemetry(0, .45) with { IsInPitLane = true, SpeedKph = 0 };
        coordinator.UpdateTelemetry(participant.ParticipantId, stopped, started.AddSeconds(5));
        coordinator.UpdateTelemetry(participant.ParticipantId, stopped, started.AddSeconds(6.1));
        coordinator.UpdateTelemetry(participant.ParticipantId,
            stopped with { IsInPitLane = false, TrackProgress = .60, SpeedKph = 60 }, started.AddSeconds(8));
        Assert.IsTrue(coordinator.Snapshot(started.AddSeconds(8)).Participants.Single().HasPendingDriveThrough);
        Assert.IsTrue(coordinator.Events(50).Any(item => item.Type == "driveThroughAttemptFailed"));

        var late = CreateCoordinator();
        var lateParticipant = Join(late, 1).Accepted!;
        late.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Race, null, 3, null, null), started);
        late.ApplyPenalty(new RaceAdminPenaltyCommand(
            lateParticipant.ParticipantId, RacePenaltyKind.DriveThrough, null, null, "最后三圈处罚"));
        var lateSnapshot = late.Snapshot(started).Participants.Single();
        Assert.IsFalse(lateSnapshot.HasPendingDriveThrough);
        Assert.AreEqual(20, lateSnapshot.TimePenaltySeconds, 0.0001);
        Assert.IsTrue(lateSnapshot.Penalties.Single().IsPostRaceAdjustment);
    }

    [TestMethod]
    public void FinishedDriverCannotServeOrReceiveAutomaticPitPenaltiesAndPendingTimeIsAddedToResult()
    {
        var coordinator = CreateCoordinator();
        var participant = Join(coordinator, 1).Accepted!;
        var started = DateTimeOffset.Parse("2026-08-12T10:00:00Z");
        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Race, null, 1, null, null), started);
        Assert.IsTrue(coordinator.ApplyPenalty(new RaceAdminPenaltyCommand(
            participant.ParticipantId, RacePenaltyKind.Time, 5, null, "赛中罚时")).IsAccepted);

        CompleteLap(coordinator, participant.ParticipantId, 1, 60, started.AddSeconds(60));
        var finished = coordinator.Snapshot(started.AddSeconds(60)).Participants.Single();
        Assert.AreEqual(RaceParticipantStatus.Finished, finished.Status);
        Assert.AreEqual(0, finished.PendingTimePenaltySeconds, 0.0001);
        Assert.AreEqual(5, finished.TimePenaltySeconds, 0.0001);
        Assert.AreEqual(
            finished.RaceTotalSeconds.GetValueOrDefault() + 5,
            finished.AdjustedRaceTotalSeconds.GetValueOrDefault(),
            0.0001);
        Assert.IsTrue(finished.Penalties.Single().IsPostRaceAdjustment);

        var postRacePit = Telemetry(1, .10) with
        {
            IsInPitLane = true,
            IsInServiceZone = true,
            IsPausedOrRewinding = true,
            SpeedKph = 120,
            PitSpeedLimitKph = 80
        };
        coordinator.UpdateTelemetry(participant.ParticipantId, postRacePit, started.AddSeconds(70));
        coordinator.UpdateTelemetry(participant.ParticipantId,
            postRacePit with { IsPausedOrRewinding = false }, started.AddSeconds(71));
        var afterPit = coordinator.Snapshot(started.AddSeconds(71));
        Assert.AreEqual(RaceSessionPhase.Finished, afterPit.Phase);
        Assert.AreEqual(1, afterPit.Penalties?.Count);
        Assert.IsFalse(afterPit.Participants.Single().HasPendingDriveThrough);
        Assert.IsFalse(afterPit.Participants.Single().IsServingTimePenalty);
        Assert.IsFalse(afterPit.Penalties!.Any(item => item.Reason.Contains("维修区超速", StringComparison.Ordinal)));

        Assert.IsTrue(coordinator.ApplyPenalty(new RaceAdminPenaltyCommand(
            participant.ParticipantId, RacePenaltyKind.DriveThrough, null, null, "赛后人工判罚")).IsAccepted);
        var postRacePenalty = coordinator.Snapshot(started.AddSeconds(72));
        Assert.AreEqual(25, postRacePenalty.Participants.Single().TimePenaltySeconds, 0.0001);
        Assert.IsFalse(postRacePenalty.Participants.Single().HasPendingDriveThrough);
        Assert.IsTrue(postRacePenalty.Penalties!.Last().IsPostRaceAdjustment);
    }

    [TestMethod]
    public void InvestigationCanBeResolvedThenItsPenaltyEditedOrCancelled()
    {
        var coordinator = CreateCoordinator();
        var participant = Join(coordinator, 1).Accepted!;
        var started = DateTimeOffset.Parse("2026-08-12T11:00:00Z");
        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Qualifying, null, null, null, 10), started);
        var outside = Telemetry(0, .60) with
        {
            ClientMonotonicMilliseconds = 20_000,
            LateralOffsetMeters = 20,
            SpeedKph = 36,
            TrackLengthMeters = 1_000
        };
        coordinator.UpdateTelemetry(participant.ParticipantId, outside, started.AddSeconds(1));
        coordinator.UpdateTelemetry(participant.ParticipantId,
            outside with { ClientMonotonicMilliseconds = 20_300, TrackProgress = .62 }, started.AddSeconds(1.3));
        coordinator.UpdateTelemetry(participant.ParticipantId,
            outside with { ClientMonotonicMilliseconds = 20_500, TrackProgress = .625, LateralOffsetMeters = 0 }, started.AddSeconds(1.5));
        coordinator.UpdateTelemetry(participant.ParticipantId,
            outside with { ClientMonotonicMilliseconds = 20_950, TrackProgress = .63, LateralOffsetMeters = 0 }, started.AddSeconds(1.95));

        var investigation = coordinator.Snapshot().Investigations!.Single();
        Assert.AreEqual(RaceBannerKind.Information, coordinator.Snapshot().Banner?.Kind);
        Assert.IsTrue(coordinator.Snapshot().Banner?.IsInvestigation);
        Assert.IsTrue(coordinator.ResolveInvestigation(new RaceAdminInvestigationCommand(
            investigation.Id, true, RacePenaltyKind.Time, 4, "总控确认获利")).IsAccepted);
        var resolved = coordinator.Snapshot();
        Assert.AreEqual(RaceInvestigationStatus.Penalized, resolved.Investigations!.Single().Status);
        var penalty = resolved.Penalties!.Single();
        Assert.AreEqual(investigation.Id, penalty.InvestigationId);
        Assert.AreEqual(4, penalty.ValueSeconds);

        Assert.IsTrue(coordinator.UpdatePenalty(new RaceAdminPenaltyUpdateCommand(
            penalty.Id, 2, "复核后改为 2 秒", false)).IsAccepted);
        Assert.AreEqual(2, coordinator.Snapshot().Penalties!.Single().ValueSeconds);
        Assert.IsTrue(coordinator.UpdatePenalty(new RaceAdminPenaltyUpdateCommand(
            penalty.Id, null, null, true)).IsAccepted);
        var cancelled = coordinator.Snapshot();
        Assert.IsTrue(cancelled.Penalties!.Single().IsRevoked);
        Assert.HasCount(0, cancelled.Participants.Single().Penalties);
    }

    [TestMethod]
    public void SessionFastestLapCarriesTheSectorsFromThatExactLap()
    {
        var coordinator = CreateCoordinator();
        var first = Join(coordinator, 1).Accepted!;
        var second = Join(coordinator, 2).Accepted!;
        coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Qualifying, null, null, null, 10));
        coordinator.CompleteLap(first.ParticipantId, new RaceLapCompleted(
            Guid.NewGuid(), 1, 60, [20, 20, 20], true, null, 60_000));
        coordinator.CompleteLap(second.ParticipantId, new RaceLapCompleted(
            Guid.NewGuid(), 1, 59, [18, 21, 20], true, null, 59_000));

        CollectionAssert.AreEqual(
            new double?[] { 18, 21, 20 },
            coordinator.Snapshot().FastestLapSectorSeconds?.ToArray());
        CollectionAssert.AreEqual(
            new double?[] { 18, 20, 20 },
            coordinator.Snapshot().FastestSectorSeconds.ToArray(),
            "全场最快分段与全场最快圈的分段来源必须分别保存。" );
    }

    [TestMethod]
    public void InvalidTelemetryDoesNotEraseAutomaticYellowCandidate()
    {
        var coordinator = CreateCoordinator();
        var participant = Join(coordinator, 1).Accepted!;
        var started = DateTimeOffset.Parse("2026-08-09T12:45:00Z");
        coordinator.ApplySessionCommand(
            new RaceAdminSessionCommand(RaceSessionPhase.Qualifying, null, null, null, 10), started);
        var slow = Telemetry(0, .30) with { SpeedKph = 2 };
        coordinator.UpdateTelemetry(participant.ParticipantId, slow, started.AddSeconds(1));
        coordinator.UpdateTelemetry(participant.ParticipantId,
            slow with { IsTelemetryValid = false, IsPausedOrRewinding = true }, started.AddSeconds(2));
        coordinator.UpdateTelemetry(participant.ParticipantId, slow, started.AddSeconds(4.2));

        var snapshot = coordinator.Snapshot(started.AddSeconds(4.2));
        Assert.AreEqual(RaceControlFlag.Yellow, snapshot.Flag);
        Assert.IsTrue(snapshot.YellowZones?.Any(item => item.IsAutomatic) == true);
    }

    [TestMethod]
    public void ConfiguredTeamsAreRequiredAndEnforcePerTeamCapacity()
    {
        var coordinator = CreateCoordinator();
        var settings = coordinator.RoomSettings();
        var applied = coordinator.ApplyRoomSettings(new RaceAdminRoomSettingsCommand(
            settings.SessionName, settings.TotalRaceLaps, settings.SectorCount,
            settings.AutomaticYellowEnabled, settings.SlowSpeedKph, settings.SlowDurationSeconds,
            settings.SevereLateralOffsetMeters, settings.RecoveryDurationSeconds,
            true, settings.TrackName, settings.TrackId, settings.TrackRevision, settings.TrackPackageHash,
            2, 1,
            [
                new RaceTeamDefinition("red", "红队", "#FF4057"),
                new RaceTeamDefinition("blue", "蓝队", "#5A8CFF")
            ]));
        Assert.IsTrue(applied.IsAccepted, applied.Error);

        var missing = coordinator.TryJoin(Login(1) with { TeamId = null, TeamName = null });
        Assert.IsFalse(missing.IsAccepted);
        Assert.AreEqual("teamRequired", missing.Rejected?.Code);
        var first = coordinator.TryJoin(Login(1) with { TeamId = "red", TeamName = "红队" });
        Assert.IsTrue(first.IsAccepted);
        var full = coordinator.TryJoin(Login(2) with { TeamId = "red", TeamName = "红队" });
        Assert.IsFalse(full.IsAccepted);
        Assert.AreEqual("teamFull", full.Rejected?.Code);
        Assert.IsTrue(coordinator.TryJoin(Login(2) with { TeamId = "blue", TeamName = "蓝队" }).IsAccepted);
    }

    [TestMethod]
    public void LazyForza142WithoutConfiguredTeamSelectionIsAssignedToAnAvailableTeam()
    {
        var coordinator = CreateCoordinator();
        var settings = coordinator.RoomSettings();
        Assert.IsTrue(coordinator.ApplyRoomSettings(new RaceAdminRoomSettingsCommand(
            settings.SessionName, settings.TotalRaceLaps, settings.SectorCount,
            settings.AutomaticYellowEnabled, settings.SlowSpeedKph, settings.SlowDurationSeconds,
            settings.SevereLateralOffsetMeters, settings.RecoveryDurationSeconds,
            true, settings.TrackName, settings.TrackId, settings.TrackRevision, settings.TrackPackageHash,
            2, 1,
            [
                new RaceTeamDefinition("red", "红队", "#FF4057"),
                new RaceTeamDefinition("blue", "蓝队", "#5A8CFF")
            ])).IsAccepted);

        var first = coordinator.TryJoin(Login(1) with
        {
            ClientVersion = "1.4.2",
            TeamId = null,
            TeamName = null
        });
        var second = coordinator.TryJoin(Login(2) with
        {
            ClientVersion = "v1.4.2",
            TeamId = null,
            TeamName = "旧客户端自由填写的名称"
        });
        Assert.IsTrue(first.IsAccepted);
        Assert.IsTrue(second.IsAccepted);
        CollectionAssert.AreEquivalent(
            new[] { "red", "blue" },
            coordinator.Snapshot().Participants.Select(item => item.TeamId!).ToArray());

        var current = coordinator.TryJoin(Login(3) with
        {
            ClientVersion = "1.4.3",
            TeamId = null,
            TeamName = null
        });
        Assert.IsFalse(current.IsAccepted);
        Assert.AreEqual("teamRequired", current.Rejected?.Code);
    }

    [TestMethod]
    public void LazyForza142WirePayloadsUseBackwardCompatibleDefaults()
    {
        var loginEnvelope = RaceProtocolJson.DeserializeEnvelope(Encoding.UTF8.GetBytes(
            """{"protocolVersion":2,"type":"login","sequence":1,"payload":{"password":"p","displayName":"旧客户端","themeColor":"#42D7E8","teamName":null,"clientVersion":"1.4.2","resumeToken":null,"trackId":null,"trackRevision":null,"trackPackageHash":null,"sectorCount":3}}"""));
        var login = RaceProtocolJson.DeserializePayload<RaceLoginRequest>(loginEnvelope);
        Assert.IsNull(login.TeamId);

        var telemetryEnvelope = RaceProtocolJson.DeserializeEnvelope(Encoding.UTF8.GetBytes(
            """{"protocolVersion":2,"type":"telemetry","sequence":2,"payload":{"clientMonotonicMilliseconds":1000,"trackProgress":0.5,"lateralOffsetMeters":0,"mapX":0.5,"mapY":0.5,"speedKph":120,"completedLaps":1,"currentSector":1,"currentLapSeconds":30,"isInPitLane":false,"isInServiceZone":false,"isTelemetryValid":true,"isPausedOrRewinding":false,"gripCondition":"slightlyReduced","pitServiceElapsedSeconds":0,"pitServiceRequirementMet":false,"completedPitServices":0}}"""));
        var telemetry = RaceProtocolJson.DeserializePayload<RaceTelemetryUpdate>(telemetryEnvelope);
        Assert.AreEqual(18, telemetry.TrackToleranceMeters, 0.0001);
        Assert.AreEqual(0, telemetry.TrackLengthMeters, 0.0001);
        Assert.IsFalse(telemetry.IsApproachingPit);
        Assert.IsFalse(telemetry.IsOnPitRoute);

        var lapEnvelope = RaceProtocolJson.DeserializeEnvelope(Encoding.UTF8.GetBytes(
            """{"protocolVersion":2,"type":"lapCompleted","sequence":3,"payload":{"eventId":"11111111-1111-1111-1111-111111111111","lapNumber":1,"lapSeconds":60,"sectorSeconds":[20,20,20],"isValid":true,"invalidReason":null,"clientMonotonicMilliseconds":60000}}"""));
        var lap = RaceProtocolJson.DeserializePayload<RaceLapCompleted>(lapEnvelope);
        Assert.IsTrue(lap.IsBestLapEligible);
    }

    [TestMethod]
    public void ChequeredFlagIsPreviewedForLeaderNearTheFinishWithoutEndingRaceEarly()
    {
        var coordinator = CreateCoordinator();
        var leader = Join(coordinator, 1).Accepted!;
        Assert.IsTrue(coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Race, null, 2, null, null)).IsAccepted);
        CompleteLap(coordinator, leader.ParticipantId, 1, 60);
        coordinator.UpdateTelemetry(leader.ParticipantId, Telemetry(1, .95));

        var approaching = coordinator.Snapshot();
        Assert.IsTrue(approaching.ChequeredImminent);
        Assert.AreEqual(RaceControlFlag.Green, approaching.Flag);
        Assert.AreNotEqual(RaceParticipantStatus.Finished, approaching.Participants.Single().Status);

        CompleteLap(coordinator, leader.ParticipantId, 2, 61);
        var finished = coordinator.Snapshot();
        Assert.IsFalse(finished.ChequeredImminent);
        Assert.AreEqual(RaceControlFlag.Chequered, finished.Flag);
    }

    [TestMethod]
    public void MinimumRequiredPitStopsDisqualifiesOnlyDriversWhoFinishBelowTheConfiguredCount()
    {
        var coordinator = CreateCoordinator();
        var settings = coordinator.RoomSettings();
        Assert.IsTrue(coordinator.ApplyRoomSettings(new RaceAdminRoomSettingsCommand(
            settings.SessionName,
            1,
            settings.SectorCount,
            settings.AutomaticYellowEnabled,
            settings.SlowSpeedKph,
            settings.SlowDurationSeconds,
            settings.SevereLateralOffsetMeters,
            settings.RecoveryDurationSeconds,
            settings.AllowTeams,
            settings.TrackName,
            settings.TrackId,
            settings.TrackRevision,
            settings.TrackPackageHash,
            settings.TeamCount,
            settings.DriversPerTeam,
            settings.Teams,
            settings.TrackLimitMode,
            1)).IsAccepted);
        var withoutStop = Join(coordinator, 1).Accepted!;
        var withStop = Join(coordinator, 2).Accepted!;
        Assert.IsTrue(coordinator.ApplySessionCommand(new RaceAdminSessionCommand(
            RaceSessionPhase.Race, null, 1, 0, null)).IsAccepted);

        Assert.IsTrue(coordinator.UpdateTelemetry(withStop.ParticipantId,
            Telemetry(0, 0.5) with
            {
                IsInPitLane = true,
                IsInServiceZone = true,
                PitServiceRequirementMet = true,
                CompletedPitServices = 1
            }).IsAccepted);
        CompleteLap(coordinator, withoutStop.ParticipantId, 1, 60);
        CompleteLap(coordinator, withStop.ParticipantId, 1, 61);

        var snapshot = coordinator.Snapshot();
        Assert.AreEqual(1, snapshot.MinimumRequiredPitStops);
        Assert.AreEqual(
            RaceParticipantStatus.Disqualified,
            snapshot.Participants.Single(item => item.Id == withoutStop.ParticipantId).Status);
        Assert.AreEqual(
            RaceParticipantStatus.Finished,
            snapshot.Participants.Single(item => item.Id == withStop.ParticipantId).Status);
        Assert.IsTrue(snapshot.Penalties!.Any(item =>
            item.ParticipantId == withoutStop.ParticipantId &&
            item.Kind == RacePenaltyKind.Disqualification &&
            item.IsAutomatic));
    }

    private static RaceCoordinator CreateCoordinator(int maximumParticipants = RaceProtocol.MaximumParticipants) =>
        new(new RaceServerOptions
        {
            PlayerPassword = "player-pass",
            AdminPassword = "admin-pass",
            MaximumParticipants = maximumParticipants,
            MinimumRequiredPitStops = 0
        });

    private static void SetTrackLimitMode(RaceCoordinator coordinator, TrackLimitEnforcementMode mode)
    {
        var settings = coordinator.RoomSettings();
        var result = coordinator.ApplyRoomSettings(new RaceAdminRoomSettingsCommand(
            settings.SessionName,
            settings.TotalRaceLaps,
            settings.SectorCount,
            settings.AutomaticYellowEnabled,
            settings.SlowSpeedKph,
            settings.SlowDurationSeconds,
            settings.SevereLateralOffsetMeters,
            settings.RecoveryDurationSeconds,
            settings.AllowTeams,
            settings.TrackName,
            settings.TrackId,
            settings.TrackRevision,
            settings.TrackPackageHash,
            settings.TeamCount,
            settings.DriversPerTeam,
            settings.Teams,
            mode));
        Assert.IsTrue(result.IsAccepted, result.Error);
    }

    private static RaceJoinResult Join(RaceCoordinator coordinator, int index) => coordinator.TryJoin(Login(index));

    private static RaceLoginRequest Login(int index) => new(
        "player-pass",
        $"车手 {index}",
        $"#{index * 1000 + 0x336699:X6}"[^7..],
        index % 2 == 0 ? "车队 2" : "车队 1",
        "test-client",
        null,
        null,
        null,
        null,
        null,
        index % 2 == 0 ? "team-2" : "team-1");

    private static RaceTelemetryUpdate Telemetry(int laps, double progress) => new(
        10_000,
        progress,
        0,
        progress,
        0.5,
        120,
        laps,
        1,
        30,
        false,
        false,
        true,
        false,
        RaceGripCondition.SlightlyReduced,
        0,
        false,
        0);

    private static RaceTelemetryUpdate CollisionTelemetry(
        long sequence,
        double worldX,
        double velocityX,
        long impactSequence,
        double impactMagnitude) => Telemetry(0, .5) with
        {
            ClientMonotonicMilliseconds = sequence * 100,
            HasWorldPosition = true,
            WorldX = worldX,
            WorldY = 0,
            WorldZ = 50,
            VelocityX = velocityX,
            VelocityY = 0,
            VelocityZ = 0,
            HasWorldVelocity = true,
            WorldVelocityX = velocityX,
            WorldVelocityY = 0,
            WorldVelocityZ = 0,
            ImpactSequence = impactSequence,
            ImpactMagnitudeMps = impactMagnitude,
            ImpactWorldX = worldX,
            ImpactWorldY = 0,
            ImpactWorldZ = 50,
            ImpactWorldVelocityX = velocityX,
            ImpactWorldVelocityY = 0,
            ImpactWorldVelocityZ = 0
        };

    private static void CompleteLap(
        RaceCoordinator coordinator,
        Guid participantId,
        int lap,
        double seconds,
        DateTimeOffset? receivedAt = null)
    {
        var result = coordinator.CompleteLap(participantId, new RaceLapCompleted(
            Guid.NewGuid(), lap, seconds, [seconds / 3, seconds / 3, seconds / 3], true, null, 50_000),
            receivedAt);
        Assert.IsTrue(result.IsAccepted, result.Error);
    }
}
