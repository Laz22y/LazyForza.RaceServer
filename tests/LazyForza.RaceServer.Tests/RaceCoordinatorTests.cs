using LazyForza.RaceServer.Core;
using LazyForza.RaceServer.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LazyForza.RaceServer.Tests;

[TestClass]
public sealed class RaceCoordinatorTests
{
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
    public void PasswordProfileDuplicateNameAndVisualTeamAreValidated()
    {
        var coordinator = CreateCoordinator();
        var invalidPassword = coordinator.TryJoin(Login(1) with { Password = "bad-password" });
        Assert.IsFalse(invalidPassword.IsAccepted);
        Assert.AreEqual("invalidPassword", invalidPassword.Rejected?.Code);

        var first = coordinator.TryJoin(Login(1) with { TeamName = "青岚车队", ThemeColor = "#19bde0" });
        Assert.IsTrue(first.IsAccepted);
        var participant = coordinator.Snapshot().Participants.Single();
        Assert.AreEqual("#19BDE0", participant.ThemeColor);
        Assert.AreEqual("青岚车队", participant.TeamName);

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
            "黄旗测试", 5, 3, true, 12, 3, 25, 3)).IsAccepted);
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

    private static RaceCoordinator CreateCoordinator(int maximumParticipants = RaceProtocol.MaximumParticipants) =>
        new(new RaceServerOptions
        {
            PlayerPassword = "player-pass",
            AdminPassword = "admin-pass",
            MaximumParticipants = maximumParticipants
        });

    private static RaceJoinResult Join(RaceCoordinator coordinator, int index) => coordinator.TryJoin(Login(index));

    private static RaceLoginRequest Login(int index) => new(
        "player-pass",
        $"车手 {index}",
        $"#{index * 1000 + 0x336699:X6}"[^7..],
        index % 2 == 0 ? "远山车队" : null,
        "test-client",
        null,
        null,
        null,
        null);

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

    private static void CompleteLap(RaceCoordinator coordinator, Guid participantId, int lap, double seconds)
    {
        var result = coordinator.CompleteLap(participantId, new RaceLapCompleted(
            Guid.NewGuid(), lap, seconds, [seconds / 3, seconds / 3, seconds / 3], true, null, 50_000));
        Assert.IsTrue(result.IsAccepted, result.Error);
    }
}
