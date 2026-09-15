using LazyForza.RaceServer.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LazyForza.RaceServer.Tests;

[TestClass]
public sealed class RaceProgressTimelineTests
{
    [TestMethod]
    public void DelayedReceiptDoesNotChangeExistingPassageTimes()
    {
        var timeline = new RaceProgressTimeline();
        timeline.Observe(.8, 0, 0, 48_000, 48);
        timeline.Observe(.1, 0, 0, 70_000, 70);
        timeline.Observe(.4, 0, 0, 90_000, 90);
        var before = timeline.Samples.ToArray();
        timeline.ConfirmCompletedLaps(1);
        timeline.ConfirmCompletedLaps(1);
        CollectionAssert.AreEqual(before, timeline.Samples.ToArray());
        Assert.AreEqual(90, timeline.PassageTime(1.4));
        timeline.Observe(.5, 1, 1, 95_000, 95);
        Assert.AreEqual(95, timeline.PassageTime(1.5));
    }

    [TestMethod]
    public void ReceiptAnchorAcceptsLateNextLapTelemetryEvenPastThreeQuarters()
    {
        var timeline = new RaceProgressTimeline();
        timeline.Observe(.8, 0, 0, 48_000, 48);
        timeline.ConfirmCompletedLaps(1);
        timeline.Observe(.99, 0, 1, 59_000, 61);
        Assert.AreEqual(.8, timeline.Samples[^1].DistanceLaps);
        timeline.Observe(.85, 1, 1, 110_000, 110);
        Assert.AreEqual(1.85, timeline.Samples[^1].DistanceLaps, .00001);
        Assert.AreEqual(110, timeline.PassageTime(1.85));
    }

    [TestMethod]
    public void ReverseOverFinishAndReturnCannotCreateAnotherLapOfDistance()
    {
        var timeline = new RaceProgressTimeline();
        timeline.Observe(.98, 0, 0, 58_000, 58);
        timeline.Observe(.01, 0, 0, 60_000, 60);
        timeline.Observe(.999, 0, 0, 61_000, 61);
        timeline.Observe(.015, 0, 0, 62_000, 62);
        Assert.AreEqual(1.015, timeline.Samples[^1].DistanceLaps, .00001);
        Assert.AreEqual(60, timeline.PassageTime(1.01));
    }

    [TestMethod]
    public void OlderPacketsAndStationarySamplesCannotRewritePassageTimes()
    {
        var timeline = new RaceProgressTimeline();
        timeline.Observe(.98, 0, 0, 58_000, 58);
        timeline.Observe(.02, 0, 0, 62_000, 62);
        timeline.Observe(.8, 0, 0, 57_000, 63);
        timeline.Observe(.02, 0, 0, 70_000, 70);
        timeline.Observe(.1, 0, 0, 80_000, 80);
        Assert.HasCount(3, timeline.Samples);
        Assert.AreEqual(62, timeline.PassageTime(1.02));
        Assert.AreEqual(80, timeline.PassageTime(1.1));
    }

    [TestMethod]
    public void ReconnectionResetsClockAndNewStageClearsTheEntireTimeline()
    {
        var timeline = new RaceProgressTimeline();
        timeline.Observe(.98, 0, 0, 58_000, 58);
        timeline.ResetClientClock();
        timeline.Observe(.02, 0, 0, 500, 68);
        Assert.AreEqual(1.02, timeline.Samples[^1].DistanceLaps, .00001);
        timeline.Reset();
        Assert.HasCount(0, timeline.Samples);
        timeline.Observe(.98, 0, 0, 100, 1);
        timeline.Observe(.01, 0, 0, 200, 3);
        Assert.HasCount(1, timeline.Samples);
        Assert.AreEqual(.01, timeline.Samples[^1].DistanceLaps, .00001);
    }

    [TestMethod]
    public void DenseLongRaceKeepsBoundedHistoryAndInterpolatesForwardSamples()
    {
        var timeline = new RaceProgressTimeline();
        for (var i = 1; i <= 20_000; i++)
            timeline.Observe(i % 1000 / 1000d, 0, 0, i * 100, 10 + i / 10d);
        Assert.IsTrue(timeline.Samples.Count <= 3_600);
        Assert.AreEqual(20, timeline.Samples[^1].DistanceLaps, .00001);
        Assert.AreEqual(2009.95, timeline.PassageTime(19.9995)!.Value, .00001);
        Assert.IsNull(timeline.PassageTime(10));
    }
}
