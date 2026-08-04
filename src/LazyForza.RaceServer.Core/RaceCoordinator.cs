using System.Security.Cryptography;
using System.Text;
using LazyForza.RaceServer.Protocol;

namespace LazyForza.RaceServer.Core;

public sealed record RaceJoinResult(
    bool IsAccepted,
    RaceLoginAccepted? Accepted,
    RaceLoginRejected? Rejected)
{
    public static RaceJoinResult Accept(RaceLoginAccepted value) => new(true, value, null);
    public static RaceJoinResult Reject(string code, string message) =>
        new(false, null, new RaceLoginRejected(code, message));
}

public sealed record RaceCommandResult(bool IsAccepted, string? Error = null)
{
    public static RaceCommandResult Accepted { get; } = new(true);
    public static RaceCommandResult Reject(string error) => new(false, error);
}

public sealed class RaceCoordinator
{
    private readonly object sync = new();
    private readonly RaceServerOptions options;
    private readonly IRaceStatePersistence persistence;
    private readonly List<ParticipantState> participants = [];
    private readonly List<RacePenaltySnapshot> penalties = [];
    private readonly HashSet<Guid> receivedLapEvents = [];
    private readonly Func<string, bool> playerPasswordMatches;
    private readonly Dictionary<int, string> manualSectorYellows = [];
    private string? manualFullCourseYellow;
    private RaceSessionPhase phase = RaceSessionPhase.Lobby;
    private RaceSessionPhase phaseBeforeSuspension = RaceSessionPhase.Race;
    private RaceControlFlag flag = RaceControlFlag.Green;
    private string? flagMessage;
    private string sessionName;
    private int totalRaceLaps;
    private int sectorCount;
    private bool automaticYellowEnabled;
    private double slowSpeedKph;
    private double slowDurationSeconds;
    private double severeLateralOffsetMeters;
    private double recoveryDurationSeconds;
    private bool allowTeams = true;
    private string? trackName;
    private string? trackId;
    private string? trackRevision;
    private string? trackPackageHash;
    private DateTimeOffset? startsAt;
    private DateTimeOffset? startSequenceAt;
    private DateTimeOffset? qualifyingEndsAt;
    private int illuminatedStartLights;
    private bool startLightsOut;
    private bool qualifyingTimeExpired;
    private RaceBannerSnapshot? banner;
    private long revision;

    public RaceCoordinator(
        RaceServerOptions options,
        IRaceStatePersistence? persistence = null,
        Func<string, bool>? playerPasswordMatches = null)
    {
        this.options = options.Normalize();
        this.persistence = persistence ?? NullRaceStatePersistence.Instance;
        sessionName = this.options.SessionName;
        totalRaceLaps = this.options.TotalRaceLaps;
        sectorCount = this.options.SectorCount;
        automaticYellowEnabled = this.options.AutomaticYellowEnabled;
        slowSpeedKph = this.options.SlowSpeedKph;
        slowDurationSeconds = this.options.SlowDurationSeconds;
        severeLateralOffsetMeters = this.options.SevereLateralOffsetMeters;
        recoveryDurationSeconds = this.options.RecoveryDurationSeconds;
        trackName = this.options.TrackName;
        trackId = this.options.TrackId;
        trackRevision = this.options.TrackRevision;
        trackPackageHash = this.options.TrackPackageHash;
        var configuredHash = Hash(this.options.PlayerPassword);
        this.playerPasswordMatches = playerPasswordMatches ?? (password =>
            CryptographicOperations.FixedTimeEquals(configuredHash, Hash(password)));
    }

    public event Action<RaceSessionSnapshot>? SnapshotChanged;

    public RaceServerOptions Options => options;

    public RaceRoomSettingsSnapshot RoomSettings()
    {
        lock (sync)
            return new RaceRoomSettingsSnapshot(
                sessionName,
                totalRaceLaps,
                sectorCount,
                automaticYellowEnabled,
                slowSpeedKph,
                slowDurationSeconds,
                severeLateralOffsetMeters,
                recoveryDurationSeconds,
                allowTeams,
                trackName,
                trackId,
                trackRevision,
                trackPackageHash);
    }

    public RaceSessionSnapshot Snapshot()
    {
        lock (sync)
            return BuildSnapshot(DateTimeOffset.UtcNow);
    }

    public RaceJoinResult TryJoin(RaceLoginRequest request)
    {
        RaceSessionSnapshot? published = null;
        RaceLoginAccepted? accepted = null;
        RaceLoginRejected? rejected = null;
        RaceAuditEntry? audit = null;
        lock (sync)
        {
            if (!playerPasswordMatches(request.Password))
            {
                rejected = new RaceLoginRejected("invalidPassword", "赛事密码不正确。");
            }
            else if (!TrackMatches(request, out var trackError))
            {
                rejected = new RaceLoginRejected("trackMismatch", trackError);
            }
            else if (request.SectorCount is int requestedSectorCount &&
                     Math.Clamp(requestedSectorCount, 1, 20) != sectorCount)
            {
                rejected = new RaceLoginRejected(
                    "sectorMismatch",
                    $"客户端赛道为 {requestedSectorCount} 个分段，房间设置为 {sectorCount} 个分段。");
            }
            else
            {
                string displayName;
                string themeColor;
                string? teamName;
                try
                {
                    displayName = RaceProtocolValidation.NormalizeDisplayName(request.DisplayName);
                    themeColor = RaceProtocolValidation.NormalizeThemeColor(request.ThemeColor);
                    teamName = allowTeams ? RaceProtocolValidation.NormalizeTeamName(request.TeamName) : null;
                }
                catch (ArgumentException exception)
                {
                    rejected = new RaceLoginRejected("invalidProfile", exception.Message);
                    goto Complete;
                }

                var resumed = FindByResumeToken(request.ResumeToken);
                if (resumed is not null)
                {
                    if (HasDuplicateName(displayName, resumed.Id))
                    {
                        rejected = new RaceLoginRejected("duplicateName", "该比赛昵称已被其他车手使用。");
                        goto Complete;
                    }

                    resumed.DisplayName = displayName;
                    resumed.ThemeColor = themeColor;
                    resumed.TeamName = teamName;
                    resumed.IsConnected = true;
                    resumed.LastSeenAt = DateTimeOffset.UtcNow;
                    if (resumed.Status == RaceParticipantStatus.Disconnected)
                        resumed.Status = phase is RaceSessionPhase.Race or RaceSessionPhase.Countdown or
                            RaceSessionPhase.OutLap or RaceSessionPhase.FormationLap
                            ? RaceParticipantStatus.OnTrack
                            : RaceParticipantStatus.Connected;
                    IncrementRevision();
                    published = BuildSnapshot(DateTimeOffset.UtcNow);
                    accepted = new RaceLoginAccepted(resumed.Id, resumed.ResumeToken, published, published.ServerTime);
                    audit = new RaceAuditEntry(published.ServerTime, "participantResumed", $"{displayName} 重新连接。", resumed.Id);
                    goto Complete;
                }

                if (phase is RaceSessionPhase.OutLap or RaceSessionPhase.FormationLap or
                    RaceSessionPhase.Countdown or RaceSessionPhase.Race or
                    RaceSessionPhase.Suspended or RaceSessionPhase.Finished)
                {
                    rejected = new RaceLoginRejected("sessionLocked", "比赛已开始，只允许已有车手重新连接。");
                    goto Complete;
                }
                if (participants.Count >= options.MaximumParticipants)
                {
                    rejected = new RaceLoginRejected("roomFull", $"房间人数已达到 {options.MaximumParticipants} 人上限。");
                    goto Complete;
                }
                if (HasDuplicateName(displayName, null))
                {
                    rejected = new RaceLoginRejected("duplicateName", "该比赛昵称已被使用。");
                    goto Complete;
                }

                var participant = new ParticipantState(
                    Guid.NewGuid(),
                    Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                    displayName,
                    themeColor,
                    teamName,
                    DateTimeOffset.UtcNow);
                participants.Add(participant);
                IncrementRevision();
                published = BuildSnapshot(DateTimeOffset.UtcNow);
                accepted = new RaceLoginAccepted(participant.Id, participant.ResumeToken, published, published.ServerTime);
                audit = new RaceAuditEntry(published.ServerTime, "participantJoined", $"{displayName} 加入赛事。", participant.Id);
            }

        Complete:;
        }

        if (published is not null)
        {
            Publish(published, important: true, audit);
            return RaceJoinResult.Accept(accepted!);
        }
        return RaceJoinResult.Reject(rejected!.Code, rejected.Message);
    }

    public RaceCommandResult SetReady(Guid participantId, bool isReady)
    {
        RaceSessionSnapshot snapshot;
        lock (sync)
        {
            var now = DateTimeOffset.UtcNow;
            var participant = Find(participantId);
            if (participant is null) return RaceCommandResult.Reject("参赛者不存在。");
            if (phase is not (RaceSessionPhase.Lobby or RaceSessionPhase.Qualifying or RaceSessionPhase.Grid))
                return RaceCommandResult.Reject("当前阶段不能修改准备状态。");
            participant.IsReady = isReady;
            participant.Status = isReady ? RaceParticipantStatus.Ready : RaceParticipantStatus.Connected;
            participant.LastSeenAt = now;
            IncrementRevision();
            snapshot = BuildSnapshot(DateTimeOffset.UtcNow);
        }
        Publish(snapshot, important: false);
        return RaceCommandResult.Accepted;
    }

    public RaceCommandResult UpdateTelemetry(
        Guid participantId,
        RaceTelemetryUpdate update,
        DateTimeOffset? receivedAt = null)
    {
        RaceSessionSnapshot snapshot;
        RaceAuditEntry? audit = null;
        lock (sync)
        {
            var now = receivedAt ?? DateTimeOffset.UtcNow;
            var participant = Find(participantId);
            if (participant is null) return RaceCommandResult.Reject("参赛者不存在。");
            if (!participant.IsConnected) return RaceCommandResult.Reject("连接已经失效。");

            var normalized = RaceProtocolValidation.NormalizeTelemetry(update);
            participant.LastSeenAt = now;
            if (!normalized.IsTelemetryValid || normalized.IsPausedOrRewinding)
            {
                participant.TelemetryValid = false;
                participant.HazardCandidateStartedAt = null;
                participant.HazardRecoveryStartedAt = null;
                IncrementRevision();
                snapshot = BuildSnapshot(now);
                goto Complete;
            }

            participant.TelemetryValid = true;
            participant.TrackProgress = normalized.TrackProgress;
            participant.LateralOffsetMeters = normalized.LateralOffsetMeters;
            participant.MapX = normalized.MapX;
            participant.MapY = normalized.MapY;
            participant.SpeedKph = normalized.SpeedKph;
            participant.CurrentSector = Math.Clamp(normalized.CurrentSector, 0, sectorCount - 1);
            participant.CurrentLapSeconds = normalized.CurrentLapSeconds;
            participant.IsInPitLane = normalized.IsInPitLane;
            participant.IsInServiceZone = normalized.IsInServiceZone;
            participant.PitServiceElapsedSeconds = normalized.IsInServiceZone
                ? normalized.PitServiceElapsedSeconds
                : 0;
            participant.PitServiceRequirementMet = normalized.IsInServiceZone &&
                                                    normalized.PitServiceRequirementMet;
            if (participant.PitServiceRequirementMet &&
                normalized.CompletedPitServices == participant.CompletedPitServices + 1)
                participant.CompletedPitServices++;
            participant.GripCondition = normalized.GripCondition;
            participant.Status = normalized.IsInServiceZone
                ? RaceParticipantStatus.InService
                : normalized.IsInPitLane
                    ? RaceParticipantStatus.InPitLane
                    : phase is RaceSessionPhase.Race or RaceSessionPhase.Countdown or RaceSessionPhase.Qualifying or
                        RaceSessionPhase.OutLap or RaceSessionPhase.FormationLap
                        ? RaceParticipantStatus.OnTrack
                        : participant.IsReady ? RaceParticipantStatus.Ready : RaceParticipantStatus.Connected;
            if (EvaluateFalseStart(participant, now) is { } falseStartPenalty)
                audit = new RaceAuditEntry(
                    now,
                    "falseStart",
                    $"{participant.DisplayName} 在红灯熄灭前移动，自动加罚 5 秒。",
                    participant.Id,
                    falseStartPenalty);
            EvaluateAutomaticYellow(participant, now);
            RefreshYellowFlag(now);
            IncrementRevision();
            snapshot = BuildSnapshot(now);
        Complete:;
        }
        Publish(snapshot, important: audit is not null, audit);
        return RaceCommandResult.Accepted;
    }

    public RaceCommandResult CompleteLap(Guid participantId, RaceLapCompleted completed)
    {
        RaceSessionSnapshot snapshot;
        RaceAuditEntry audit;
        lock (sync)
        {
            var participant = Find(participantId);
            if (participant is null) return RaceCommandResult.Reject("参赛者不存在。");
            if (phase is not (RaceSessionPhase.Qualifying or RaceSessionPhase.Race))
                return RaceCommandResult.Reject("当前阶段不接收圈速成绩。");
            if (phase == RaceSessionPhase.Qualifying && qualifyingTimeExpired &&
                !participant.QualifyingFinalLapPending)
                return RaceCommandResult.Reject("排位赛计时已结束，该车手没有待完成的最后一圈。");
            if (!receivedLapEvents.Add(completed.EventId)) return RaceCommandResult.Accepted;
            if (completed.LapSeconds is < 3 or > 21_600 || !double.IsFinite(completed.LapSeconds))
                return RaceCommandResult.Reject("圈速超出允许范围。");

            participant.LastSeenAt = DateTimeOffset.UtcNow;
            var fastestBefore = FastestLap();
            if (completed.IsValid)
            {
                participant.CompletedLaps++;
                participant.LastLapSeconds = completed.LapSeconds;
                participant.CurrentLapSeconds = 0;
                participant.BestLapSeconds = participant.BestLapSeconds is null
                    ? completed.LapSeconds
                    : Math.Min(participant.BestLapSeconds.Value, completed.LapSeconds);
                UpdateBestSectors(participant, completed.SectorSeconds);
            }
            if (phase == RaceSessionPhase.Qualifying && qualifyingTimeExpired)
            {
                participant.QualifyingFinalLapPending = false;
                CompleteQualifyingIfReady();
            }
            var fastestAfter = FastestLap();
            if (completed.IsValid && (fastestBefore is null || fastestAfter?.Time < fastestBefore.Value.Time - 0.0005))
            {
                banner = NewBanner(
                    RaceBannerKind.FastestLap,
                    "本场最快圈",
                    $"{participant.DisplayName}  {completed.LapSeconds:0.000}",
                    participant.Id,
                    TimeSpan.FromSeconds(6));
            }

            if (phase == RaceSessionPhase.Race && completed.IsValid)
            {
                if (flag == RaceControlFlag.Chequered)
                {
                    participant.Status = RaceParticipantStatus.Finished;
                    participant.FinishedAt ??= DateTimeOffset.UtcNow;
                }
                else if (participant.CompletedLaps >= totalRaceLaps &&
                         OrderParticipants().FirstOrDefault()?.Id == participant.Id)
                {
                    participant.Status = RaceParticipantStatus.Finished;
                    participant.FinishedAt ??= DateTimeOffset.UtcNow;
                    flag = RaceControlFlag.Chequered;
                    flagMessage = "领跑者已完成预定圈数";
                    manualFullCourseYellow = null;
                    manualSectorYellows.Clear();
                    banner = NewBanner(
                        RaceBannerKind.Winner,
                        "比赛胜者",
                        participant.DisplayName,
                        participant.Id,
                        null);
                }

                var classified = participants.Where(candidate =>
                    candidate.IsConnected &&
                    candidate.Status is not (RaceParticipantStatus.DidNotFinish or
                        RaceParticipantStatus.Disqualified or RaceParticipantStatus.Disconnected));
                if (flag == RaceControlFlag.Chequered && classified.All(candidate => candidate.Status == RaceParticipantStatus.Finished))
                    phase = RaceSessionPhase.Finished;
            }

            IncrementRevision();
            snapshot = BuildSnapshot(DateTimeOffset.UtcNow);
            audit = new RaceAuditEntry(
                snapshot.ServerTime,
                "lapCompleted",
                completed.IsValid
                    ? $"{participant.DisplayName} 完成有效圈 {completed.LapSeconds:0.000}。"
                    : $"{participant.DisplayName} 完成无效圈：{completed.InvalidReason ?? "未说明"}。",
                participant.Id,
                completed);
        }
        Publish(snapshot, important: true, audit);
        return RaceCommandResult.Accepted;
    }

    public void Disconnect(Guid participantId)
    {
        RaceSessionSnapshot? snapshot = null;
        RaceAuditEntry? audit = null;
        lock (sync)
        {
            var participant = Find(participantId);
            if (participant is null || !participant.IsConnected) return;
            participant.IsConnected = false;
            participant.Status = RaceParticipantStatus.Disconnected;
            participant.AutomaticYellowActive = false;
            participant.HazardCandidateStartedAt = null;
            participant.HazardRecoveryStartedAt = null;
            participant.LastSeenAt = DateTimeOffset.UtcNow;
            participant.QualifyingFinalLapPending = false;
            CompleteQualifyingIfReady();
            RefreshYellowFlag(DateTimeOffset.UtcNow);
            IncrementRevision();
            snapshot = BuildSnapshot(DateTimeOffset.UtcNow);
            audit = new RaceAuditEntry(snapshot.ServerTime, "participantDisconnected", $"{participant.DisplayName} 断开连接。", participant.Id);
        }
        Publish(snapshot, important: true, audit);
    }

    public RaceCommandResult ApplyRoomSettings(RaceAdminRoomSettingsCommand command)
    {
        RaceSessionSnapshot snapshot;
        RaceAuditEntry audit;
        lock (sync)
        {
            var normalizedName = NormalizeReason(command.SessionName, 64);
            if (string.IsNullOrWhiteSpace(normalizedName))
                return RaceCommandResult.Reject("赛事名称不能为空。");
            var normalizedTrackName = NormalizeReason(command.TrackName, 128);
            var normalizedTrackId = NormalizeReason(command.TrackId, 128);
            var normalizedTrackHash = NormalizeReason(command.TrackPackageHash, 128)?.ToUpperInvariant();
            var hasAnyTrackIdentity = normalizedTrackName is not null || normalizedTrackId is not null || normalizedTrackHash is not null;
            if (hasAnyTrackIdentity && (normalizedTrackName is null || normalizedTrackId is null || normalizedTrackHash is null))
                return RaceCommandResult.Reject("配置赛事赛道时，名称、标识和 SHA-256 三项都要填写。");
            if (normalizedTrackId is not null && !Guid.TryParse(normalizedTrackId, out _))
                return RaceCommandResult.Reject("赛道标识不是有效的 UUID。");
            if (normalizedTrackHash is not null &&
                (normalizedTrackHash.Length != 64 || normalizedTrackHash.Any(character => !Uri.IsHexDigit(character))))
                return RaceCommandResult.Reject("赛道 SHA-256 必须是导出提示中的 64 位十六进制摘要。");
            if (phase is RaceSessionPhase.OutLap or RaceSessionPhase.FormationLap or
                RaceSessionPhase.Countdown or RaceSessionPhase.Race or RaceSessionPhase.Suspended)
                return RaceCommandResult.Reject("发车后不能修改房间规则。请先返回大厅。");

            sessionName = normalizedName;
            totalRaceLaps = Math.Clamp(command.TotalRaceLaps, 1, 999);
            sectorCount = Math.Clamp(command.SectorCount, 1, 20);
            automaticYellowEnabled = command.AutomaticYellowEnabled;
            slowSpeedKph = Math.Clamp(command.SlowSpeedKph, 3, 50);
            slowDurationSeconds = Math.Clamp(command.SlowDurationSeconds, 1, 15);
            severeLateralOffsetMeters = Math.Clamp(command.SevereLateralOffsetMeters, 5, 200);
            recoveryDurationSeconds = Math.Clamp(command.RecoveryDurationSeconds, 1, 15);
            allowTeams = command.AllowTeams;
            trackName = normalizedTrackName;
            trackId = normalizedTrackId;
            trackRevision = NormalizeReason(command.TrackRevision, 64);
            trackPackageHash = normalizedTrackHash;
            if (!allowTeams)
                foreach (var participant in participants) participant.TeamName = null;
            if (!automaticYellowEnabled)
            {
                foreach (var participant in participants)
                {
                    participant.AutomaticYellowActive = false;
                    participant.HazardCandidateStartedAt = null;
                    participant.HazardRecoveryStartedAt = null;
                }
                RefreshYellowFlag(DateTimeOffset.UtcNow);
            }
            IncrementRevision();
            snapshot = BuildSnapshot(DateTimeOffset.UtcNow);
            audit = new RaceAuditEntry(
                snapshot.ServerTime,
                "roomSettings",
                $"房间设置已保存：{sessionName}，{totalRaceLaps} 圈，{sectorCount} 个分段。",
                Detail: command);
        }
        Publish(snapshot, important: true, audit);
        return RaceCommandResult.Accepted;
    }

    public RaceCommandResult ApplySessionCommand(RaceAdminSessionCommand command)
    {
        RaceSessionSnapshot snapshot;
        RaceAuditEntry audit;
        lock (sync)
        {
            var now = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(command.SessionName))
                sessionName = command.SessionName.Trim()[..Math.Min(64, command.SessionName.Trim().Length)];
            if (command.TotalRaceLaps is int laps)
                totalRaceLaps = Math.Clamp(laps, 1, 999);

            switch (command.Phase)
            {
                case RaceSessionPhase.Lobby:
                    ResetCompetitiveState(clearParticipants: false);
                    phase = RaceSessionPhase.Lobby;
                    flag = RaceControlFlag.Green;
                    flagMessage = null;
                    break;
                case RaceSessionPhase.Qualifying:
                    ResetCompetitiveState(clearParticipants: false);
                    phase = RaceSessionPhase.Qualifying;
                    flag = RaceControlFlag.Green;
                    qualifyingEndsAt = now.AddMinutes(Math.Clamp(command.QualifyingMinutes ?? 10, 1, 180));
                    qualifyingTimeExpired = false;
                    foreach (var participant in participants)
                    {
                        participant.Status = participant.IsConnected ? RaceParticipantStatus.OnTrack : RaceParticipantStatus.Disconnected;
                        participant.IsReady = false;
                    }
                    banner = NewBanner(RaceBannerKind.Information, "排位赛开始", sessionName, null, TimeSpan.FromSeconds(5));
                    break;
                case RaceSessionPhase.Grid:
                    phase = RaceSessionPhase.Grid;
                    qualifyingEndsAt = null;
                    qualifyingTimeExpired = false;
                    flag = RaceControlFlag.Green;
                    foreach (var participant in participants)
                    {
                        participant.QualifyingFinalLapPending = false;
                        if (participant.IsConnected) participant.Status = RaceParticipantStatus.Ready;
                    }
                    break;
                case RaceSessionPhase.OutLap:
                    PrepareRace();
                    phase = RaceSessionPhase.OutLap;
                    flag = RaceControlFlag.Green;
                    flagMessage = null;
                    banner = NewBanner(
                        RaceBannerKind.Information,
                        "出场圈",
                        "按总控指令驶离维修区并前往发车区",
                        null,
                        TimeSpan.FromSeconds(7));
                    break;
                case RaceSessionPhase.FormationLap:
                    if (phase != RaceSessionPhase.OutLap) PrepareRace();
                    phase = RaceSessionPhase.FormationLap;
                    flag = RaceControlFlag.Green;
                    flagMessage = null;
                    banner = NewBanner(
                        RaceBannerKind.Information,
                        "暖胎圈",
                        "保持队列，返回各自发车位",
                        null,
                        TimeSpan.FromSeconds(7));
                    break;
                case RaceSessionPhase.Countdown:
                    PrepareRace();
                    phase = RaceSessionPhase.Countdown;
                    flag = RaceControlFlag.Green;
                    flagMessage = null;
                    startSequenceAt = now.AddSeconds(Math.Clamp(command.CountdownSeconds ?? 10, 0, 120));
                    var randomHoldMilliseconds = RandomNumberGenerator.GetInt32(1_000, 4_001);
                    startsAt = startSequenceAt.Value.AddSeconds(4).AddMilliseconds(randomHoldMilliseconds);
                    illuminatedStartLights = 0;
                    startLightsOut = false;
                    banner = NewBanner(
                        RaceBannerKind.Information,
                        "准备发车",
                        $"距第一盏红灯亮起还有 {Math.Max(0, (startSequenceAt.Value - now).TotalSeconds):0} 秒",
                        null,
                        startSequenceAt - now);
                    break;
                case RaceSessionPhase.Race:
                    if (phase != RaceSessionPhase.Countdown) PrepareRace();
                    phase = RaceSessionPhase.Race;
                    startsAt = now;
                    startSequenceAt = null;
                    illuminatedStartLights = 0;
                    startLightsOut = true;
                    flag = RaceControlFlag.Green;
                    banner = NewBanner(RaceBannerKind.Information, "比赛开始", sessionName, null, TimeSpan.FromSeconds(4));
                    break;
                case RaceSessionPhase.Finished:
                    return RaceCommandResult.Reject("方格旗由领跑者完成预定圈数后自动触发。");
                default:
                    return RaceCommandResult.Reject("该阶段不能通过常规阶段命令直接设置。");
            }

            IncrementRevision();
            snapshot = BuildSnapshot(now);
            audit = new RaceAuditEntry(now, "sessionPhase", $"赛事阶段切换为 {phase}。", Detail: command);
        }
        Publish(snapshot, important: true, audit);
        return RaceCommandResult.Accepted;
    }

    public RaceCommandResult ApplyFlagCommand(RaceAdminFlagCommand command)
    {
        RaceSessionSnapshot snapshot;
        RaceAuditEntry audit;
        lock (sync)
        {
            var now = DateTimeOffset.UtcNow;
            var message = NormalizeReason(command.Message, 160);
            var requestedSector = command.SectorIndex is int sector
                ? Math.Clamp(sector, 0, sectorCount - 1)
                : (int?)null;
            switch (command.Flag)
            {
                case RaceControlFlag.Green:
                    if (phase == RaceSessionPhase.Suspended)
                    {
                        phase = phaseBeforeSuspension;
                        flag = RaceControlFlag.Green;
                    }
                    if (requestedSector is int greenSector)
                        manualSectorYellows.Remove(greenSector);
                    else
                    {
                        manualFullCourseYellow = null;
                        manualSectorYellows.Clear();
                    }
                    RefreshYellowFlag(now);
                    break;
                case RaceControlFlag.Yellow:
                    if (flag == RaceControlFlag.Red)
                        return RaceCommandResult.Reject("红旗期间不能发布黄旗，请先恢复绿旗。");
                    if (requestedSector is int yellowSector)
                        manualSectorYellows[yellowSector] = message ?? "赛道总控发布分区黄旗";
                    else
                        manualFullCourseYellow = message ?? "赛道总控发布全场黄旗";
                    if (flag != RaceControlFlag.Chequered)
                        RefreshYellowFlag(now);
                    break;
                case RaceControlFlag.Red:
                    if (phase != RaceSessionPhase.Suspended) phaseBeforeSuspension = phase;
                    phase = RaceSessionPhase.Suspended;
                    flag = RaceControlFlag.Red;
                    flagMessage = message ?? "比赛暂停";
                    break;
                case RaceControlFlag.Chequered:
                    return RaceCommandResult.Reject("方格旗按领跑者完成预定圈数的规则自动亮起，不能手动发布。");
            }
            IncrementRevision();
            snapshot = BuildSnapshot(now);
            audit = new RaceAuditEntry(
                now,
                "flag",
                requestedSector is int loggedSector
                    ? $"赛事总控对第 {loggedSector + 1} 分段发布 {command.Flag}。"
                    : $"赛事总控发布全场 {command.Flag}。",
                Detail: command);
        }
        Publish(snapshot, important: true, audit);
        return RaceCommandResult.Accepted;
    }

    public RaceCommandResult ApplyPenalty(RaceAdminPenaltyCommand command)
    {
        RaceSessionSnapshot snapshot;
        RaceAuditEntry audit;
        lock (sync)
        {
            var participant = Find(command.ParticipantId);
            if (participant is null) return RaceCommandResult.Reject("参赛者不存在。");
            var reason = NormalizeReason(command.Reason, 240);
            if (string.IsNullOrWhiteSpace(reason)) return RaceCommandResult.Reject("处罚原因不能为空。");
            var penalty = new RacePenaltySnapshot(
                Guid.NewGuid(),
                participant.Id,
                command.Kind,
                command.Kind is RacePenaltyKind.Time or RacePenaltyKind.StopAndGo
                    ? Math.Clamp(command.ValueSeconds ?? 5, 1, 3600)
                    : null,
                command.Kind == RacePenaltyKind.GridDrop
                    ? Math.Clamp(command.GridPlaces ?? 1, 1, RaceProtocol.MaximumParticipants)
                    : null,
                reason,
                DateTimeOffset.UtcNow,
                false,
                false);
            penalties.Add(penalty);
            if (command.Kind == RacePenaltyKind.Disqualification)
                participant.Status = RaceParticipantStatus.Disqualified;
            banner = NewBanner(
                RaceBannerKind.Penalty,
                $"处罚 · {participant.DisplayName}",
                PenaltyDescription(penalty),
                participant.Id,
                TimeSpan.FromSeconds(8));
            IncrementRevision();
            snapshot = BuildSnapshot(DateTimeOffset.UtcNow);
            audit = new RaceAuditEntry(snapshot.ServerTime, "penalty", $"{participant.DisplayName}：{PenaltyDescription(penalty)}。", participant.Id, penalty);
        }
        Publish(snapshot, important: true, audit);
        return RaceCommandResult.Accepted;
    }

    public RaceCommandResult ApplyParticipantCommand(RaceAdminParticipantCommand command)
    {
        RaceSessionSnapshot snapshot;
        RaceAuditEntry audit;
        lock (sync)
        {
            var participant = Find(command.ParticipantId);
            if (participant is null) return RaceCommandResult.Reject("参赛者不存在。");
            participant.Status = command.Status;
            if (command.Status == RaceParticipantStatus.Finished)
                participant.FinishedAt ??= DateTimeOffset.UtcNow;
            if (command.Status is RaceParticipantStatus.Finished or RaceParticipantStatus.DidNotFinish or
                RaceParticipantStatus.Disqualified or RaceParticipantStatus.Disconnected)
            {
                participant.AutomaticYellowActive = false;
                participant.HazardCandidateStartedAt = null;
                participant.HazardRecoveryStartedAt = null;
                RefreshYellowFlag(DateTimeOffset.UtcNow);
            }
            IncrementRevision();
            snapshot = BuildSnapshot(DateTimeOffset.UtcNow);
            audit = new RaceAuditEntry(
                snapshot.ServerTime,
                "participantStatus",
                $"{participant.DisplayName} 状态改为 {command.Status}：{NormalizeReason(command.Reason, 160) ?? "未填写原因"}。",
                participant.Id,
                command);
        }
        Publish(snapshot, important: true, audit);
        return RaceCommandResult.Accepted;
    }

    public void Tick(DateTimeOffset now)
    {
        RaceSessionSnapshot? snapshot = null;
        RaceAuditEntry? audit = null;
        lock (sync)
        {
            if (phase == RaceSessionPhase.Countdown)
            {
                var nextLights = CalculateIlluminatedStartLights(now);
                if (nextLights > 0 && illuminatedStartLights == 0)
                    ArmFalseStartDetection();
                if (nextLights != illuminatedStartLights)
                {
                    illuminatedStartLights = nextLights;
                    IncrementRevision();
                    snapshot = BuildSnapshot(now);
                }
                if (startsAt is DateTimeOffset scheduled && now >= scheduled)
                {
                    phase = RaceSessionPhase.Race;
                    flag = RaceControlFlag.Green;
                    illuminatedStartLights = 0;
                    startLightsOut = true;
                    banner = NewBanner(RaceBannerKind.Information, "比赛开始", sessionName, null, TimeSpan.FromSeconds(4));
                    IncrementRevision();
                    snapshot = BuildSnapshot(now);
                    audit = new RaceAuditEntry(now, "raceStarted", "五盏红灯熄灭，比赛开始。");
                }
            }
            else if (phase == RaceSessionPhase.Qualifying && !qualifyingTimeExpired &&
                     qualifyingEndsAt is DateTimeOffset ending && now >= ending)
            {
                flag = RaceControlFlag.Chequered;
                qualifyingTimeExpired = true;
                foreach (var participant in participants)
                    participant.QualifyingFinalLapPending = IsEligibleForQualifyingFinalLap(participant);
                var pending = participants.Count(participant => participant.QualifyingFinalLapPending);
                banner = NewBanner(
                    RaceBannerKind.ChequeredFlag,
                    "排位计时结束",
                    pending == 0 ? "成绩已冻结" : $"{pending} 名车手可完成已经开始的最后一圈",
                    null,
                    TimeSpan.FromSeconds(8));
                CompleteQualifyingIfReady();
                IncrementRevision();
                snapshot = BuildSnapshot(now);
                audit = new RaceAuditEntry(
                    now,
                    "qualifyingEnded",
                    pending == 0
                        ? "排位赛计时结束，成绩已冻结。"
                        : $"排位赛计时结束，等待 {pending} 名车手完成最后一圈。");
            }
            else if (banner?.ExpiresAt is DateTimeOffset expiresAt && now >= expiresAt)
            {
                banner = null;
                IncrementRevision();
                snapshot = BuildSnapshot(now);
            }
        }
        if (snapshot is not null) Publish(snapshot, important: audit is not null, audit);
    }

    private int CalculateIlluminatedStartLights(DateTimeOffset now)
    {
        if (phase != RaceSessionPhase.Countdown || startSequenceAt is not DateTimeOffset sequence ||
            now < sequence || startsAt is DateTimeOffset raceStart && now >= raceStart)
            return 0;
        return Math.Clamp((int)Math.Floor((now - sequence).TotalSeconds) + 1, 1, 5);
    }

    private void ArmFalseStartDetection()
    {
        foreach (var participant in participants)
        {
            participant.FalseStartBaselineProgress = participant.TrackProgress;
            participant.FalseStartCandidateStartedAt = null;
            participant.FalseStartPenalized = false;
        }
    }

    private RacePenaltySnapshot? EvaluateFalseStart(ParticipantState participant, DateTimeOffset now)
    {
        if (phase != RaceSessionPhase.Countdown || startSequenceAt is not DateTimeOffset sequence ||
            startsAt is not DateTimeOffset raceStart || now < sequence || now >= raceStart ||
            participant.FalseStartPenalized || participant.IsInPitLane || participant.IsInServiceZone ||
            participant.Status is RaceParticipantStatus.Disqualified or RaceParticipantStatus.Disconnected)
            return null;

        participant.FalseStartBaselineProgress ??= participant.TrackProgress;
        var delta = participant.TrackProgress - participant.FalseStartBaselineProgress.Value;
        if (delta > 0.5) delta -= 1;
        else if (delta < -0.5) delta += 1;
        var forwardProgress = Math.Max(0, delta);
        var movementDetected = participant.SpeedKph >= 5 || forwardProgress >= 0.0008;
        if (!movementDetected)
        {
            participant.FalseStartCandidateStartedAt = null;
            return null;
        }

        participant.FalseStartCandidateStartedAt ??= now;
        if (forwardProgress < 0.002 &&
            now - participant.FalseStartCandidateStartedAt.Value < TimeSpan.FromMilliseconds(250))
            return null;

        participant.FalseStartPenalized = true;
        var penalty = new RacePenaltySnapshot(
            Guid.NewGuid(),
            participant.Id,
            RacePenaltyKind.Time,
            5,
            null,
            "抢跑：五盏红灯熄灭前车辆已经移动",
            now,
            false,
            false);
        penalties.Add(penalty);
        banner = NewBanner(
            RaceBannerKind.Penalty,
            $"抢跑 · {participant.DisplayName}",
            "自动加罚 5 秒",
            participant.Id,
            TimeSpan.FromSeconds(8));
        return penalty;
    }

    private bool IsEligibleForQualifyingFinalLap(ParticipantState participant) =>
        participant.IsConnected &&
        participant.TelemetryValid &&
        !participant.IsInPitLane &&
        !participant.IsInServiceZone &&
        participant.CurrentLapSeconds > 0.05 &&
        participant.Status is not (RaceParticipantStatus.Disqualified or
            RaceParticipantStatus.DidNotFinish or RaceParticipantStatus.Disconnected);

    private void CompleteQualifyingIfReady()
    {
        if (phase != RaceSessionPhase.Qualifying || !qualifyingTimeExpired ||
            participants.Any(participant => participant.QualifyingFinalLapPending))
            return;
        phase = RaceSessionPhase.Grid;
        flag = RaceControlFlag.Green;
        flagMessage = null;
        qualifyingEndsAt = null;
        qualifyingTimeExpired = false;
        foreach (var participant in participants.Where(candidate => candidate.IsConnected))
            participant.Status = RaceParticipantStatus.Ready;
    }

    private void EvaluateAutomaticYellow(ParticipantState participant, DateTimeOffset now)
    {
        if (!automaticYellowEnabled || phase is not (RaceSessionPhase.Race or RaceSessionPhase.Qualifying) ||
            participant.IsInPitLane || participant.IsInServiceZone ||
            participant.Status is RaceParticipantStatus.Finished or RaceParticipantStatus.DidNotFinish or
                RaceParticipantStatus.Disqualified or RaceParticipantStatus.Disconnected)
        {
            participant.AutomaticYellowActive = false;
            participant.HazardCandidateStartedAt = null;
            participant.HazardRecoveryStartedAt = null;
            return;
        }

        var severeOffset = Math.Abs(participant.LateralOffsetMeters) >= severeLateralOffsetMeters;
        var extremelySlow = participant.SpeedKph <= slowSpeedKph;
        var reason = severeOffset
            ? "车辆严重偏离赛道"
            : extremelySlow ? "车辆在赛道上异常低速" : null;
        var required = severeOffset ? 1d : slowDurationSeconds;
        if (reason is not null)
        {
            participant.HazardRecoveryStartedAt = null;
            if (!string.Equals(participant.HazardCandidateReason, reason, StringComparison.Ordinal))
            {
                participant.HazardCandidateReason = reason;
                participant.HazardCandidateStartedAt = now;
            }
            participant.HazardCandidateStartedAt ??= now;
            if (now - participant.HazardCandidateStartedAt.Value >= TimeSpan.FromSeconds(required))
            {
                participant.AutomaticYellowActive = true;
                participant.AutomaticYellowSector = participant.CurrentSector;
                participant.AutomaticYellowReason = reason;
            }
            return;
        }

        participant.HazardCandidateReason = null;
        participant.HazardCandidateStartedAt = null;
        if (!participant.AutomaticYellowActive) return;
        participant.HazardRecoveryStartedAt ??= now;
        if (now - participant.HazardRecoveryStartedAt.Value < TimeSpan.FromSeconds(recoveryDurationSeconds)) return;
        participant.AutomaticYellowActive = false;
        participant.AutomaticYellowReason = null;
        participant.HazardRecoveryStartedAt = null;
    }

    private void RefreshYellowFlag(DateTimeOffset now)
    {
        if (flag is RaceControlFlag.Red or RaceControlFlag.Chequered) return;
        var zones = BuildYellowZones();
        var previous = flag;
        if (zones.Count == 0)
        {
            flag = RaceControlFlag.Green;
            flagMessage = null;
            return;
        }

        flag = RaceControlFlag.Yellow;
        var fullCourse = zones.FirstOrDefault(zone => zone.SectorIndex is null);
        var first = fullCourse ?? zones[0];
        flagMessage = first.SectorIndex is int sector
            ? $"第 {sector + 1} 分段 · {first.Reason}"
            : first.Reason;
    }

    private List<RaceYellowZoneSnapshot> BuildYellowZones()
    {
        var result = new List<RaceYellowZoneSnapshot>();
        if (manualFullCourseYellow is not null)
            result.Add(new RaceYellowZoneSnapshot(null, false, manualFullCourseYellow, null, null));
        result.AddRange(manualSectorYellows
            .OrderBy(pair => pair.Key)
            .Select(pair => new RaceYellowZoneSnapshot(pair.Key, false, pair.Value, null, null)));
        var automatic = participants
            .Where(participant => participant.AutomaticYellowActive)
            .OrderBy(participant => participant.AutomaticYellowSector)
            .ThenBy(participant => participant.JoinedAt)
            .Select(participant => new RaceYellowZoneSnapshot(
                participant.AutomaticYellowSector,
                true,
                participant.AutomaticYellowReason ?? "赛道上存在异常车辆",
                participant.Id,
                participant.DisplayName))
            .ToArray();
        if (automatic.Select(zone => zone.SectorIndex).Distinct().Count() >= 2)
            result.Add(new RaceYellowZoneSnapshot(
                null,
                true,
                "多个分段同时存在异常车辆",
                null,
                null));
        result.AddRange(automatic);
        return result;
    }

    private IReadOnlyList<RaceBlueFlagSnapshot> BuildBlueFlags()
    {
        if (phase != RaceSessionPhase.Race) return [];
        var active = participants.Where(participant =>
            participant.IsConnected && participant.TelemetryValid &&
            !participant.IsInPitLane && !participant.IsInServiceZone &&
            participant.Status is not (RaceParticipantStatus.Finished or
                RaceParticipantStatus.DidNotFinish or RaceParticipantStatus.Disqualified or
                RaceParticipantStatus.Disconnected)).ToArray();
        var result = new List<RaceBlueFlagSnapshot>();
        foreach (var approaching in active)
        foreach (var recipient in active)
        {
            if (approaching.Id == recipient.Id ||
                approaching.CompletedLaps < recipient.CompletedLaps + 1)
                continue;
            var distanceAhead = recipient.TrackProgress - approaching.TrackProgress;
            if (distanceAhead < 0) distanceAhead += 1;
            if (distanceAhead is > 0.003 and <= 0.15)
                result.Add(new RaceBlueFlagSnapshot(recipient.Id, approaching.Id, distanceAhead));
        }
        return result;
    }

    private RaceSessionSnapshot BuildSnapshot(DateTimeOffset now)
    {
        var ordered = OrderParticipants();
        var snapshots = new List<RaceParticipantSnapshot>(ordered.Count);
        double? priorComparable = null;
        double? leaderComparable = null;
        for (var index = 0; index < ordered.Count; index++)
        {
            var participant = ordered[index];
            var comparable = phase == RaceSessionPhase.Qualifying || phase == RaceSessionPhase.Grid
                ? participant.BestLapSeconds
                : null;
            if (index == 0) leaderComparable = comparable;
            var participantPenalties = penalties
                .Where(candidate => candidate.ParticipantId == participant.Id && !candidate.IsRevoked)
                .OrderBy(candidate => candidate.IssuedAt)
                .ToArray();
            snapshots.Add(new RaceParticipantSnapshot(
                participant.Id,
                index + 1,
                participant.DisplayName,
                participant.ThemeColor,
                participant.TeamName,
                participant.Status,
                participant.IsConnected,
                participant.IsReady,
                participant.CompletedLaps,
                participant.CurrentSector,
                participant.TrackProgress,
                participant.MapX,
                participant.MapY,
                participant.SpeedKph,
                participant.CurrentLapSeconds,
                participant.LastLapSeconds,
                participant.BestLapSeconds,
                comparable is not null && leaderComparable is not null ? comparable - leaderComparable : null,
                comparable is not null && priorComparable is not null ? comparable - priorComparable : null,
                participant.IsInPitLane,
                participant.IsInServiceZone,
                participant.PitServiceElapsedSeconds,
                participant.PitServiceRequirementMet,
                participant.CompletedPitServices,
                participant.GripCondition,
                participant.BestSectorSeconds.ToArray(),
                participantPenalties,
                participant.LastSeenAt,
                participant.QualifyingFinalLapPending));
            if (comparable is not null) priorComparable = comparable;
        }

        var fastest = FastestLap();
        return new RaceSessionSnapshot(
            revision,
            sessionName,
            phase,
            flag,
            flagMessage,
            trackId,
            trackRevision,
            trackPackageHash,
            totalRaceLaps,
            startsAt,
            qualifyingEndsAt,
            fastest?.Participant.Id,
            fastest?.Time,
            FastestSectors(),
            banner is null || banner.ExpiresAt is null || banner.ExpiresAt > now ? banner : null,
            snapshots,
            now,
            BuildYellowZones(),
            sectorCount,
            allowTeams,
            trackName,
            BuildBlueFlags(),
            startSequenceAt,
            illuminatedStartLights,
            startLightsOut,
            qualifyingTimeExpired);
    }

    private List<ParticipantState> OrderParticipants()
    {
        if (phase is RaceSessionPhase.Qualifying or RaceSessionPhase.Grid)
            return participants
                .OrderBy(candidate => candidate.BestLapSeconds is null)
                .ThenBy(candidate => candidate.BestLapSeconds)
                .ThenBy(candidate => candidate.JoinedAt)
                .ToList();

        if (phase is RaceSessionPhase.OutLap or RaceSessionPhase.FormationLap or
            RaceSessionPhase.Race or RaceSessionPhase.Countdown or
            RaceSessionPhase.Suspended or RaceSessionPhase.Finished)
            return participants
                .OrderBy(candidate => TerminalRank(candidate.Status))
                .ThenBy(candidate => candidate.Status == RaceParticipantStatus.Finished ? candidate.FinishedAt : null)
                .ThenByDescending(candidate => candidate.CompletedLaps)
                .ThenByDescending(candidate => candidate.TrackProgress)
                .ThenBy(candidate => candidate.JoinedAt)
                .ToList();

        return participants
            .OrderByDescending(candidate => candidate.IsReady)
            .ThenBy(candidate => candidate.JoinedAt)
            .ToList();
    }

    private static int TerminalRank(RaceParticipantStatus status) => status switch
    {
        RaceParticipantStatus.Finished => 0,
        RaceParticipantStatus.DidNotFinish => 2,
        RaceParticipantStatus.Disqualified => 3,
        RaceParticipantStatus.Disconnected => 4,
        _ => 1
    };

    private void PrepareRace()
    {
        ClearYellowState();
        startsAt = null;
        startSequenceAt = null;
        qualifyingEndsAt = null;
        illuminatedStartLights = 0;
        startLightsOut = false;
        qualifyingTimeExpired = false;
        banner = null;
        receivedLapEvents.Clear();
        foreach (var participant in participants)
        {
            participant.CompletedLaps = 0;
            participant.CurrentSector = 0;
            participant.TrackProgress = 0;
            participant.CurrentLapSeconds = 0;
            participant.LastLapSeconds = null;
            participant.BestLapSeconds = null;
            participant.BestSectorSeconds.Clear();
            participant.FinishedAt = null;
            participant.IsInPitLane = false;
            participant.IsInServiceZone = false;
            participant.PitServiceElapsedSeconds = 0;
            participant.PitServiceRequirementMet = false;
            participant.CompletedPitServices = 0;
            participant.QualifyingFinalLapPending = false;
            participant.FalseStartBaselineProgress = null;
            participant.FalseStartCandidateStartedAt = null;
            participant.FalseStartPenalized = false;
            participant.Status = participant.IsConnected ? RaceParticipantStatus.OnTrack : RaceParticipantStatus.Disconnected;
        }
    }

    private void ResetCompetitiveState(bool clearParticipants)
    {
        ClearYellowState();
        penalties.Clear();
        receivedLapEvents.Clear();
        startsAt = null;
        startSequenceAt = null;
        qualifyingEndsAt = null;
        illuminatedStartLights = 0;
        startLightsOut = false;
        qualifyingTimeExpired = false;
        banner = null;
        if (clearParticipants)
        {
            participants.Clear();
            return;
        }
        foreach (var participant in participants)
        {
            participant.IsReady = false;
            participant.CompletedLaps = 0;
            participant.CurrentSector = 0;
            participant.TrackProgress = 0;
            participant.CurrentLapSeconds = 0;
            participant.LastLapSeconds = null;
            participant.BestLapSeconds = null;
            participant.BestSectorSeconds.Clear();
            participant.FinishedAt = null;
            participant.IsInPitLane = false;
            participant.IsInServiceZone = false;
            participant.PitServiceElapsedSeconds = 0;
            participant.PitServiceRequirementMet = false;
            participant.CompletedPitServices = 0;
            participant.AutomaticYellowActive = false;
            participant.HazardCandidateStartedAt = null;
            participant.HazardRecoveryStartedAt = null;
            participant.QualifyingFinalLapPending = false;
            participant.FalseStartBaselineProgress = null;
            participant.FalseStartCandidateStartedAt = null;
            participant.FalseStartPenalized = false;
            participant.Status = participant.IsConnected ? RaceParticipantStatus.Connected : RaceParticipantStatus.Disconnected;
        }
    }

    private void ClearYellowState()
    {
        manualFullCourseYellow = null;
        manualSectorYellows.Clear();
        foreach (var participant in participants)
        {
            participant.AutomaticYellowActive = false;
            participant.HazardCandidateStartedAt = null;
            participant.HazardRecoveryStartedAt = null;
        }
    }

    private (ParticipantState Participant, double Time)? FastestLap()
    {
        var fastest = participants
            .Where(candidate => candidate.BestLapSeconds is not null)
            .OrderBy(candidate => candidate.BestLapSeconds)
            .ThenBy(candidate => candidate.JoinedAt)
            .FirstOrDefault();
        return fastest?.BestLapSeconds is double time ? (fastest, time) : null;
    }

    private IReadOnlyList<double?> FastestSectors()
    {
        var count = participants.Count == 0
            ? 0
            : participants.Max(candidate => candidate.BestSectorSeconds.Count);
        var result = new double?[count];
        for (var index = 0; index < count; index++)
        {
            var candidates = participants
                .Where(candidate => candidate.BestSectorSeconds.Count > index)
                .Select(candidate => candidate.BestSectorSeconds[index])
                .Where(value => value is > 0)
                .Select(value => value!.Value)
                .ToArray();
            result[index] = candidates.Length == 0 ? null : candidates.Min();
        }
        return result;
    }

    private static void UpdateBestSectors(
        ParticipantState participant,
        IReadOnlyList<double> sectors)
    {
        for (var index = 0; index < Math.Min(20, sectors.Count); index++)
        {
            var value = sectors[index];
            if (!double.IsFinite(value) || value <= 0 || value > 7_200) continue;
            while (participant.BestSectorSeconds.Count <= index)
                participant.BestSectorSeconds.Add(null);
            participant.BestSectorSeconds[index] =
                participant.BestSectorSeconds[index] is double current
                    ? Math.Min(current, value)
                    : value;
        }
    }

    private bool TrackMatches(RaceLoginRequest request, out string error)
    {
        if (trackId is not null && !string.Equals(trackId, request.TrackId, StringComparison.OrdinalIgnoreCase))
        {
            error = "客户端选择的地产赛道与服务端赛事赛道不一致。";
            return false;
        }
        if (trackRevision is not null && !string.Equals(trackRevision, request.TrackRevision, StringComparison.Ordinal))
        {
            error = "地产赛道修订版本不一致。";
            return false;
        }
        if (trackPackageHash is not null && !string.Equals(trackPackageHash, request.TrackPackageHash, StringComparison.OrdinalIgnoreCase))
        {
            error = "地产赛道文件摘要不一致。";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private ParticipantState? Find(Guid participantId) => participants.FirstOrDefault(candidate => candidate.Id == participantId);

    private ParticipantState? FindByResumeToken(string? resumeToken) =>
        string.IsNullOrWhiteSpace(resumeToken)
            ? null
            : participants.FirstOrDefault(candidate => ConstantTimeEquals(candidate.ResumeToken, resumeToken));

    private bool HasDuplicateName(string displayName, Guid? exceptParticipantId) => participants.Any(candidate =>
        candidate.Id != exceptParticipantId &&
        string.Equals(candidate.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static bool ConstantTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Hash(left), Hash(right));

    private void IncrementRevision() => revision++;

    private void Publish(RaceSessionSnapshot snapshot, bool important, RaceAuditEntry? audit = null)
    {
        if (important) persistence.SaveImportantSnapshot(snapshot);
        if (audit is not null) persistence.AppendAudit(audit);
        SnapshotChanged?.Invoke(snapshot);
    }

    private static string? NormalizeReason(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = new string(value.Trim()
            .Where(character => !char.IsControl(character) || character == ' ')
            .Take(maximumLength)
            .ToArray());
        return normalized.Length == 0 ? null : normalized;
    }

    private static RaceBannerSnapshot NewBanner(
        RaceBannerKind kind,
        string title,
        string? detail,
        Guid? participantId,
        TimeSpan? duration)
    {
        var now = DateTimeOffset.UtcNow;
        return new RaceBannerSnapshot(Guid.NewGuid(), kind, title, detail, participantId, now, duration is null ? null : now + duration);
    }

    private static string PenaltyDescription(RacePenaltySnapshot penalty) => penalty.Kind switch
    {
        RacePenaltyKind.Warning => $"警告 · {penalty.Reason}",
        RacePenaltyKind.Time => $"加罚 {penalty.ValueSeconds:0.#} 秒 · {penalty.Reason}",
        RacePenaltyKind.DriveThrough => $"通过维修区处罚 · {penalty.Reason}",
        RacePenaltyKind.StopAndGo => $"停车 {penalty.ValueSeconds:0.#} 秒 · {penalty.Reason}",
        RacePenaltyKind.GridDrop => $"退后 {penalty.GridPlaces} 个发车位 · {penalty.Reason}",
        _ => $"取消比赛资格 · {penalty.Reason}"
    };

    private sealed class ParticipantState(
        Guid id,
        string resumeToken,
        string displayName,
        string themeColor,
        string? teamName,
        DateTimeOffset joinedAt)
    {
        public Guid Id { get; } = id;
        public string ResumeToken { get; } = resumeToken;
        public string DisplayName { get; set; } = displayName;
        public string ThemeColor { get; set; } = themeColor;
        public string? TeamName { get; set; } = teamName;
        public DateTimeOffset JoinedAt { get; } = joinedAt;
        public DateTimeOffset LastSeenAt { get; set; } = joinedAt;
        public RaceParticipantStatus Status { get; set; } = RaceParticipantStatus.Connected;
        public bool IsConnected { get; set; } = true;
        public bool IsReady { get; set; }
        public bool TelemetryValid { get; set; }
        public int CompletedLaps { get; set; }
        public int CurrentSector { get; set; }
        public double TrackProgress { get; set; }
        public double LateralOffsetMeters { get; set; }
        public double MapX { get; set; }
        public double MapY { get; set; }
        public double SpeedKph { get; set; }
        public double CurrentLapSeconds { get; set; }
        public double? LastLapSeconds { get; set; }
        public double? BestLapSeconds { get; set; }
        public List<double?> BestSectorSeconds { get; } = [];
        public bool IsInPitLane { get; set; }
        public bool IsInServiceZone { get; set; }
        public double PitServiceElapsedSeconds { get; set; }
        public bool PitServiceRequirementMet { get; set; }
        public int CompletedPitServices { get; set; }
        public RaceGripCondition GripCondition { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public string? HazardCandidateReason { get; set; }
        public DateTimeOffset? HazardCandidateStartedAt { get; set; }
        public DateTimeOffset? HazardRecoveryStartedAt { get; set; }
        public bool AutomaticYellowActive { get; set; }
        public int AutomaticYellowSector { get; set; }
        public string? AutomaticYellowReason { get; set; }
        public bool QualifyingFinalLapPending { get; set; }
        public double? FalseStartBaselineProgress { get; set; }
        public DateTimeOffset? FalseStartCandidateStartedAt { get; set; }
        public bool FalseStartPenalized { get; set; }
    }
}
