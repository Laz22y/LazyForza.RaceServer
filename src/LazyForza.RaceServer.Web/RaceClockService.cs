using LazyForza.RaceServer.Core;

namespace LazyForza.RaceServer.Web;

public sealed class RaceClockService(
    RaceCoordinator coordinator,
    RaceBroadcastService broadcasts) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        var nextHeartbeatAt = DateTimeOffset.MinValue;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = DateTimeOffset.UtcNow;
                coordinator.Tick(now);
                if (now < nextHeartbeatAt) continue;
                broadcasts.Queue(coordinator.Snapshot(now));
                nextHeartbeatAt = now.AddSeconds(1);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
