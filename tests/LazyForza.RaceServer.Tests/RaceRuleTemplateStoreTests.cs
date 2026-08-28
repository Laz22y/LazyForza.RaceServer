using LazyForza.RaceServer.Core;
using LazyForza.RaceServer.Protocol;
using LazyForza.RaceServer.Web;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LazyForza.RaceServer.Tests;

[TestClass]
public sealed class RaceRuleTemplateStoreTests
{
    [TestMethod]
    public void CreatesNormalizesReloadsUpdatesAndDeletesTemplates()
    {
        var root = TemporaryDirectory();
        try
        {
            var createdAt = new DateTimeOffset(2026, 8, 28, 8, 0, 0, TimeSpan.Zero);
            var store = new RaceRuleTemplateStore(new RaceServerOptions { DataDirectory = root });
            var created = store.Create(new RaceRuleTemplateSaveRequest(
                "  冲刺赛  ",
                new RaceRuleTemplateRules(
                    TotalRaceLaps: 1_200,
                    MinimumRequiredPitStops: -2,
                    SectorCount: 0,
                    SlowSpeedKph: 80,
                    CountdownSeconds: 150,
                    PracticeSessionCount: 2,
                    PracticeSessionMinutes: [45, 240],
                    QualifyingSessionCount: 3,
                    QualifyingSessionMinutes: [18, 15, 0],
                    QualifyingEliminationCounts: [5, 20])),
                createdAt);

            Assert.AreEqual("冲刺赛", created.Name);
            Assert.AreEqual(999, created.Rules.TotalRaceLaps);
            Assert.AreEqual(0, created.Rules.MinimumRequiredPitStops);
            Assert.AreEqual(1, created.Rules.SectorCount);
            Assert.AreEqual(50d, created.Rules.SlowSpeedKph);
            Assert.AreEqual(120, created.Rules.CountdownSeconds);
            CollectionAssert.AreEqual(new[] { 45, 180 }, created.Rules.PracticeSessionMinutes!.ToArray());
            CollectionAssert.AreEqual(new[] { 18, 15, 1 }, created.Rules.QualifyingSessionMinutes!.ToArray());
            CollectionAssert.AreEqual(new int?[] { 5, 11 }, created.Rules.QualifyingEliminationCounts!.ToArray());

            var reloaded = new RaceRuleTemplateStore(new RaceServerOptions { DataDirectory = root });
            Assert.AreEqual(created.Id, reloaded.List().Single().Id);
            Assert.ThrowsExactly<InvalidDataException>(() => reloaded.Create(
                new RaceRuleTemplateSaveRequest("冲刺赛", new RaceRuleTemplateRules())));

            var updatedAt = createdAt.AddHours(1);
            var updated = reloaded.Update(created.Id, new RaceRuleTemplateSaveRequest(
                "耐力赛",
                new RaceRuleTemplateRules(TotalRaceLaps: 120, CountdownSeconds: 30)), updatedAt);
            Assert.AreEqual(created.CreatedAt, updated.CreatedAt);
            Assert.AreEqual(updatedAt, updated.UpdatedAt);
            Assert.AreEqual(120, updated.Rules.TotalRaceLaps);
            Assert.IsTrue(reloaded.Delete(created.Id));
            Assert.IsFalse(reloaded.Delete(created.Id));
            Assert.AreEqual(0, new RaceRuleTemplateStore(
                new RaceServerOptions { DataDirectory = root }).List().Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ApplyingTemplatePreservesEventTrackAndTeamIdentity()
    {
        var trackId = Guid.NewGuid().ToString("D");
        var teams = new[]
        {
            new RaceTeamDefinition("factory", "厂队", "#123456"),
            new RaceTeamDefinition("privateer", "私人车队", "#ABCDEF")
        };
        var current = new RaceRoomSettingsSnapshot(
            "周末正赛", 10, 3, true, 12, 3, 25, 3, true,
            "山谷环线", trackId, "rev-7", "HASH-7", 2, 6, teams);
        var template = new RaceRuleTemplateSnapshot(
            Guid.NewGuid(), "耐力赛", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new RaceRuleTemplateRules(
                TotalRaceLaps: 120,
                MinimumRequiredPitStops: 4,
                SectorCount: 5,
                AutomaticYellowEnabled: false,
                AutomaticCollisionInvestigationsEnabled: true,
                DisconnectedLapRecoveryEnabled: true,
                TrackLimitMode: TrackLimitEnforcementMode.Automatic,
                TeamCount: 3,
                DriversPerTeam: 4));

        var merged = RaceRuleTemplateStore.MergeWithRoom(template, current);

        Assert.AreEqual(current.SessionName, merged.SessionName);
        Assert.AreEqual(current.TrackName, merged.TrackName);
        Assert.AreEqual(current.TrackId, merged.TrackId);
        Assert.AreEqual(current.TrackRevision, merged.TrackRevision);
        Assert.AreEqual(current.TrackPackageHash, merged.TrackPackageHash);
        Assert.AreEqual(120, merged.TotalRaceLaps);
        Assert.AreEqual(4, merged.MinimumRequiredPitStops);
        Assert.AreEqual(5, merged.SectorCount);
        Assert.IsFalse(merged.AutomaticYellowEnabled);
        Assert.IsTrue(merged.AutomaticCollisionInvestigationsEnabled);
        Assert.IsTrue(merged.DisconnectedLapRecoveryEnabled);
        Assert.AreEqual(TrackLimitEnforcementMode.Automatic, merged.TrackLimitMode);
        Assert.AreEqual(3, merged.Teams!.Count);
        Assert.AreEqual(teams[0], merged.Teams[0]);
        Assert.AreEqual(teams[1], merged.Teams[1]);
        Assert.AreEqual("车队 3", merged.Teams[2].Name);
    }

    private static string TemporaryDirectory() =>
        Path.Combine(Path.GetTempPath(), "LazyForza-RaceServer-Test-" + Guid.NewGuid().ToString("N"));
}
