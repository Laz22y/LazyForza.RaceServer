using LazyForza.RaceServer.Protocol;

namespace LazyForza.RaceServer.Core;

public sealed record LapValidationSample(long At, double Progress, bool Reliable, bool Pit);
public sealed record LapValidationResult(bool CanAccept, RaceLapValidationStatus Status, string Reason);

public static class LapCompletionValidator
{
    public static LapValidationResult Validate(RaceLapCompleted lap, Guid? stageId, int sectorCount,
        int? lastNumber, bool lastWasValid, IReadOnlyList<LapValidationSample> samples)
    {
        static LapValidationResult Reject(string reason) => new(false, RaceLapValidationStatus.Rejected, reason);
        if (lap.EventId == Guid.Empty || lap.LapNumber < 1 || lap.ClientMonotonicMilliseconds is < 0 or > 9_007_199_254_740_991)
            return Reject("圈事件编号、圈序或客户端时间无效。");
        if (lap.StageId is not null && lap.StageId != stageId)
            return Reject("圈事件不属于当前赛事阶段。");
        if (lastNumber is int prior && (lap.LapNumber < prior || lap.LapNumber == prior && lastWasValid))
            return Reject("圈序已经处理或已过期。");
        if (!double.IsFinite(lap.LapSeconds) || lap.LapSeconds < (lap.IsValid ? 3 : 0) || lap.LapSeconds > 21_600)
            return Reject("圈速超出允许范围。");
        if (lap.SectorSeconds is null ||
            (lap.IsValid || lap.SectorSeconds.Count > 0) && lap.SectorSeconds.Count != sectorCount)
            return Reject("圈事件分段数量与赛道不一致。");
        if (lap.SectorSeconds.Any(value => !double.IsFinite(value) || value < 0 || value > 21_600))
            return Reject("圈事件分段时间无效。");
        if (!lap.IsValid)
            return new(true, RaceLapValidationStatus.Rejected, "客户端已将本圈标为无效。");
        if (lastNumber is int last && (long)lap.LapNumber > (long)last + 1)
            return new(true, RaceLapValidationStatus.PendingReview, "圈序存在缺口，需核对遗漏圈事件。");
        // Client BuildSegments uses non-negative durations and includes pit time in elapsed lap time.
        if (Math.Abs(lap.SectorSeconds.Sum() - lap.LapSeconds) > Math.Max(.05, lap.LapSeconds * .001))
            return new(true, RaceLapValidationStatus.PendingReview, "分段总时与圈时不一致，需核对计时样本。");
        var window = samples.Where(item => item.At >= lap.ClientMonotonicMilliseconds - lap.LapSeconds * 1000 &&
            item.At <= lap.ClientMonotonicMilliseconds).ToArray();
        var sufficient = window.Length >= 8 && window.All(item => item.Reliable && !item.Pit) &&
            window[0].At - (lap.ClientMonotonicMilliseconds - lap.LapSeconds * 1000) <= 1500 &&
            lap.ClientMonotonicMilliseconds - window[^1].At <= 1500;
        double progress = 0;
        for (var i = 1; i < window.Length && sufficient; i++)
        {
            var gap = window[i].At - window[i - 1].At;
            var delta = window[i].Progress - window[i - 1].Progress;
            if (delta < -.5) delta += 1;
            if (gap is <= 0 or > 2000 || delta is < -.02 or > .25) sufficient = false;
            else progress += Math.Max(0, delta);
        }
        if (sufficient && lap.StageId is not null && lap.SectorSeconds.All(value => value > 0) && (progress < .65 || progress > 1.35))
            return new(true, RaceLapValidationStatus.PendingReview, "连续遥测进度与完成一圈不一致，需人工核对。");
        if (!sufficient || lap.StageId is null || lap.SectorSeconds.Any(value => value == 0))
            return new(true, RaceLapValidationStatus.InsufficientEvidence, "阶段或连续遥测证据不足，未作完整交叉核验。");
        return new(true, RaceLapValidationStatus.Verified, "圈事件结构与连续遥测交叉核验通过。");
    }
}
