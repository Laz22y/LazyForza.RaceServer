using System.IO.Compression;
using LazyForza.RaceServer.Core;
using LazyForza.RaceServer.Protocol;
using LazyForza.RaceServer.Web;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LazyForza.RaceServer.Tests;

[TestClass]
public sealed class RaceEventProjectStoreTests
{
    [TestMethod]
    public void CreatesSynchronizesCopiesAndReloadsProjects()
    {
        var root = TemporaryDirectory();
        try
        {
            var now = new DateTimeOffset(2026, 8, 28, 10, 0, 0, TimeSpan.Zero);
            var driverId = Guid.NewGuid();
            var store = new RaceEventProjectStore(new RaceServerOptions { DataDirectory = root });
            var created = store.Create(
                Request(" 周末杯 "),
                Room(),
                [],
                [],
                null,
                null,
                null,
                null,
                now);

            Assert.AreEqual("周末杯", created.Name);
            Assert.AreEqual(RaceEventProjectStatus.Draft, created.Status);
            Assert.AreEqual(2, created.Schedule.PracticeSessionCount);
            CollectionAssert.AreEqual(new[] { 45, 180 }, created.Schedule.PracticeSessionMinutes!.ToArray());

            var active = store.Activate(created.Id, now.AddMinutes(1));
            Assert.AreEqual(RaceEventProjectStatus.Active, active.Status);
            var resultId = Guid.NewGuid();
            store.SyncActive(
                [Result(driverId, now.AddHours(1), resultId)],
                [new RaceEventSnapshot(12, now.AddMinutes(2), "phaseChanged", "练习赛开始")],
                now.AddHours(1));

            var synchronized = store.Find(created.Id)!;
            Assert.HasCount(1, synchronized.Results);
            Assert.HasCount(1, synchronized.AuditEvents);
            Assert.AreEqual(now.AddMinutes(2), synchronized.AuditEvents[0].OccurredAt);
            var synchronizedRevision = synchronized.Revision;

            store.SyncActive(
                [Result(driverId, now.AddHours(1), resultId, 60.5)],
                [new RaceEventSnapshot(12, now.AddMinutes(2), "phaseChanged", "练习赛开始")],
                now.AddHours(1).AddMinutes(1));
            var revised = store.Find(created.Id)!;
            Assert.AreEqual(60.5, revised.Results[0].FastestLapSeconds);
            Assert.AreEqual(synchronizedRevision + 1, revised.Revision);

            store.SyncActive(
                [Result(driverId, now.AddHours(1), resultId, 60.5)],
                [new RaceEventSnapshot(12, now.AddMinutes(2), "phaseChanged", "练习赛开始")],
                now.AddHours(1).AddMinutes(2));
            Assert.AreEqual(revised.Revision, store.Find(created.Id)!.Revision);

            var copy = store.Copy(created.Id, "下一站", now.AddHours(2));
            Assert.AreEqual(RaceEventProjectStatus.Draft, copy.Status);
            Assert.HasCount(0, copy.Results);
            Assert.HasCount(0, copy.AuditEvents);

            var reloaded = new RaceEventProjectStore(new RaceServerOptions { DataDirectory = root });
            Assert.HasCount(2, reloaded.List());
            Assert.AreEqual(created.Id, reloaded.List()[0].Id);
            Assert.IsFalse(reloaded.Delete(Guid.NewGuid()));
            Assert.ThrowsExactly<InvalidDataException>(() => reloaded.Delete(created.Id));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ExportsImportsAndRejectsUnknownPackageEntries()
    {
        var sourceRoot = TemporaryDirectory();
        var destinationRoot = TemporaryDirectory();
        try
        {
            var now = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
            var source = new RaceEventProjectStore(new RaceServerOptions { DataDirectory = sourceRoot });
            var created = source.Create(
                Request("耐力赛"),
                Room(),
                [Result(Guid.NewGuid(), now)],
                [new RaceEventSnapshot(5, now, "result", "成绩已固化")],
                null,
                null,
                null,
                null,
                now);
            var package = source.Export(created.Id);

            var destination = new RaceEventProjectStore(new RaceServerOptions { DataDirectory = destinationRoot });
            var imported = destination.Import(package, now.AddHours(1));
            Assert.AreEqual(created.Id, imported.Id);
            Assert.AreEqual(RaceEventProjectStatus.Completed, imported.Status);
            Assert.AreEqual("耐力赛", imported.Name);
            Assert.HasCount(1, imported.Results);
            Assert.HasCount(1, imported.AuditEvents);

            var conflict = destination.Import(package, now.AddHours(2));
            Assert.AreNotEqual(created.Id, conflict.Id);

            var tampered = AddUnknownEntry(package);
            var exception = Assert.ThrowsExactly<InvalidDataException>(() => destination.Import(tampered));
            StringAssert.Contains(exception.Message, "未知");
        }
        finally
        {
            if (Directory.Exists(sourceRoot)) Directory.Delete(sourceRoot, recursive: true);
            if (Directory.Exists(destinationRoot)) Directory.Delete(destinationRoot, recursive: true);
        }
    }

    [TestMethod]
    public void MetadataPreservesRulesAndEventOwnershipSurvivesExportAndReload()
    {
        var root = TemporaryDirectory();
        var importedRoot = TemporaryDirectory();
        try
        {
            var store = new RaceEventProjectStore(new RaceServerOptions { DataDirectory = root });
            var first = store.Create(Request("First"), Room(), [], [], null, null, null, null);
            var second = store.Create(Request("Second"), Room(), [], [], null, null, null, null);
            store.Activate(first.Id, eventId: first.Id);
            var owned = Result(Guid.NewGuid(), DateTimeOffset.UtcNow) with { EventId = first.Id };
            var unrelated = Result(Guid.NewGuid(), DateTimeOffset.UtcNow) with { EventId = second.Id };
            var ownEvent = new RaceEventSnapshot(1, DateTimeOffset.UtcNow, "result", "First") with { EventId = first.Id };
            var otherEvent = new RaceEventSnapshot(2, DateTimeOffset.UtcNow, "result", "Second") with { EventId = second.Id };
            store.SyncActive([owned, unrelated], [ownEvent, otherEvent]);
            var updated = store.Capture(first.Id, Request("Renamed") with { Schedule = new(CountdownSeconds: 7) },
                Room() with { TotalRaceLaps = 99 }, [unrelated], [otherEvent], null, null, null, null);
            Assert.AreEqual(20, updated.Room.TotalRaceLaps);
            Assert.AreEqual(first.Schedule.CountdownSeconds, updated.Schedule.CountdownSeconds);
            CollectionAssert.AreEqual(first.Schedule.PracticeSessionMinutes!.ToArray(), updated.Schedule.PracticeSessionMinutes!.ToArray());
            Assert.AreEqual(owned.Id, updated.Results.Single().Id);
            Assert.AreEqual("First", updated.AuditEvents.Single().Message);
            store.Activate(second.Id, eventId: second.Id);
            store.SyncActive([owned, unrelated], [ownEvent, otherEvent]);
            Assert.AreEqual(unrelated.Id, store.Find(second.Id)!.Results.Single().Id);
            Assert.AreEqual(RaceEventProjectStatus.Completed, store.Find(first.Id)!.Status);
            Assert.ThrowsExactly<InvalidDataException>(() => store.Activate(first.Id));
            var imported = new RaceEventProjectStore(new RaceServerOptions { DataDirectory = importedRoot }).Import(store.Export(first.Id));
            Assert.AreEqual(first.Id, imported.EventId);
            Assert.AreEqual(first.Id, imported.Results.Single().EventId);
            var rebuilt = new RaceEventProjectStore(new RaceServerOptions { DataDirectory = root });
            Assert.AreEqual(first.Id, rebuilt.Find(first.Id)!.EventId);
            var copy = rebuilt.Copy(first.Id, "Next");
            Assert.IsNull(copy.EventId);
            Assert.HasCount(0, copy.Results);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); if (Directory.Exists(importedRoot)) Directory.Delete(importedRoot, true); }
    }

    private static RaceEventProjectSaveRequest Request(string name) => new(
        name,
        "WC",
        "LazyForza Club",
        "周末测试赛事",
        new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero),
        "Asia/Shanghai",
        new RaceEventSchedule(
            CountdownSeconds: 130,
            PracticeSessionCount: 2,
            PracticeSessionMinutes: [45, 240],
            QualifyingSessionCount: 3,
            QualifyingSessionMinutes: [18, 15, 12],
            QualifyingEliminationCounts: [3, 2]));

    private static RaceRoomSettingsSnapshot Room() => new(
        "周末杯", 20, 3, true, 12, 3, 25, 3, true,
        "山谷环线", null, null, null, 2, 6,
        [new RaceTeamDefinition("team-1", "厂队", "#42D7E8")],
        TrackLimitEnforcementMode.WarningsOnly, 1, true, true);

    private static RaceStageResultSnapshot Result(
        Guid driverId,
        DateTimeOffset completedAt,
        Guid? resultId = null,
        double fastestLapSeconds = 61.25) => new(
        resultId ?? Guid.NewGuid(),
        RaceSessionPhase.Practice,
        "FP1",
        1,
        2,
        true,
        completedAt,
        "周末杯",
        "山谷环线",
        driverId,
        fastestLapSeconds,
        [new RaceStageResultParticipantSnapshot(
            driverId, 1, "Driver 1", "#42D7E8", "厂队", "#42D7E8",
            RaceParticipantStatus.OnTrack, 5, 0.8, fastestLapSeconds, null, null, null, 0, [])]);

    private static byte[] AddUnknownEntry(byte[] package)
    {
        using var stream = new MemoryStream();
        stream.Write(package);
        stream.Position = 0;
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = archive.CreateEntry("private/credentials.json", CompressionLevel.NoCompression);
            using var output = entry.Open();
            output.Write("{}"u8);
        }
        return stream.ToArray();
    }

    private static string TemporaryDirectory() =>
        Path.Combine(Path.GetTempPath(), "LazyForza-RaceServer-Test-" + Guid.NewGuid().ToString("N"));
}
