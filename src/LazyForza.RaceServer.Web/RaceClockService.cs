using LazyForza.RaceServer.Core;

namespace LazyForza.RaceServer.Web;

public sealed class RaceClockService(RaceCoordinator coordinator) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                coordinator.Tick(DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
