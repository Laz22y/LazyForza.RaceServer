namespace LazyForza.RaceServer.Core;

/// <summary>Physical passage times; independent of pit service, penalties and scored laps.</summary>
internal sealed class RaceProgressTimeline
{
    private const int MaximumSamples = 3_600;
    private const double HistoryLaps = 1.25;
    private readonly List<Sample> samples = [];
    private int lapOffset;
    private bool initialized, awaitingAnchor;
    private double? previousProgress;
    private long lastClientMilliseconds;

    public IReadOnlyList<Sample> Samples => samples;

    public void Reset()
    {
        samples.Clear();
        lapOffset = 0;
        initialized = awaitingAnchor = false;
        previousProgress = null;
        ResetClientClock();
    }

    // A reconnected process can have a new monotonic clock. Its race distance survives.
    public void ResetClientClock() => lastClientMilliseconds = 0;

    public void ConfirmCompletedLaps(int completedLaps)
    {
        if (initialized && lapOffset >= completedLaps) return;
        initialized = awaitingAnchor = true;
        lapOffset = completedLaps;
        // Receipt time is not passage time: the next ordered telemetry supplies it.
    }

    public void Observe(double progress, int reportedLaps, int scoredLaps,
        long clientMilliseconds, double elapsedSeconds)
    {
        if (!double.IsFinite(progress) || !double.IsFinite(elapsedSeconds) || elapsedSeconds < 0) return;
        if (clientMilliseconds > 0 && lastClientMilliseconds > clientMilliseconds) return;
        lastClientMilliseconds = Math.Max(lastClientMilliseconds, clientMilliseconds);
        progress = Math.Clamp(progress, 0, 1);
        if (!initialized)
        {
            // The initial grid crossing starts lap zero, without scoring a lap.
            lapOffset = scoredLaps == 0 && elapsedSeconds < 10 && progress > .75 ? -1 : scoredLaps;
            initialized = true;
        }
        if (awaitingAnchor)
        {
            var crossed = previousProgress is double previous && previous - progress > .5;
            if (reportedLaps < lapOffset && !crossed && progress > .25) return;
            awaitingAnchor = false;
        }
        else if (previousProgress is double previous)
        {
            if (previous - progress > .5) lapOffset++;
            // Backing over the line and returning must reach the same distance again.
            else if (progress - previous > .75) lapOffset--;
        }
        previousProgress = progress;
        var distance = lapOffset + progress;
        if (distance < 0 || samples.Count > 0 &&
            (distance <= samples[^1].DistanceLaps || elapsedSeconds < samples[^1].ElapsedSeconds)) return;
        // Keep the FIRST passage time, including during a stationary service stop.
        samples.Add(new(distance, elapsedSeconds));
        var removeCount = 0;
        while (removeCount < samples.Count - 2 && samples[removeCount].DistanceLaps < distance - HistoryLaps)
            removeCount++;
        removeCount = Math.Max(removeCount, samples.Count - MaximumSamples);
        if (removeCount > 0) samples.RemoveRange(0, removeCount);
    }

    public double? PassageTime(double distance)
    {
        if (samples.Count == 0 || distance < samples[0].DistanceLaps || distance > samples[^1].DistanceLaps) return null;
        var lower = 0;
        var upper = samples.Count - 1;
        while (lower < upper)
        {
            var middle = lower + (upper - lower) / 2;
            if (samples[middle].DistanceLaps < distance) lower = middle + 1;
            else upper = middle;
        }
        var next = samples[lower];
        if (Math.Abs(next.DistanceLaps - distance) <= 1e-9) return next.ElapsedSeconds;
        if (lower == 0) return null;
        var previous = samples[lower - 1];
        var fraction = (distance - previous.DistanceLaps) / (next.DistanceLaps - previous.DistanceLaps);
        return previous.ElapsedSeconds + (next.ElapsedSeconds - previous.ElapsedSeconds) * fraction;
    }

    public readonly record struct Sample(double DistanceLaps, double ElapsedSeconds);
}
