using System.Threading.Channels;
using LazyForza.RaceServer.Core;
using LazyForza.RaceServer.Protocol;

namespace LazyForza.RaceServer.Web;

public sealed class RaceBroadcastService : BackgroundService
{
    internal static readonly TimeSpan MinimumBroadcastInterval = TimeSpan.FromMilliseconds(100);
    private readonly RaceCoordinator coordinator;
    private readonly RaceWebSocketRegistry registry;
    private readonly HostedOrganizerLogoStore organizerLogo;
    private readonly ILogger<RaceBroadcastService> logger;
    private readonly Channel<RaceSessionSnapshot> snapshots = Channel.CreateBounded<RaceSessionSnapshot>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    private long sequence;

    public RaceBroadcastService(
        RaceCoordinator coordinator,
        RaceWebSocketRegistry registry,
        HostedOrganizerLogoStore organizerLogo,
        ILogger<RaceBroadcastService> logger)
    {
        this.coordinator = coordinator;
        this.registry = registry;
        this.organizerLogo = organizerLogo;
        this.logger = logger;
        coordinator.SnapshotChanged += Queue;
    }

    public void Queue(RaceSessionSnapshot snapshot) => snapshots.Writer.TryWrite(snapshot);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastBroadcastAt = DateTimeOffset.MinValue;
        try
        {
            while (await snapshots.Reader.WaitToReadAsync(stoppingToken))
            {
                try
                {
                    if (!snapshots.Reader.TryRead(out var snapshot)) continue;
                    while (snapshots.Reader.TryRead(out var newer)) snapshot = newer;

                    var remaining = MinimumBroadcastInterval - (DateTimeOffset.UtcNow - lastBroadcastAt);
                    if (remaining > TimeSpan.Zero)
                        await Task.Delay(remaining, stoppingToken);
                    while (snapshots.Reader.TryRead(out var newer)) snapshot = newer;

                    var message = RaceProtocolJson.SerializeToUtf8Bytes(
                        RaceMessageTypes.Snapshot,
                        Interlocked.Increment(ref sequence),
                        WithOrganizerLogo(snapshot));
                    var disconnected = await registry.BroadcastAsync(message, stoppingToken);
                    foreach (var participantId in disconnected)
                        coordinator.Disconnect(participantId);
                    lastBroadcastAt = DateTimeOffset.UtcNow;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Race snapshot broadcast failed; the broadcast loop will continue.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public RaceSessionSnapshot WithOrganizerLogo(RaceSessionSnapshot snapshot)
    {
        var logo = organizerLogo.Current;
        return snapshot with
        {
            OrganizerLogoHash = logo?.Sha256,
            OrganizerLogoMimeType = logo?.MimeType,
            OrganizerLogoDownloadPath = logo is null ? null : "/api/organizer-logo"
        };
    }

    public override void Dispose()
    {
        coordinator.SnapshotChanged -= Queue;
        base.Dispose();
    }
}
