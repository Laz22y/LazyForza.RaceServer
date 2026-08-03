using System.Threading.Channels;
using LazyForza.RaceServer.Core;
using LazyForza.RaceServer.Protocol;

namespace LazyForza.RaceServer.Web;

public sealed class RaceBroadcastService : BackgroundService
{
    private readonly RaceCoordinator coordinator;
    private readonly RaceWebSocketRegistry registry;
    private readonly Channel<RaceSessionSnapshot> snapshots = Channel.CreateBounded<RaceSessionSnapshot>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    private long sequence;

    public RaceBroadcastService(RaceCoordinator coordinator, RaceWebSocketRegistry registry)
    {
        this.coordinator = coordinator;
        this.registry = registry;
        coordinator.SnapshotChanged += Queue;
    }

    public void Queue(RaceSessionSnapshot snapshot) => snapshots.Writer.TryWrite(snapshot);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var snapshot in snapshots.Reader.ReadAllAsync(stoppingToken))
            {
                var message = RaceProtocolJson.Serialize(RaceMessageTypes.Snapshot, Interlocked.Increment(ref sequence), snapshot);
                await registry.BroadcastAsync(message, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override void Dispose()
    {
        coordinator.SnapshotChanged -= Queue;
        base.Dispose();
    }
}
