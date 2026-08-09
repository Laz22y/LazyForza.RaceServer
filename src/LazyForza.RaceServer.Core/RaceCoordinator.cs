using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
    private readonly List<RaceEventSnapshot> events = [];
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
    private TrackLimitEnforcementMode trackLimitMode;
    private bool allowTeams = true;
    private int driversPerTeam;
    private IReadOnlyList<RaceTeamDefinition> teams = [];
    private bool chequeredImminent;
    private string? trackName;
    private string? trackId;
    private string? trackRevision;
    private string? trackPackageHash;
    private DateTimeOffset? startsAt;
    private DateTimeOffset? startSequenceAt;
    private DateTimeOffset? raceSuspendedAt;
    private TimeSpan raceSuspendedDuration;
    private DateTimeOffset? raceEndedAt;
    private DateTimeOffset? qualifyingEndsAt;
    private int illuminatedStartLights;
    private bool startLightsOut;
    private bool qualifyingTimeExpired;
    private RaceBannerSnapshot? banner;
    private long revision;
    private long eventSequence;

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
        trackLimitMode = this.options.TrackLimitMode;
        allowTeams = this.options.AllowTeams;
        driversPerTeam = this.options.DriversPerTeam;
        teams = NormalizeTeams(this.options.TeamCount, this.options.Teams);
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
                trackPackageHash,
                teams.Count,
                driversPerTeam,
                teams,
                trackLimitMode);
    }

    public IReadOnlyList<RaceEventSnapshot> Events(int limit = 200)
    {
        lock (sync)
            return events
                .TakeLast(Math.Clamp(limit, 20, 500))
                .Reverse()
                .ToArray();
    }

    public RaceSessionSnapshot Snapshot(DateTimeOffset? observedAt = null)
    {
        lock (sync)
            return BuildSnapshot(observedAt ?? DateTimeOffset.UtcNow);
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
                string? teamId;
                string? teamColor;
                var resumed = FindByResumeToken(request.ResumeToken);
                try
                {
                    displayName = RaceProtocolValidation.NormalizeDisplayName(request.DisplayName);
                    themeColor = RaceProtocolValidation.NormalizeThemeColor(request.ThemeColor);
                    var selectedTeam = allowTeams ? ResolveTeam(request.TeamId, request.TeamName) : null;
                    if (allowTeams && selectedTeam is null && IsLegacyTeamClient(request.ClientVersion))
                        selectedTeam = SelectLegacyTeam(resumed?.Id);
                    if (allowTeams && selectedTeam is null)
                    {
                        rejected = new RaceLoginRejected("teamRequired", "请选择服务端已经配置的车队。");
                        goto Complete;
                    }
                    teamName = selectedTeam?.Name;
                    teamId = selectedTeam?.Id;
                    teamColor = selectedTeam?.ThemeColor;
                }
                catch (ArgumentException exception)
                {
                    rejected = new RaceLoginRejected("invalidProfile", exception.Message);
                    goto Complete;
                }

                if (resumed is not null)
                {
                    if (HasDuplicateName(displayName, resumed.Id))
                    {
                        rejected = new RaceLoginRejected("duplicateName", "该比赛昵称已被其他车手使用。");
                        goto Complete;
                    }
                    if (teamId is not null && !TeamHasCapacity(teamId, resumed.Id))
                    {
                        rejected = new RaceLoginRejected("teamFull", $"{teamName} 已达到每队 {driversPerTeam} 人上限。");
                        goto Complete;
                    }

                    resumed.DisplayName = displayName;
                    resumed.ThemeColor = themeColor;
                    resumed.TeamName = teamName;
                    resumed.TeamId = teamId;
                    resumed.TeamColor = teamColor;
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
                if (teamId is not null && !TeamHasCapacity(teamId))
                {
                    rejected = new RaceLoginRejected("teamFull", $"{teamName} 已达到每队 {driversPerTeam} 人上限。");
                    goto Complete;
                }

                var participant = new ParticipantState(
                    Guid.NewGuid(),
                    Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                    displayName,
                    themeColor,
                    teamName,
                    DateTimeOffset.UtcNow,
                    teamId,
                    teamColor);
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
        var audits = new List<RaceAuditEntry>();
        lock (sync)
        {
            var now = receivedAt ?? DateTimeOffset.UtcNow;
            var participant = Find(participantId);
            if (participant is null) return RaceCommandResult.Reject("参赛者不存在。");
            if (!participant.IsConnected) return RaceCommandResult.Reject("连接已经失效。");

            var normalized = RaceProtocolValidation.NormalizeTelemetry(update);
            participant.LastSeenAt = now;
            var wasInPitLane = participant.IsInPitLane;
            var wasInServiceZone = participant.IsInServiceZone;
            var completedPitServicesBefore = participant.CompletedPitServices;
            UpdatePenaltyServiceState(participant, normalized, now, audits);
            UpdatePitServiceState(participant, normalized);
            if (!wasInPitLane && participant.IsInPitLane)
                audits.Add(new RaceAuditEntry(now, "pitEntered", $"{participant.DisplayName} 进入维修区。", participant.Id));
            if (!wasInServiceZone && participant.IsInServiceZone)
                audits.Add(new RaceAuditEntry(now, "pitBoxEntered", $"{participant.DisplayName} 停入换胎区。", participant.Id));
            if (completedPitServicesBefore < participant.CompletedPitServices)
                audits.Add(new RaceAuditEntry(now, "pitServiceCompleted", $"{participant.DisplayName} 完成换胎停留。", participant.Id));
            if (wasInPitLane && !participant.IsInPitLane)
                audits.Add(new RaceAuditEntry(now, "pitExited", $"{participant.DisplayName} 离开维修区。", participant.Id));
            if (!normalized.IsTelemetryValid || normalized.IsPausedOrRewinding)
            {
                participant.TelemetryValid = false;
                participant.ProgressContinuityReady = false;
                IncrementRevision();
                snapshot = BuildSnapshot(now);
                goto Complete;
            }

            var shortcutPenalty = EvaluateShortcut(participant, normalized, now);
            participant.TelemetryValid = true;
            participant.TrackProgress = normalized.TrackProgress;
            participant.LateralOffsetMeters = normalized.LateralOffsetMeters;
            participant.MapX = normalized.MapX;
            participant.MapY = normalized.MapY;
            participant.SpeedKph = normalized.SpeedKph;
            participant.CurrentSector = Math.Clamp(normalized.CurrentSector, 0, sectorCount - 1);
            participant.CurrentLapSeconds = normalized.CurrentLapSeconds;
            participant.TrackToleranceMeters = normalized.TrackToleranceMeters;
            participant.GripCondition = normalized.GripCondition;
            if (participant.Status is not (RaceParticipantStatus.Finished or RaceParticipantStatus.DidNotFinish or
                    RaceParticipantStatus.Disqualified or RaceParticipantStatus.Disconnected))
                participant.Status = normalized.IsInServiceZone
                    ? RaceParticipantStatus.InService
                    : normalized.IsInPitLane
                        ? RaceParticipantStatus.InPitLane
                        : phase is RaceSessionPhase.Race or RaceSessionPhase.Countdown or RaceSessionPhase.Qualifying or
                            RaceSessionPhase.OutLap or RaceSessionPhase.FormationLap
                            ? RaceParticipantStatus.OnTrack
                            : participant.IsReady ? RaceParticipantStatus.Ready : RaceParticipantStatus.Connected;
            if (EvaluateFalseStart(participant, now) is { } falseStartPenalty)
                audits.Add(new RaceAuditEntry(
                    now,
                    "falseStart",
                    $"{participant.DisplayName} 在红灯熄灭前移动，自动加罚 5 秒。",
                    participant.Id,
                    falseStartPenalty));
            if (EvaluateTrackLimits(participant, normalized, now) is { } trackLimitPenalty)
                audits.Add(new RaceAuditEntry(
                    now,
                    "automaticTrackLimitPenalty",
                    $"{participant.DisplayName}：{PenaltyDescription(trackLimitPenalty)}。",
                    participant.Id,
                    trackLimitPenalty));
            if (shortcutPenalty is { } detectedShortcutPenalty)
                audits.Add(new RaceAuditEntry(
                    now,
                    "automaticShortcutPenalty",
                    $"{participant.DisplayName}：{PenaltyDescription(detectedShortcutPenalty)}。",
                    participant.Id,
                    detectedShortcutPenalty));
            if (EvaluatePitSpeeding(participant, normalized, now) is { } pitSpeedPenalty)
                audits.Add(new RaceAuditEntry(
                    now,
                    "automaticPitSpeedPenalty",
                    $"{participant.DisplayName}：{PenaltyDescription(pitSpeedPenalty)}。",
                    participant.Id,
                    pitSpeedPenalty));
            var automaticYellowBefore = participant.AutomaticYellowActive;
            EvaluateAutomaticYellow(participant, now);
            if (!automaticYellowBefore && participant.AutomaticYellowActive)
                audits.Add(new RaceAuditEntry(
                    now,
                    "automaticYellow",
                    $"{participant.DisplayName} 触发第 {participant.AutomaticYellowSector + 1} 分段自动黄旗：{participant.AutomaticYellowReason}。",
                    participant.Id));
            else if (automaticYellowBefore && !participant.AutomaticYellowActive)
                audits.Add(new RaceAuditEntry(now, "automaticGreen", $"{participant.DisplayName} 的异常状态已解除。", participant.Id));
            RefreshYellowFlag(now);
            RefreshChequeredImminent(now);
            IncrementRevision();
            snapshot = BuildSnapshot(now);
        Complete:;
        }
        Publish(snapshot, important: audits.Count > 0, audits);
        return RaceCommandResult.Accepted;
    }

    public RaceCommandResult CompleteLap(
        Guid participantId,
        RaceLapCompleted completed,
        DateTimeOffset? receivedAt = null)
    {
        RaceSessionSnapshot snapshot;
        var audits = new List<RaceAuditEntry>();
        lock (sync)
        {
            var now = receivedAt ?? DateTimeOffset.UtcNow;
            var participant = Find(participantId);
            if (participant is null) return RaceCommandResult.Reject("参赛者不存在。");
            if (phase is not (RaceSessionPhase.Qualifying or RaceSessionPhase.Race))
                return RaceCommandResult.Reject("当前阶段不接收圈速成绩。");
            if (phase == RaceSessionPhase.Qualifying && qualifyingTimeExpired &&
                !participant.QualifyingFinalLapPending)
                return RaceCommandResult.Reject("排位赛计时已结束，该车手没有待完成的最后一圈。");
            if (!receivedLapEvents.Add(completed.EventId)) return RaceCommandResult.Accepted;
            if (completed.IsValid &&
                (completed.LapSeconds is < 3 or > 21_600 || !double.IsFinite(completed.LapSeconds)))
                return RaceCommandResult.Reject("圈速超出允许范围。");

            participant.LastSeenAt = now;
            var fastestBefore = FastestLap();
            var bestLapEligible = completed.IsValid &&
                                  completed.IsBestLapEligible &&
                                  !participant.LapHasTrackLimitIncident;
            var improvesPersonalBest = bestLapEligible &&
                                       (participant.BestLapSeconds is null ||
                                        completed.LapSeconds < participant.BestLapSeconds.Value - 0.0005);
            if (completed.IsValid)
            {
                participant.CompletedLaps++;
                participant.LastLapSeconds = completed.LapSeconds;
                participant.LastLapCompletedAt = now;
                participant.CurrentLapSeconds = 0;
                participant.ShortcutPenaltyIssued = false;
                participant.ProgressContinuityReady = false;
                if (bestLapEligible)
                {
                    if (improvesPersonalBest)
                    {
                        participant.BestLapSeconds = completed.LapSeconds;
                        ReplaceBestLapSectors(participant, completed.SectorSeconds);
                    }
                    UpdateBestSectors(participant, completed.SectorSeconds);
                }
            }
            participant.LapHasTrackLimitIncident = false;
            if (phase == RaceSessionPhase.Qualifying && qualifyingTimeExpired)
            {
                participant.QualifyingFinalLapPending = false;
                CompleteQualifyingIfReady();
            }
            var fastestAfter = FastestLap();
            if (bestLapEligible && (fastestBefore is null || fastestAfter?.Time < fastestBefore.Value.Time - 0.0005))
            {
                banner = NewBanner(
                    RaceBannerKind.FastestLap,
                    "本场最快圈",
                    $"{participant.DisplayName}  {FormatLapTime(completed.LapSeconds)}",
                    participant.Id,
                    TimeSpan.FromSeconds(6));
            }

            if (phase == RaceSessionPhase.Race)
            {
                UpdateDriveThroughDeadline(
                    participant,
                    now,
                    completed.IsValid &&
                    (participant.CompletedLaps >= totalRaceLaps || flag == RaceControlFlag.Chequered),
                    audits);
            }
            if (phase == RaceSessionPhase.Race && completed.IsValid)
            {
                if (flag == RaceControlFlag.Chequered)
                {
                    participant.Status = RaceParticipantStatus.Finished;
                    participant.FinishedAt ??= now;
                    participant.RaceTotalSeconds ??= RaceElapsedSeconds(now);
                }
                else if (participant.CompletedLaps >= totalRaceLaps)
                {
                    participant.Status = RaceParticipantStatus.Finished;
                    participant.FinishedAt ??= now;
                    participant.RaceTotalSeconds ??= RaceElapsedSeconds(now);
                    flag = RaceControlFlag.Chequered;
                    chequeredImminent = false;
                    flagMessage = "领跑者已完成预定圈数";
                    manualFullCourseYellow = null;
                    manualSectorYellows.Clear();
                    banner = NewBanner(
                        RaceBannerKind.ChequeredFlag,
                        "方格旗",
                        $"{participant.DisplayName} 率先完成 {totalRaceLaps} 圈",
                        participant.Id,
                        TimeSpan.FromSeconds(8));
                }

                var classified = participants.Where(candidate =>
                    candidate.IsConnected &&
                    candidate.Status is not (RaceParticipantStatus.DidNotFinish or
                        RaceParticipantStatus.Disqualified or RaceParticipantStatus.Disconnected));
                if (flag == RaceControlFlag.Chequered && classified.All(candidate => candidate.Status == RaceParticipantStatus.Finished))
                {
                    phase = RaceSessionPhase.Finished;
                    raceEndedAt = now;
                    var winner = OrderParticipants(now).FirstOrDefault(candidate =>
                        candidate.Status == RaceParticipantStatus.Finished);
                    if (winner is not null)
                        banner = NewBanner(
                            RaceBannerKind.Winner,
                            "比赛胜者",
                            $"{winner.DisplayName}  {FormatRaceTime(AdjustedRaceTotalSeconds(winner, now))}",
                            winner.Id,
                            null);
                }
            }

            IncrementRevision();
            snapshot = BuildSnapshot(now);
            audits.Add(new RaceAuditEntry(
                snapshot.ServerTime,
                "lapCompleted",
                completed.IsValid
                    ? bestLapEligible
                        ? $"{participant.DisplayName} 完成有效圈 {FormatLapTime(completed.LapSeconds)}。"
                        : $"{participant.DisplayName} 完成计圈 {FormatLapTime(completed.LapSeconds)}，因赛道边界事件不计入最快圈。"
                    : $"{participant.DisplayName} 完成无效圈：{completed.InvalidReason ?? "未说明"}。",
                participant.Id,
                completed));
        }
        Publish(snapshot, important: true, audits);
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
            var requestedTeamCount = Math.Clamp(command.TeamCount, 1, RaceProtocol.MaximumParticipants);
            if (command.AllowTeams)
            {
                if (command.Teams is null || command.Teams.Count != requestedTeamCount)
                    return RaceCommandResult.Reject($"已开启车队，请完整配置 {requestedTeamCount} 支车队的名称和代表色。");
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var team in command.Teams)
                {
                    var teamId = NormalizeReason(team.Id, 40);
                    var teamName = RaceProtocolValidation.NormalizeTeamName(team.Name);
                    if (teamId is null || teamName is null)
                        return RaceCommandResult.Reject("每支车队都需要有效的名称和标识。");
                    if (!ids.Add(teamId) || !names.Add(teamName))
                        return RaceCommandResult.Reject("车队名称和标识不能重复。");
                    try
                    {
                        RaceProtocolValidation.NormalizeThemeColor(team.ThemeColor);
                    }
                    catch (ArgumentException)
                    {
                        return RaceCommandResult.Reject($"{teamName} 的代表色不是有效的 #RRGGBB 颜色。");
                    }
                }
            }
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
            trackLimitMode = Enum.IsDefined(command.TrackLimitMode)
                ? command.TrackLimitMode
                : TrackLimitEnforcementMode.WarningsOnly;
            allowTeams = command.AllowTeams;
            driversPerTeam = Math.Clamp(command.DriversPerTeam, 1, RaceProtocol.MaximumParticipants);
            teams = NormalizeTeams(requestedTeamCount, command.Teams);
            trackName = normalizedTrackName;
            trackId = normalizedTrackId;
            trackRevision = NormalizeReason(command.TrackRevision, 64);
            trackPackageHash = normalizedTrackHash;
            foreach (var participant in participants)
            {
                var selectedTeam = allowTeams ? ResolveTeam(participant.TeamId, participant.TeamName) : null;
                participant.TeamId = selectedTeam?.Id;
                participant.TeamName = selectedTeam?.Name;
                participant.TeamColor = selectedTeam?.ThemeColor;
            }
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
                $"房间设置已保存：{sessionName}，{totalRaceLaps} 圈，{sectorCount} 个分段，赛道边界模式 {trackLimitMode}。",
                Detail: command);
        }
        Publish(snapshot, important: true, audit);
        return RaceCommandResult.Accepted;
    }

    public RaceCommandResult ApplySessionCommand(
        RaceAdminSessionCommand command,
        DateTimeOffset? invokedAt = null)
    {
        RaceSessionSnapshot snapshot;
        RaceAuditEntry audit;
        lock (sync)
        {
            var now = invokedAt ?? DateTimeOffset.UtcNow;
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
                    raceSuspendedAt = null;
                    raceSuspendedDuration = TimeSpan.Zero;
                    raceEndedAt = null;
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

    public RaceCommandResult ApplyFlagCommand(
        RaceAdminFlagCommand command,
        DateTimeOffset? invokedAt = null)
    {
        RaceSessionSnapshot snapshot;
        RaceAuditEntry audit;
        lock (sync)
        {
            var now = invokedAt ?? DateTimeOffset.UtcNow;
            var message = NormalizeReason(command.Message, 160);
            var requestedSector = command.SectorIndex is int sector
                ? Math.Clamp(sector, 0, sectorCount - 1)
                : (int?)null;
            switch (command.Flag)
            {
                case RaceControlFlag.Green:
                    if (phase == RaceSessionPhase.Suspended)
                    {
                        if (phaseBeforeSuspension == RaceSessionPhase.Race && raceSuspendedAt is DateTimeOffset suspendedAt)
                            raceSuspendedDuration += now - suspendedAt;
                        raceSuspendedAt = null;
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
                    if (phase == RaceSessionPhase.Race && raceSuspendedAt is null)
                        raceSuspendedAt = now;
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
            var now = DateTimeOffset.UtcNow;
            var participant = Find(command.ParticipantId);
            if (participant is null) return RaceCommandResult.Reject("参赛者不存在。");
            var reason = NormalizeReason(command.Reason, 240);
            if (string.IsNullOrWhiteSpace(reason)) return RaceCommandResult.Reject("处罚原因不能为空。");
            var penalty = command.Kind == RacePenaltyKind.DriveThrough
                ? CreateDriveThroughPenalty(participant, reason, now)
                : new RacePenaltySnapshot(
                    Guid.NewGuid(),
                    participant.Id,
                    command.Kind,
                    command.Kind switch
                    {
                        RacePenaltyKind.Time => Math.Clamp(Math.Round(command.ValueSeconds ?? 5), 1, 6),
                        RacePenaltyKind.StopAndGo => Math.Clamp(command.ValueSeconds ?? 5, 1, 3600),
                        _ => null
                    },
                    command.Kind == RacePenaltyKind.GridDrop
                        ? Math.Clamp(command.GridPlaces ?? 1, 1, RaceProtocol.MaximumParticipants)
                        : null,
                    reason,
                    now,
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
            snapshot = BuildSnapshot(now);
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
            var now = DateTimeOffset.UtcNow;
            var participant = Find(command.ParticipantId);
            if (participant is null) return RaceCommandResult.Reject("参赛者不存在。");
            participant.Status = command.Status;
            if (command.Status == RaceParticipantStatus.Finished)
            {
                participant.FinishedAt ??= now;
                participant.RaceTotalSeconds ??= RaceElapsedSeconds(now);
            }
            if (command.Status is RaceParticipantStatus.Finished or RaceParticipantStatus.DidNotFinish or
                RaceParticipantStatus.Disqualified or RaceParticipantStatus.Disconnected)
            {
                participant.AutomaticYellowActive = false;
                participant.HazardCandidateStartedAt = null;
                participant.HazardRecoveryStartedAt = null;
                ResetTrackLimitExcursion(participant);
                RefreshYellowFlag(now);
            }
            IncrementRevision();
            snapshot = BuildSnapshot(now);
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
            "已记录 5 秒待执行罚时",
            participant.Id,
            TimeSpan.FromSeconds(8));
        return penalty;
    }

    private void UpdatePenaltyServiceState(
        ParticipantState participant,
        RaceTelemetryUpdate telemetry,
        DateTimeOffset now,
        ICollection<RaceAuditEntry> audits)
    {
        var enteredPit = !participant.IsInPitLane && telemetry.IsInPitLane;
        var exitedPit = participant.IsInPitLane && !telemetry.IsInPitLane;
        var leftServiceZone = participant.IsInServiceZone && !telemetry.IsInServiceZone;
        if (enteredPit)
        {
            participant.PitVisitHadServiceStop = false;
            participant.PitVisitPaused = false;
            participant.DriveThroughVisitActive = HasPendingDriveThrough(participant.Id);
            participant.DriveThroughStopCandidateStartedAt = null;
        }
        if (telemetry.IsInPitLane && telemetry.IsPausedOrRewinding)
            participant.PitVisitPaused = true;
        if (participant.DriveThroughVisitActive && telemetry.IsInPitLane)
        {
            if (telemetry.SpeedKph <= 1)
            {
                participant.DriveThroughStopCandidateStartedAt ??= now;
                if (now - participant.DriveThroughStopCandidateStartedAt.Value >= TimeSpan.FromSeconds(1))
                    participant.PitVisitHadServiceStop = true;
            }
            else
            {
                participant.DriveThroughStopCandidateStartedAt = null;
            }
        }

        var pendingTime = PendingTimePenaltySeconds(participant.Id);
        if (pendingTime > 0 && telemetry.IsInServiceZone)
        {
            if (telemetry.IsPausedOrRewinding || !telemetry.IsTelemetryValid)
            {
                if (participant.PenaltyServiceActive || participant.PenaltyServiceAttempted)
                    ConvertTimePenaltyToDriveThrough(participant, now, "执行罚时期间打开暂停菜单或使用回转", audits);
            }
            else if (telemetry.SpeedKph <= 1)
            {
                if (!participant.PenaltyServiceActive)
                {
                    participant.PenaltyServiceActive = true;
                    participant.PenaltyServiceAttempted = true;
                    participant.PenaltyServiceElapsedSeconds = 0;
                    participant.PenaltyServiceRequiredSeconds = pendingTime;
                    participant.PenaltyServiceLastUpdatedAt = now;
                    audits.Add(new RaceAuditEntry(
                        now,
                        "penaltyServiceStarted",
                        $"{participant.DisplayName} 开始执行 {pendingTime:0.#} 秒停车罚时。",
                        participant.Id));
                }
                else if (participant.PenaltyServiceLastUpdatedAt is DateTimeOffset previous && now > previous)
                {
                    participant.PenaltyServiceElapsedSeconds = Math.Min(
                        participant.PenaltyServiceRequiredSeconds,
                        participant.PenaltyServiceElapsedSeconds + (now - previous).TotalSeconds);
                    participant.PenaltyServiceLastUpdatedAt = now;
                }

                if (participant.PenaltyServiceElapsedSeconds + 0.0005 >= participant.PenaltyServiceRequiredSeconds)
                {
                    MarkPendingPenaltiesServed(participant.Id, RacePenaltyKind.Time);
                    participant.PenaltyServiceElapsedSeconds = participant.PenaltyServiceRequiredSeconds;
                    participant.PenaltyServiceActive = false;
                    participant.PenaltyServiceAttempted = false;
                    participant.PenaltyServiceLastUpdatedAt = null;
                    participant.PenaltyServiceCompletedAt = now;
                    audits.Add(new RaceAuditEntry(
                        now,
                        "penaltyServiceCompleted",
                        $"{participant.DisplayName} 已完成 {participant.PenaltyServiceRequiredSeconds:0.#} 秒停车罚时，可以开始换胎。",
                        participant.Id));
                }
            }
            else if (participant.PenaltyServiceActive && participant.PenaltyServiceElapsedSeconds > 0)
            {
                ConvertTimePenaltyToDriveThrough(participant, now, "停车罚时完成前车辆移动", audits);
            }
        }
        else if (pendingTime > 0 && leftServiceZone && participant.PenaltyServiceAttempted)
        {
            ConvertTimePenaltyToDriveThrough(participant, now, "停车罚时完成前离开换胎区", audits);
        }

        if (exitedPit)
        {
            if (PendingTimePenaltySeconds(participant.Id) > 0 && participant.PenaltyServiceAttempted)
                ConvertTimePenaltyToDriveThrough(participant, now, "未完成停车罚时便离开维修区", audits);

            if (participant.DriveThroughVisitActive &&
                !participant.PitVisitHadServiceStop &&
                !participant.PitVisitPaused &&
                HasPendingDriveThrough(participant.Id))
            {
                MarkPendingPenaltiesServed(participant.Id, RacePenaltyKind.DriveThrough);
                participant.PenaltyServiceCompletedAt = now;
                participant.DriveThroughReminderAt = now;
                participant.DriveThroughLineCrossings = 0;
                participant.DriveThroughOverdue = false;
                audits.Add(new RaceAuditEntry(
                    now,
                    "driveThroughServed",
                    $"{participant.DisplayName} 已完成通过维修区处罚。",
                    participant.Id));
            }
            else if (participant.DriveThroughVisitActive && HasPendingDriveThrough(participant.Id))
            {
                participant.DriveThroughReminderAt = now;
                audits.Add(new RaceAuditEntry(
                    now,
                    "driveThroughAttemptFailed",
                    participant.PitVisitPaused
                        ? $"{participant.DisplayName} 执行通过维修区处罚时暂停或回转，本次进站无效。"
                        : $"{participant.DisplayName} 执行通过维修区处罚时停车，本次进站无效。",
                    participant.Id));
            }
            participant.DriveThroughVisitActive = false;
            participant.DriveThroughStopCandidateStartedAt = null;
            participant.PitVisitHadServiceStop = false;
            participant.PitVisitPaused = false;
        }
    }

    private void UpdatePitServiceState(ParticipantState participant, RaceTelemetryUpdate telemetry)
    {
        participant.IsInPitLane = telemetry.IsInPitLane;
        participant.IsInServiceZone = telemetry.IsInServiceZone;
        var serviceBlocked = PendingTimePenaltySeconds(participant.Id) > 0 ||
                             participant.PenaltyServiceActive;
        participant.PitServiceElapsedSeconds = telemetry.IsInServiceZone && !serviceBlocked
            ? telemetry.PitServiceElapsedSeconds
            : 0;
        participant.PitLaneElapsedSeconds = telemetry.PitLaneElapsedSeconds;
        participant.PitServiceRequirementMet = telemetry.IsInServiceZone &&
                                                !serviceBlocked &&
                                                telemetry.PitServiceRequirementMet;
        if (participant.PitServiceRequirementMet &&
            telemetry.CompletedPitServices == participant.CompletedPitServices + 1)
            participant.CompletedPitServices++;
    }

    private double PendingTimePenaltySeconds(Guid participantId) => penalties
        .Where(candidate => candidate.ParticipantId == participantId &&
                            !candidate.IsRevoked && !candidate.IsServed &&
                            candidate.Kind == RacePenaltyKind.Time &&
                            !candidate.IsPostRaceAdjustment)
        .Sum(candidate => candidate.ValueSeconds ?? 0);

    private bool HasPendingDriveThrough(Guid participantId) => penalties.Any(candidate =>
        candidate.ParticipantId == participantId && !candidate.IsRevoked && !candidate.IsServed &&
        candidate.Kind == RacePenaltyKind.DriveThrough);

    private void MarkPendingPenaltiesServed(Guid participantId, RacePenaltyKind kind)
    {
        for (var index = 0; index < penalties.Count; index++)
        {
            var penalty = penalties[index];
            if (penalty.ParticipantId == participantId && penalty.Kind == kind &&
                !penalty.IsRevoked && !penalty.IsServed &&
                !(kind == RacePenaltyKind.Time && penalty.IsPostRaceAdjustment))
                penalties[index] = penalty with { IsServed = true };
        }
    }

    private RacePenaltySnapshot CreateDriveThroughPenalty(
        ParticipantState participant,
        string reason,
        DateTimeOffset now)
    {
        if (phase == RaceSessionPhase.Race && totalRaceLaps - participant.CompletedLaps <= 3)
        {
            return new RacePenaltySnapshot(
                Guid.NewGuid(),
                participant.Id,
                RacePenaltyKind.Time,
                20,
                null,
                $"最后三圈下发的通过维修区处罚：{reason}",
                now,
                false,
                false,
                true);
        }

        participant.DriveThroughLineCrossings = 0;
        participant.DriveThroughReminderAt = now;
        participant.DriveThroughOverdue = false;
        participant.PenaltyServiceCompletedAt = null;
        return new RacePenaltySnapshot(
            Guid.NewGuid(),
            participant.Id,
            RacePenaltyKind.DriveThrough,
            null,
            null,
            reason,
            now,
            false,
            false);
    }

    private void UpdateDriveThroughDeadline(
        ParticipantState participant,
        DateTimeOffset now,
        bool raceFinishedForParticipant,
        ICollection<RaceAuditEntry> audits)
    {
        if (!HasPendingDriveThrough(participant.Id) ||
            participant.DriveThroughVisitActive || participant.IsInPitLane)
            return;

        participant.DriveThroughLineCrossings++;
        participant.DriveThroughReminderAt = now;
        var remaining = Math.Max(0, 2 - participant.DriveThroughLineCrossings);
        if (!raceFinishedForParticipant && participant.DriveThroughLineCrossings <= 2)
        {
            audits.Add(new RaceAuditEntry(
                now,
                "driveThroughReminder",
                remaining > 0
                    ? $"{participant.DisplayName} 的通过维修区处罚还可跨越终点线 {remaining} 次。"
                    : $"{participant.DisplayName} 必须在本圈结束前进入维修区执行通过维修区处罚。",
                participant.Id));
            return;
        }

        ConvertDriveThroughToTimeAdjustment(
            participant,
            now,
            raceFinishedForParticipant
                ? "比赛已结束，无法继续执行通过维修区处罚"
                : "收到处罚后第三次从赛道上跨越终点线",
            audits);
    }

    private void ConvertDriveThroughToTimeAdjustment(
        ParticipantState participant,
        DateTimeOffset now,
        string reason,
        ICollection<RaceAuditEntry> audits)
    {
        if (!HasPendingDriveThrough(participant.Id)) return;
        MarkPendingPenaltiesServed(participant.Id, RacePenaltyKind.DriveThrough);
        penalties.Add(new RacePenaltySnapshot(
            Guid.NewGuid(),
            participant.Id,
            RacePenaltyKind.Time,
            20,
            null,
            $"通过维修区处罚未按期执行：{reason}",
            now,
            false,
            false,
            true));
        participant.DriveThroughOverdue = true;
        participant.DriveThroughReminderAt = now;
        participant.DriveThroughVisitActive = false;
        participant.DriveThroughStopCandidateStartedAt = null;
        banner = NewBanner(
            RaceBannerKind.Penalty,
            $"通过维修区处罚逾期 · {participant.DisplayName}",
            "原处罚已替换为 20 秒完赛加时",
            participant.Id,
            TimeSpan.FromSeconds(8));
        audits.Add(new RaceAuditEntry(
            now,
            "driveThroughOverdue",
            $"{participant.DisplayName} 未按期执行通过维修区处罚，原处罚已替换为 20 秒完赛加时。",
            participant.Id));
    }

    private void ConvertTimePenaltyToDriveThrough(
        ParticipantState participant,
        DateTimeOffset now,
        string reason,
        ICollection<RaceAuditEntry> audits)
    {
        var convertedSeconds = PendingTimePenaltySeconds(participant.Id);
        if (convertedSeconds <= 0) return;
        MarkPendingPenaltiesServed(participant.Id, RacePenaltyKind.Time);
        RacePenaltySnapshot? replacement = null;
        if (!HasPendingDriveThrough(participant.Id))
        {
            replacement = CreateDriveThroughPenalty(
                participant,
                $"停车罚时执行失败：{reason}",
                now);
            penalties.Add(replacement);
        }
        participant.PenaltyServiceActive = false;
        participant.PenaltyServiceAttempted = false;
        participant.PenaltyServiceElapsedSeconds = 0;
        participant.PenaltyServiceRequiredSeconds = 0;
        participant.PenaltyServiceLastUpdatedAt = null;
        participant.PenaltyServiceCompletedAt = null;
        banner = NewBanner(
            RaceBannerKind.Penalty,
            $"罚时执行失败 · {participant.DisplayName}",
            replacement?.IsPostRaceAdjustment == true
                ? "比赛已进入最后三圈，已替换为 20 秒完赛加时"
                : "已转为通过维修区处罚",
            participant.Id,
            TimeSpan.FromSeconds(8));
        audits.Add(new RaceAuditEntry(
            now,
            "penaltyServiceFailed",
            replacement?.IsPostRaceAdjustment == true
                ? $"{participant.DisplayName} 未正确执行 {convertedSeconds:0.#} 秒停车罚时；比赛已进入最后三圈，处罚替换为 20 秒完赛加时。"
                : $"{participant.DisplayName} 未正确执行 {convertedSeconds:0.#} 秒停车罚时，处罚已转为通过维修区。",
            participant.Id));
    }

    private RacePenaltySnapshot? EvaluateShortcut(
        ParticipantState participant,
        RaceTelemetryUpdate telemetry,
        DateTimeOffset now)
    {
        RacePenaltySnapshot? penalty = null;
        if (participant.ProgressContinuityReady &&
            telemetry.TrackLengthMeters >= 50 &&
            telemetry.ClientMonotonicMilliseconds > participant.LastTelemetryMonotonicMilliseconds)
        {
            var elapsedSeconds = (telemetry.ClientMonotonicMilliseconds -
                                  participant.LastTelemetryMonotonicMilliseconds) / 1000d;
            var progressDelta = telemetry.TrackProgress - participant.LastContinuityProgress;
            if (progressDelta < -0.75) progressDelta += 1;
            var routeDistance = progressDelta * telemetry.TrackLengthMeters;
            var reportedSpeed = Math.Max(participant.SpeedKph, telemetry.SpeedKph) / 3.6;
            var plausibleDistance = Math.Max(60, reportedSpeed * elapsedSeconds * 3 + 30);
            var eligible = phase is RaceSessionPhase.Race or RaceSessionPhase.Qualifying &&
                           !telemetry.IsInPitLane && !telemetry.IsInServiceZone && !telemetry.IsApproachingPit &&
                           participant.Status is not (RaceParticipantStatus.Finished or
                               RaceParticipantStatus.DidNotFinish or RaceParticipantStatus.Disqualified or
                               RaceParticipantStatus.Disconnected);
            if (eligible && elapsedSeconds is > 0 and <= 2 && progressDelta is > 0 and < 0.75 &&
                routeDistance > plausibleDistance && !participant.ShortcutPenaltyIssued)
            {
                participant.ShortcutPenaltyIssued = true;
                participant.LapHasTrackLimitIncident = true;
                participant.TrackLimitSeverePenaltyIssued = true;
                penalty = RegisterTrackLimitIncident(
                    participant,
                    severe: true,
                    routeDistance - plausibleDistance,
                    $"跨越约 {routeDistance:0} 米参考路线，确认获得距离优势",
                    now);
            }
        }

        participant.LastTelemetryMonotonicMilliseconds = telemetry.ClientMonotonicMilliseconds;
        participant.LastContinuityProgress = telemetry.TrackProgress;
        participant.ProgressContinuityReady = telemetry.ClientMonotonicMilliseconds > 0;
        return penalty;
    }

    private RacePenaltySnapshot? EvaluatePitSpeeding(
        ParticipantState participant,
        RaceTelemetryUpdate telemetry,
        DateTimeOffset now)
    {
        if (phase != RaceSessionPhase.Race || !telemetry.IsInPitLane || telemetry.PitSpeedLimitKph <= 0 ||
            participant.Status is RaceParticipantStatus.Finished or RaceParticipantStatus.DidNotFinish or
                RaceParticipantStatus.Disqualified or RaceParticipantStatus.Disconnected)
        {
            participant.PitSpeedCandidateStartedAt = null;
            if (!telemetry.IsInPitLane) participant.PitSpeedPenaltyIssued = false;
            return null;
        }

        if (telemetry.SpeedKph <= telemetry.PitSpeedLimitKph + 2)
        {
            participant.PitSpeedCandidateStartedAt = null;
            return null;
        }
        if (participant.PitSpeedPenaltyIssued) return null;
        participant.PitSpeedCandidateStartedAt ??= now;
        if (now - participant.PitSpeedCandidateStartedAt.Value < TimeSpan.FromMilliseconds(400))
            return null;

        participant.PitSpeedPenaltyIssued = true;
        return AddAutomaticTrackLimitPenalty(
            participant,
            RacePenaltyKind.Time,
            5,
            $"维修区超速：{telemetry.SpeedKph:0} km/h，限速 {telemetry.PitSpeedLimitKph:0} km/h",
            now);
    }

    private RacePenaltySnapshot? EvaluateTrackLimits(
        ParticipantState participant,
        RaceTelemetryUpdate telemetry,
        DateTimeOffset now)
    {
        if (phase is not (RaceSessionPhase.Race or RaceSessionPhase.Qualifying) ||
            telemetry.IsInPitLane || telemetry.IsInServiceZone || telemetry.IsApproachingPit ||
            participant.Status is RaceParticipantStatus.Finished or RaceParticipantStatus.DidNotFinish or
                RaceParticipantStatus.Disqualified or RaceParticipantStatus.Disconnected)
        {
            ResetTrackLimitExcursion(participant);
            return null;
        }

        var minorOffsetMeters = Math.Clamp(participant.TrackToleranceMeters, 6, 30);
        var severeOffsetMeters = Math.Max(minorOffsetMeters + 6, severeLateralOffsetMeters);
        var absoluteOffset = Math.Abs(participant.LateralOffsetMeters);
        if (absoluteOffset >= minorOffsetMeters)
        {
            participant.LapHasTrackLimitIncident = true;
            participant.TrackLimitRejoinStartedAt = null;
            if (participant.TrackLimitExcursionStartedAt is null)
            {
                participant.TrackLimitExcursionStartedAt = now;
                participant.TrackLimitStartProgress = telemetry.TrackProgress;
                participant.TrackLimitTravelDistanceMeters = 0;
                participant.TrackLimitLastMonotonicMilliseconds = telemetry.ClientMonotonicMilliseconds;
            }
            else if (telemetry.ClientMonotonicMilliseconds > participant.TrackLimitLastMonotonicMilliseconds)
            {
                var elapsedSeconds = Math.Min(
                    2,
                    (telemetry.ClientMonotonicMilliseconds - participant.TrackLimitLastMonotonicMilliseconds) / 1000d);
                participant.TrackLimitTravelDistanceMeters += Math.Max(0, telemetry.SpeedKph) / 3.6 * elapsedSeconds;
                participant.TrackLimitLastMonotonicMilliseconds = telemetry.ClientMonotonicMilliseconds;
            }
            participant.TrackLimitMaximumOffsetMeters = Math.Max(
                participant.TrackLimitMaximumOffsetMeters,
                absoluteOffset);
            return null;
        }

        if (participant.TrackLimitExcursionStartedAt is not DateTimeOffset excursionStartedAt)
            return null;
        var rejoinOffsetMeters = Math.Max(3, minorOffsetMeters - 4);
        if (absoluteOffset > rejoinOffsetMeters)
        {
            participant.TrackLimitRejoinStartedAt = null;
            return null;
        }

        participant.TrackLimitRejoinStartedAt ??= now;
        if (now - participant.TrackLimitRejoinStartedAt.Value < TimeSpan.FromMilliseconds(400))
            return null;

        var excursionDuration = now - excursionStartedAt;
        var maximumOffset = participant.TrackLimitMaximumOffsetMeters;
        var routeDelta = telemetry.TrackProgress - participant.TrackLimitStartProgress;
        if (routeDelta < -0.5) routeDelta += 1;
        else if (routeDelta > 0.75) routeDelta -= 1;
        var routeDistance = telemetry.TrackLengthMeters >= 50
            ? Math.Max(0, routeDelta) * telemetry.TrackLengthMeters
            : 0;
        var gainedDistance = Math.Max(0, routeDistance - participant.TrackLimitTravelDistanceMeters);
        var wasAlreadyHandled = participant.TrackLimitSeverePenaltyIssued;
        ResetTrackLimitExcursion(participant);
        if (wasAlreadyHandled || excursionDuration < TimeSpan.FromMilliseconds(250))
            return null;
        var minimumGain = Math.Max(6, minorOffsetMeters * 0.35);
        if (gainedDistance < minimumGain)
            return null;
        var severe = maximumOffset >= severeOffsetMeters && gainedDistance >= Math.Max(12, minorOffsetMeters) ||
                     gainedDistance >= Math.Max(35, severeOffsetMeters);
        return RegisterTrackLimitIncident(
            participant,
            severe,
            gainedDistance,
            $"偏离参考路线 {maximumOffset:0.0} 米，估算获得约 {gainedDistance:0.0} 米距离优势",
            now);
    }

    private RacePenaltySnapshot? RegisterTrackLimitIncident(
        ParticipantState participant,
        bool severe,
        double gainedDistanceMeters,
        string evidence,
        DateTimeOffset now)
    {
        if (trackLimitMode == TrackLimitEnforcementMode.Disabled) return null;

        participant.TrackLimitWarnings++;
        if (trackLimitMode == TrackLimitEnforcementMode.WarningsOnly)
            return AddAutomaticTrackLimitPenalty(
                participant,
                RacePenaltyKind.Warning,
                null,
                $"疑似切弯获利：{evidence}（事件 {participant.TrackLimitWarnings}，待总控核查）",
                now);

        if (severe)
            return AddAutomaticTrackLimitPenalty(
                participant,
                RacePenaltyKind.Time,
                5,
                $"严重切弯：{evidence}",
                now);

        if (participant.TrackLimitWarnings <= 3)
            return AddAutomaticTrackLimitPenalty(
                participant,
                RacePenaltyKind.Warning,
                null,
                $"轻微切弯获利：{evidence}（警告 {participant.TrackLimitWarnings}/3）",
                now);

        participant.TrackLimitWarnings = 0;
        return AddAutomaticTrackLimitPenalty(
            participant,
            RacePenaltyKind.Time,
            5,
            $"轻微切弯警告累计超过 3 次：{evidence}",
            now);
    }

    private RacePenaltySnapshot AddAutomaticTrackLimitPenalty(
        ParticipantState participant,
        RacePenaltyKind kind,
        double? valueSeconds,
        string reason,
        DateTimeOffset now)
    {
        var penalty = new RacePenaltySnapshot(
            Guid.NewGuid(),
            participant.Id,
            kind,
            valueSeconds,
            null,
            reason,
            now,
            false,
            false);
        penalties.Add(penalty);
        banner = NewBanner(
            RaceBannerKind.Penalty,
            kind == RacePenaltyKind.Warning
                ? $"赛道边界警告 · {participant.DisplayName}"
                : $"自动判罚 · {participant.DisplayName}",
            PenaltyDescription(penalty),
            participant.Id,
            TimeSpan.FromSeconds(8));
        return penalty;
    }

    private static void ResetTrackLimitExcursion(ParticipantState participant)
    {
        participant.TrackLimitExcursionStartedAt = null;
        participant.TrackLimitRejoinStartedAt = null;
        participant.TrackLimitMaximumOffsetMeters = 0;
        participant.TrackLimitSeverePenaltyIssued = false;
        participant.TrackLimitStartProgress = 0;
        participant.TrackLimitTravelDistanceMeters = 0;
        participant.TrackLimitLastMonotonicMilliseconds = 0;
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

    private bool IsRaceClassificationPhase(RaceSessionPhase value) =>
        value is RaceSessionPhase.Race or RaceSessionPhase.Finished ||
        value == RaceSessionPhase.Suspended && phaseBeforeSuspension == RaceSessionPhase.Race;

    private double RaceElapsedSeconds(DateTimeOffset now)
    {
        if (startsAt is not DateTimeOffset startedAt) return 0;
        var endedAt = raceEndedAt ?? now;
        var suspended = raceSuspendedDuration;
        if (raceSuspendedAt is DateTimeOffset currentSuspension && endedAt > currentSuspension)
            suspended += endedAt - currentSuspension;
        return Math.Max(0, (endedAt - startedAt - suspended).TotalSeconds);
    }

    private double TimePenaltySeconds(Guid participantId) => penalties
        .Where(candidate => candidate.ParticipantId == participantId &&
                            !candidate.IsRevoked && !candidate.IsServed &&
                            candidate.Kind == RacePenaltyKind.Time)
        .Sum(candidate => candidate.ValueSeconds ?? 0);

    private double AdjustedRaceTotalSeconds(ParticipantState participant, DateTimeOffset now) =>
        (participant.RaceTotalSeconds ?? RaceElapsedSeconds(now)) +
        (participant.Status == RaceParticipantStatus.Finished ? TimePenaltySeconds(participant.Id) : 0);

    private double? RaceDeltaSeconds(
        ParticipantState reference,
        ParticipantState participant,
        DateTimeOffset now)
    {
        if (reference.Id == participant.Id) return 0;
        if (!IsRaceClassificationPhase(phase) || reference.CompletedLaps != participant.CompletedLaps)
            return null;
        if (reference.Status == RaceParticipantStatus.Finished &&
            participant.Status == RaceParticipantStatus.Finished)
            return AdjustedRaceTotalSeconds(participant, now) - AdjustedRaceTotalSeconds(reference, now);
        if (reference.LastLapCompletedAt is DateTimeOffset referenceCrossing &&
            participant.LastLapCompletedAt is DateTimeOffset participantCrossing)
            return (participantCrossing - referenceCrossing).TotalSeconds;
        return null;
    }

    private static string FormatRaceTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return "—";
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}.{span.Milliseconds:000}"
            : $"{(int)span.TotalMinutes}:{span.Seconds:00}.{span.Milliseconds:000}";
    }

    private static string FormatLapTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return "—";
        var span = TimeSpan.FromSeconds(seconds);
        return $"{(int)span.TotalMinutes}:{span.Seconds:00}.{span.Milliseconds:000}";
    }

    private RaceSessionSnapshot BuildSnapshot(DateTimeOffset now)
    {
        var ordered = OrderParticipants(now);
        var snapshots = new List<RaceParticipantSnapshot>(ordered.Count);
        var leader = ordered.FirstOrDefault();
        ParticipantState? prior = null;
        for (var index = 0; index < ordered.Count; index++)
        {
            var participant = ordered[index];
            var participantPenalties = penalties
                .Where(candidate => candidate.ParticipantId == participant.Id && !candidate.IsRevoked)
                .OrderBy(candidate => candidate.IssuedAt)
                .ToArray();
            var timePenaltySeconds = TimePenaltySeconds(participant.Id);
            var pendingTimePenaltySeconds = PendingTimePenaltySeconds(participant.Id);
            var hasPendingDriveThrough = HasPendingDriveThrough(participant.Id);
            double? raceTotalSeconds = IsRaceClassificationPhase(phase)
                ? participant.RaceTotalSeconds ?? RaceElapsedSeconds(now)
                : null;
            var adjustedRaceTotalSeconds = raceTotalSeconds +
                                           (participant.Status == RaceParticipantStatus.Finished
                                               ? timePenaltySeconds
                                               : 0);
            double? gapToLeader;
            double? interval;
            if (phase is RaceSessionPhase.Qualifying or RaceSessionPhase.Grid)
            {
                gapToLeader = participant.BestLapSeconds is double value && leader?.BestLapSeconds is double leaderValue
                    ? value - leaderValue
                    : null;
                interval = participant.BestLapSeconds is double intervalValue && prior?.BestLapSeconds is double priorValue
                    ? intervalValue - priorValue
                    : null;
            }
            else
            {
                gapToLeader = leader is null ? null : RaceDeltaSeconds(leader, participant, now);
                interval = prior is null ? null : RaceDeltaSeconds(prior, participant, now);
            }
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
                gapToLeader,
                interval,
                participant.IsInPitLane,
                participant.IsInServiceZone,
                participant.PitServiceElapsedSeconds,
                participant.PitServiceRequirementMet,
                participant.CompletedPitServices,
                participant.GripCondition,
                participant.BestSectorSeconds.ToArray(),
                participantPenalties,
                participant.LastSeenAt,
                participant.QualifyingFinalLapPending,
                raceTotalSeconds,
                adjustedRaceTotalSeconds,
                timePenaltySeconds,
                participant.TrackLimitWarnings,
                participant.TeamId,
                participant.TeamColor,
                participant.PitLaneElapsedSeconds,
                pendingTimePenaltySeconds,
                participant.PenaltyServiceActive,
                participant.PenaltyServiceElapsedSeconds,
                participant.PenaltyServiceRequiredSeconds,
                hasPendingDriveThrough,
                participant.PenaltyServiceCompletedAt is DateTimeOffset completedAt &&
                now - completedAt <= TimeSpan.FromSeconds(3),
                hasPendingDriveThrough ? Math.Max(0, 2 - participant.DriveThroughLineCrossings) : null,
                participant.DriveThroughReminderAt,
                participant.DriveThroughOverdue,
                participant.DriveThroughVisitActive && participant.IsInPitLane));
            prior = participant;
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
            qualifyingTimeExpired,
            IsRaceClassificationPhase(phase) ? RaceElapsedSeconds(now) : null,
            phase == RaceSessionPhase.Suspended ? phaseBeforeSuspension : null,
            driversPerTeam,
            teams,
            chequeredImminent,
            fastest?.Participant.BestLapSectorSeconds.ToArray());
    }

    private List<ParticipantState> OrderParticipants(DateTimeOffset now)
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
                .ThenByDescending(candidate => candidate.CompletedLaps)
                .ThenBy(candidate => candidate.Status == RaceParticipantStatus.Finished
                    ? AdjustedRaceTotalSeconds(candidate, now)
                    : double.MaxValue)
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
        chequeredImminent = false;
        startsAt = null;
        startSequenceAt = null;
        raceSuspendedAt = null;
        raceSuspendedDuration = TimeSpan.Zero;
        raceEndedAt = null;
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
            participant.BestLapSectorSeconds.Clear();
            participant.LastLapCompletedAt = null;
            participant.RaceTotalSeconds = null;
            participant.TrackLimitWarnings = 0;
            ResetTrackLimitExcursion(participant);
            participant.FinishedAt = null;
            participant.IsInPitLane = false;
            participant.IsInServiceZone = false;
            participant.PitServiceElapsedSeconds = 0;
            participant.PitServiceRequirementMet = false;
            participant.CompletedPitServices = 0;
            participant.PitLaneElapsedSeconds = 0;
            participant.QualifyingFinalLapPending = false;
            participant.FalseStartBaselineProgress = null;
            participant.FalseStartCandidateStartedAt = null;
            participant.FalseStartPenalized = false;
            participant.ProgressContinuityReady = false;
            participant.LastTelemetryMonotonicMilliseconds = 0;
            participant.LastContinuityProgress = 0;
            participant.ShortcutPenaltyIssued = false;
            participant.PitSpeedCandidateStartedAt = null;
            participant.PitSpeedPenaltyIssued = false;
            participant.LapHasTrackLimitIncident = false;
            ResetPenaltyServiceState(participant);
            participant.Status = participant.IsConnected ? RaceParticipantStatus.OnTrack : RaceParticipantStatus.Disconnected;
        }
    }

    private void ResetCompetitiveState(bool clearParticipants)
    {
        ClearYellowState();
        chequeredImminent = false;
        penalties.Clear();
        receivedLapEvents.Clear();
        startsAt = null;
        startSequenceAt = null;
        raceSuspendedAt = null;
        raceSuspendedDuration = TimeSpan.Zero;
        raceEndedAt = null;
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
            participant.BestLapSectorSeconds.Clear();
            participant.LastLapCompletedAt = null;
            participant.RaceTotalSeconds = null;
            participant.TrackLimitWarnings = 0;
            ResetTrackLimitExcursion(participant);
            participant.FinishedAt = null;
            participant.IsInPitLane = false;
            participant.IsInServiceZone = false;
            participant.PitServiceElapsedSeconds = 0;
            participant.PitServiceRequirementMet = false;
            participant.CompletedPitServices = 0;
            participant.PitLaneElapsedSeconds = 0;
            participant.AutomaticYellowActive = false;
            participant.HazardCandidateStartedAt = null;
            participant.HazardRecoveryStartedAt = null;
            participant.QualifyingFinalLapPending = false;
            participant.FalseStartBaselineProgress = null;
            participant.FalseStartCandidateStartedAt = null;
            participant.FalseStartPenalized = false;
            participant.ProgressContinuityReady = false;
            participant.LastTelemetryMonotonicMilliseconds = 0;
            participant.LastContinuityProgress = 0;
            participant.ShortcutPenaltyIssued = false;
            participant.PitSpeedCandidateStartedAt = null;
            participant.PitSpeedPenaltyIssued = false;
            participant.LapHasTrackLimitIncident = false;
            ResetPenaltyServiceState(participant);
            participant.Status = participant.IsConnected ? RaceParticipantStatus.Connected : RaceParticipantStatus.Disconnected;
        }
    }

    private static void ResetPenaltyServiceState(ParticipantState participant)
    {
        participant.PenaltyServiceActive = false;
        participant.PenaltyServiceAttempted = false;
        participant.PenaltyServiceElapsedSeconds = 0;
        participant.PenaltyServiceRequiredSeconds = 0;
        participant.PenaltyServiceLastUpdatedAt = null;
        participant.PenaltyServiceCompletedAt = null;
        participant.DriveThroughVisitActive = false;
        participant.DriveThroughLineCrossings = 0;
        participant.DriveThroughReminderAt = null;
        participant.DriveThroughOverdue = false;
        participant.DriveThroughStopCandidateStartedAt = null;
        participant.PitVisitHadServiceStop = false;
        participant.PitVisitPaused = false;
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

    private static void ReplaceBestLapSectors(
        ParticipantState participant,
        IReadOnlyList<double> sectors)
    {
        participant.BestLapSectorSeconds.Clear();
        foreach (var value in sectors.Take(20))
            participant.BestLapSectorSeconds.Add(
                double.IsFinite(value) && value is > 0 and <= 7_200 ? value : null);
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

    private RaceTeamDefinition? ResolveTeam(string? requestedId, string? requestedName)
    {
        var id = NormalizeReason(requestedId, 40);
        if (id is not null)
        {
            var byId = teams.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
            if (byId is not null) return byId;
        }

        var name = RaceProtocolValidation.NormalizeTeamName(requestedName);
        return name is null
            ? null
            : teams.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private bool TeamHasCapacity(string teamId, Guid? exceptParticipantId = null) =>
        participants.Count(candidate =>
            candidate.Id != exceptParticipantId &&
            string.Equals(candidate.TeamId, teamId, StringComparison.OrdinalIgnoreCase)) < driversPerTeam;

    private RaceTeamDefinition? SelectLegacyTeam(Guid? exceptParticipantId)
        => teams
            .Select((team, index) => new
            {
                Team = team,
                Index = index,
                Members = participants.Count(candidate =>
                    candidate.Id != exceptParticipantId &&
                    string.Equals(candidate.TeamId, team.Id, StringComparison.OrdinalIgnoreCase))
            })
            .Where(candidate => candidate.Members < driversPerTeam)
            .OrderBy(candidate => candidate.Members)
            .ThenBy(candidate => candidate.Index)
            .Select(candidate => candidate.Team)
            .FirstOrDefault();

    private static bool IsLegacyTeamClient(string? clientVersion)
    {
        var match = Regex.Match(
            clientVersion?.Trim() ?? string.Empty,
            @"^v?(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success &&
               int.Parse(match.Groups["major"].Value) == 1 &&
               int.Parse(match.Groups["minor"].Value) == 4 &&
               int.Parse(match.Groups["patch"].Value) <= 2;
    }

    private static IReadOnlyList<RaceTeamDefinition> NormalizeTeams(
        int requestedCount,
        IReadOnlyList<RaceTeamDefinition>? configured)
    {
        var count = Math.Clamp(requestedCount, 1, RaceProtocol.MaximumParticipants);
        var source = configured ?? [];
        string[] fallbackColors =
        [
            "#42D7E8", "#FF4057", "#5A8CFF", "#FFD328", "#B86CFF", "#34D17B",
            "#FF8A3D", "#EE4FA6", "#B8F34A", "#8FA3B8", "#6FD6A7", "#F28B82"
        ];
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<RaceTeamDefinition>(count);
        for (var index = 0; index < count; index++)
        {
            var candidate = index < source.Count ? source[index] : null;
            var id = NormalizeReason(candidate?.Id, 40) ?? $"team-{index + 1}";
            if (!ids.Add(id))
            {
                id = $"team-{index + 1}";
                while (!ids.Add(id)) id += "-next";
            }
            var name = RaceProtocolValidation.NormalizeTeamName(candidate?.Name) ?? $"车队 {index + 1}";
            if (!names.Add(name))
            {
                name = $"{name} {index + 1}";
                while (!names.Add(name)) name += "-";
            }
            string color;
            try
            {
                color = RaceProtocolValidation.NormalizeThemeColor(candidate?.ThemeColor);
            }
            catch (ArgumentException)
            {
                color = fallbackColors[index % fallbackColors.Length];
            }
            result.Add(new RaceTeamDefinition(id, name, color));
        }
        return result;
    }

    private void RefreshChequeredImminent(DateTimeOffset now)
    {
        if (phase != RaceSessionPhase.Race || flag == RaceControlFlag.Chequered)
        {
            chequeredImminent = false;
            return;
        }
        if (chequeredImminent) return;
        var leader = OrderParticipants(now).FirstOrDefault(candidate =>
            candidate.IsConnected &&
            candidate.Status is not (RaceParticipantStatus.Finished or RaceParticipantStatus.DidNotFinish or
                RaceParticipantStatus.Disqualified or RaceParticipantStatus.Disconnected));
        if (leader is null || leader.CompletedLaps != totalRaceLaps - 1 || leader.TrackProgress < 0.94)
            return;
        chequeredImminent = true;
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
        => Publish(
            snapshot,
            important,
            audit is null ? Array.Empty<RaceAuditEntry>() : [audit]);

    private void Publish(
        RaceSessionSnapshot snapshot,
        bool important,
        IReadOnlyCollection<RaceAuditEntry> audits)
    {
        if (important) persistence.SaveImportantSnapshot(snapshot);
        foreach (var audit in audits)
        {
            persistence.AppendAudit(audit);
            lock (sync)
            {
                events.Add(new RaceEventSnapshot(
                    ++eventSequence,
                    audit.At,
                    audit.Type,
                    audit.Message,
                    audit.ParticipantId));
                if (events.Count > 500) events.RemoveRange(0, events.Count - 500);
            }
        }
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
        RacePenaltyKind.Time => $"待执行 +{penalty.ValueSeconds:0.#} 秒 · {penalty.Reason}",
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
        DateTimeOffset joinedAt,
        string? teamId = null,
        string? teamColor = null)
    {
        public Guid Id { get; } = id;
        public string ResumeToken { get; } = resumeToken;
        public string DisplayName { get; set; } = displayName;
        public string ThemeColor { get; set; } = themeColor;
        public string? TeamName { get; set; } = teamName;
        public string? TeamId { get; set; } = teamId;
        public string? TeamColor { get; set; } = teamColor;
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
        public DateTimeOffset? LastLapCompletedAt { get; set; }
        public double? RaceTotalSeconds { get; set; }
        public double TrackToleranceMeters { get; set; } = 18;
        public int TrackLimitWarnings { get; set; }
        public DateTimeOffset? TrackLimitExcursionStartedAt { get; set; }
        public DateTimeOffset? TrackLimitRejoinStartedAt { get; set; }
        public double TrackLimitMaximumOffsetMeters { get; set; }
        public bool TrackLimitSeverePenaltyIssued { get; set; }
        public double TrackLimitStartProgress { get; set; }
        public double TrackLimitTravelDistanceMeters { get; set; }
        public long TrackLimitLastMonotonicMilliseconds { get; set; }
        public bool LapHasTrackLimitIncident { get; set; }
        public bool ProgressContinuityReady { get; set; }
        public long LastTelemetryMonotonicMilliseconds { get; set; }
        public double LastContinuityProgress { get; set; }
        public bool ShortcutPenaltyIssued { get; set; }
        public List<double?> BestSectorSeconds { get; } = [];
        public List<double?> BestLapSectorSeconds { get; } = [];
        public bool IsInPitLane { get; set; }
        public bool IsInServiceZone { get; set; }
        public double PitServiceElapsedSeconds { get; set; }
        public bool PitServiceRequirementMet { get; set; }
        public int CompletedPitServices { get; set; }
        public double PitLaneElapsedSeconds { get; set; }
        public DateTimeOffset? PitSpeedCandidateStartedAt { get; set; }
        public bool PitSpeedPenaltyIssued { get; set; }
        public bool PenaltyServiceActive { get; set; }
        public bool PenaltyServiceAttempted { get; set; }
        public double PenaltyServiceElapsedSeconds { get; set; }
        public double PenaltyServiceRequiredSeconds { get; set; }
        public DateTimeOffset? PenaltyServiceLastUpdatedAt { get; set; }
        public DateTimeOffset? PenaltyServiceCompletedAt { get; set; }
        public bool DriveThroughVisitActive { get; set; }
        public int DriveThroughLineCrossings { get; set; }
        public DateTimeOffset? DriveThroughReminderAt { get; set; }
        public bool DriveThroughOverdue { get; set; }
        public DateTimeOffset? DriveThroughStopCandidateStartedAt { get; set; }
        public bool PitVisitHadServiceStop { get; set; }
        public bool PitVisitPaused { get; set; }
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
