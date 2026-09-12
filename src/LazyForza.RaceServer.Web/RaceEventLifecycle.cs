using System.Text.Json;
using LazyForza.RaceServer.Core;
using LazyForza.RaceServer.Protocol;

namespace LazyForza.RaceServer.Web;

// A durable intent makes the multi-file operation replayable before HTTP/WS start.
// A receipt is returned only once the event, assets, settings and project are saved.
public sealed class RaceEventLifecycle(
    RaceServerOptions options,
    RaceCoordinator coordinator,
    RaceEventProjectStore projects,
    RaceServerConfigurationStore settings,
    HostedTrackPackageStore tracks,
    HostedOrganizerLogoStore logos)
{
    private readonly string pendingPath = Path.Combine(Path.GetFullPath(options.DataDirectory), "pending-event.json");
    public bool HasPending => File.Exists(pendingPath);

    public async Task PrepareAsync(Guid? projectId, CancellationToken token = default)
    {
        if (HasPending)
        {
            var pending = ReadIntent();
            await ApplyAsync(pending, token);
            if (pending.ProjectId == projectId) return;
        }
        if (coordinator.Snapshot().Phase is not (RaceSessionPhase.Lobby or RaceSessionPhase.Finished))
            throw new InvalidDataException("请先结束当前阶段或返回大厅，再准备新赛事。");
        if (projectId is Guid id)
        {
            var project = projects.Find(id) ?? throw new KeyNotFoundException();
            if (project.Status == RaceEventProjectStatus.Active && project.EventId == coordinator.Snapshot().EventId) return;
            if (project.Status != RaceEventProjectStatus.Draft || project.Results.Count > 0)
                throw new InvalidDataException("该项目已有赛事记录，请复制为新赛事后使用。");
            _ = projects.ReadAssets(id);
            var validated = new RaceCoordinator(options).ApplyRoomSettings(ToCommand(project.Room));
            if (!validated.IsAccepted) throw new InvalidDataException(validated.Error);
        }
        projects.SyncActive(coordinator.Results(), coordinator.Events(500));
        var intent = new PendingEvent(1, projectId ?? Guid.NewGuid(), projectId);
        Directory.CreateDirectory(Path.GetDirectoryName(pendingPath)!);
        using (var stream = new FileStream(pendingPath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, intent);
            stream.Flush(flushToDisk: true);
        }
        File.Move(pendingPath + ".tmp", pendingPath, overwrite: true);
        await ApplyAsync(intent, token);
    }

    public Task RecoverAsync(CancellationToken token = default) =>
        HasPending ? ApplyAsync(ReadIntent(), token) : Task.CompletedTask;

    private PendingEvent ReadIntent()
    {
        var intent = JsonSerializer.Deserialize<PendingEvent>(File.ReadAllText(pendingPath));
        if (intent is null || intent.Version != 1 || intent.EventId == Guid.Empty)
            throw new InvalidDataException("不支持的赛事切换恢复记录，原文件已保留。");
        return intent;
    }

    private async Task ApplyAsync(PendingEvent intent, CancellationToken token)
    {
        var project = intent.ProjectId is Guid id ? projects.Find(id) ?? throw new InvalidDataException("待恢复的赛事项目不存在。") : null;
        // Archive using the old event's name and rules before changing configuration.
        var prepared = coordinator.BeginEvent(intent.EventId, project?.Name);
        if (!prepared.IsAccepted) throw new InvalidDataException(prepared.Error);
        if (project is not null)
        {
            var assets = projects.ReadAssets(project.Id);
            var applied = coordinator.ApplyRoomSettings(ToCommand(project.Room) with { SessionName = project.Name });
            if (!applied.IsAccepted) throw new InvalidDataException(applied.Error);
            if (project.TrackPackage is not null && assets.TrackPackage is not null)
            {
                await using var stream = new MemoryStream(assets.TrackPackage, writable: false);
                await tracks.SaveAsync(stream, project.TrackPackage.FileName, token);
            }
            else await tracks.DeleteAsync(token);
            if (project.OrganizerLogo is not null && assets.OrganizerLogo is not null)
            {
                await using var stream = new MemoryStream(assets.OrganizerLogo, writable: false);
                await logos.SaveAsync(stream, project.OrganizerLogo.FileName, project.OrganizerLogo.MimeType, token);
            }
            else await logos.DeleteAsync(token);
            settings.SaveRoomSettings(coordinator.RoomSettings());
            settings.SaveSchedule(project.Schedule);
            if (project.Status != RaceEventProjectStatus.Active || project.EventId != intent.EventId)
                projects.Activate(project.Id, eventId: intent.EventId);
        }
        else
        {
            foreach (var active in projects.List().Where(p => p.Status == RaceEventProjectStatus.Active))
                projects.SetStatus(active.Id, RaceEventProjectStatus.Completed);
        }
        File.Delete(pendingPath);
    }

    public static RaceAdminRoomSettingsCommand ToCommand(RaceRoomSettingsSnapshot room) => new(
        room.SessionName, room.TotalRaceLaps, room.SectorCount, room.AutomaticYellowEnabled,
        room.SlowSpeedKph, room.SlowDurationSeconds, room.SevereLateralOffsetMeters,
        room.RecoveryDurationSeconds, room.AllowTeams, room.TrackName, room.TrackId,
        room.TrackRevision, room.TrackPackageHash, room.TeamCount, room.DriversPerTeam,
        room.Teams, room.TrackLimitMode, room.MinimumRequiredPitStops,
        room.AutomaticCollisionInvestigationsEnabled, room.DisconnectedLapRecoveryEnabled);

    private sealed record PendingEvent(int Version, Guid EventId, Guid? ProjectId);
}
