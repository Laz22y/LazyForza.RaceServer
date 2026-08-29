using System.Text.Json;
using LazyForza.RaceServer.Protocol;
using LazyForza.RaceServer.Web;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LazyForza.RaceServer.Tests;

[TestClass]
public sealed class PublicTimingTests
{
    [TestMethod]
    public void ProjectionPublishesTimingWithoutInternalIdentifiersOrTelemetryCoordinates()
    {
        var now = new DateTimeOffset(2026, 8, 29, 8, 30, 0, TimeSpan.Zero);
        var participantId = Guid.NewGuid();
        var penalty = new RacePenaltySnapshot(
            Guid.NewGuid(), participantId, RacePenaltyKind.Time, 5, null,
            "碰撞责任", now.AddMinutes(-1), false, false);
        var participant = new RaceParticipantSnapshot(
            participantId, 1, "Driver One", "#42D7E8", "Team One",
            RaceParticipantStatus.OnTrack, true, true, 4, 2, 0.62,
            123.5, 456.75, 218, 42.125, 61.25, 59.123, 0, null,
            false, false, 0, false, 1, RaceGripCondition.Unknown,
            [20.1, 19.8, 19.223], [penalty], now,
            TeamColor: "#102030", TimePenaltySeconds: 5,
            PendingTimePenaltySeconds: 5);
        var state = new RaceSessionSnapshot(
            7, "直播测试", RaceSessionPhase.Race, RaceControlFlag.Yellow, "赛道事故",
            "track-id", "revision-id", "package-hash", 10, now.AddMinutes(-10), null,
            participantId, 59.123, [20.1, 19.8, 19.223], null, [participant], now,
            YellowZones: [new RaceYellowZoneSnapshot(2, true, "事故车辆", participantId, "Driver One")],
            TrackName: "测试赛道", RaceElapsedSeconds: 600,
            Investigations: [], Observers: [], MinimumRequiredPitStops: 2);
        var resultParticipant = new RaceStageResultParticipantSnapshot(
            participantId, 1, "Driver One", "#42D7E8", "Team One", "#102030",
            RaceParticipantStatus.Finished, 3, 1, 59.123, 180.5, 185.5, 0, 5, [penalty]);
        var result = new RaceStageResultSnapshot(
            Guid.NewGuid(), RaceSessionPhase.Qualifying, "Q1", 1, 1, true,
            now.AddMinutes(-5), "直播测试", "测试赛道", participantId, 59.123,
            [resultParticipant]);

        var payload = RacePublicTimingProjection.Create(state, [result]);
        var json = JsonSerializer.Serialize(payload, RaceProtocolJson.Options);

        Assert.AreEqual("Driver One", payload.State.FastestDriverName);
        Assert.AreEqual(2, payload.State.MinimumRequiredPitStops);
        Assert.HasCount(1, payload.Results);
        StringAssert.Contains(json, "\"gapToLeaderSeconds\":0");
        StringAssert.Contains(json, "\"pendingTimePenaltySeconds\":5");
        StringAssert.Contains(json, "\"participantName\":\"Driver One\"");
        Assert.IsFalse(json.Contains("participantId", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("mapX", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("mapY", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("lastSeenAt", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("investigations", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("observers", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains(participantId.ToString(), StringComparison.OrdinalIgnoreCase));
    }
}
