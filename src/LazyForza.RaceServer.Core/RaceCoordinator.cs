using System.Diagnostics;
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
    private const int MaximumLiveGapSamples = 3_600;
    private const double LiveGapHistoryLaps = 1.25;
    private const double LiveGapProgressJitter = 0.002;
    private const double MaximumLiveGapDistanceLaps = 0.999;
    private const double MinimumCollisionImpactMagnitudeMps = 1.4;
    private const double StrongCollisionImpactMagnitudeMps = 2.8;
    private const double MinimumCollisionRelativeSpeedMps = .8;
    private const double MinimumCollisionSpeedLossMps = 1.25;
    private const double MinimumCollisionApproachMeters = .2;
    private const double MaximumCollisionHorizontalDistanceMeters = 5.2;
    private const double MaximumCollisionVerticalDistanceMeters = 2.5;
    private const int MaximumCollisionInvestigationsPerSession = 24;
    private static readonly TimeSpan CollisionEvidenceLifetime = TimeSpan.FromMilliseconds(1_000);
    private static readonly TimeSpan CollisionPeerFreshness = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan CollisionTrajectoryLifetime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CollisionTrajectoryMatchTolerance = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan CollisionApproachLookback = TimeSpan.FromMilliseconds(280);
    private static readonly TimeSpan CollisionPairCooldown = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan MinimumTelemetrySnapshotInterval = TimeSpan.FromMilliseconds(100);
    private readonly object sync = new();
    private readonly RaceServerOptions options;
    private readonly IRaceStatePersistence persistence;
    private readonly List<ParticipantState> participants = [];
    private readonly List<ObserverState> observers = [];
    private readonly List<RacePenaltySnapshot> penalties = [];
    private readonly List<RaceInvestigationSnapshot> investigations = [];
    private readonly List<RaceEventSnapshot> events = [];
    private readonly HashSet<Guid> receivedLapEvents = [];
    private readonly HashSet<string> revokedResumeTokens = new(StringComparer.Ordinal);
    private readonly Func<string, bool> playerPasswordMatches;
    private readonly Dictionary<int, string> manualSectorYellows = [];
    private string? manualFullCourseYellow;
    private RaceSessionPhase phase = RaceSessionPhase.Lobby;
    private RaceSessionPhase phaseBeforeSuspension = RaceSessionPhase.Race;
    private RaceControlFlag flag = RaceControlFlag.Green;
    private string? flagMessage;
    private string sessionName;
    private int totalRaceLaps;
    private int minimumRequiredPitStops;
    private int sectorCount;
    private bool automaticYellowEnabled;
    private bool automaticCollisionInvestigationsEnabled;
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
    private DateTimeOffset? practiceEndsAt;
    private int qualifyingSessionNumber;
    private int qualifyingSessionCount = 1;
    private IReadOnlyList<int> qualifyingSessionMinutes = [10];
    private IReadOnlyList<int> qualifyingEliminationCounts = [];
    private int practiceSessionNumber;
    private int practiceSessionCount = 1;
    private IReadOnlyList<int> practiceSessionMinutes = [60];
    private int illuminatedStartLights;
    private bool startLightsOut;
    private bool qualifyingTimeExpired;
    private bool practiceTimeExpired;
    private RaceBannerSnapshot? banner;
    private long revision;
    private long eventSequence;
    private long lastTelemetrySnapshotTimestamp;

    public RaceCoordinator(
        RaceServerOptions options,
        IRaceStatePersistence? persistence = null,
        Func<string, bool>? playerPasswordMatches = null)
    {
        this.options = options.Normalize();
        this.persistence = persistence ?? NullRaceStatePersistence.Instance;
        sessionName = this.options.SessionName;
        totalRaceLaps = this.options.TotalRaceLaps;
        minimumRequiredPitStops = this.options.MinimumRequiredPitStops;
        sectorCount = this.options.SectorCount;
        automaticYellowEnabled = this.options.AutomaticYellowEnabled;
        automaticCollisionInvestigationsEnabled = this.options.AutomaticCollisionInvestigationsEnabled;
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
                trackLimitMode,
                minimumRequiredPitStops,
                automaticCollisionInvestigationsEnabled);
    }

    public IReadOnlyList<RaceEventSnapshot> Events(int limit = 200, long? afterSequence = null)
    {
        lock (sync)
            return events
                .Where(item => afterSequence is null || item.Sequence > afterSequence.Value)
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
            else if (!string.IsNullOrWhiteSpace(request.ResumeToken) &&
                     revokedResumeTokens.Any(token => ConstantTimeEquals(token, request.ResumeToken)))
            {
                rejected = new RaceLoginRejected(
                    "disconnectedByControl",
                    "赛事总控已断开这个客户端。再次手动进入房间时可以重新申请席位。");
            }
            else
            {
                string displayName;
                string themeColor;
                string? teamName;
                string? teamId;
                string? teamColor;
                var resumed = request.IsObserver ? null : FindByResumeToken(request.ResumeToken);
                try
                {
                    displayName = RaceProtocolValidation.NormalizeDisplayName(request.DisplayName);
                    themeColor = RaceProtocolValidation.NormalizeThemeColor(request.ThemeColor);
                    if (request.IsObserver)
                    {
                        var resumedObserver = FindObserverByResumeToken(request.ResumeToken);
                        if (HasDuplicateName(displayName, resumedObserver?.Id))
                        {
                            rejected = new RaceLoginRejected("duplicateName", "该显示名已被其他车手或 OB 使用。");
                            goto Complete;
                        }
                        if (resumedObserver is not null)
                        {
                            resumedObserver.DisplayName = displayName;
                            IncrementRevision();
                            published = BuildSnapshot(DateTimeOffset.UtcNow);
                            accepted = new RaceLoginAccepted(
                                resumedObserver.Id,
                                resumedObserver.ResumeToken,
                                published,
                                published.ServerTime,
                                true);
                            audit = new RaceAuditEntry(
                                published.ServerTime,
                                "observerResumed",
                                $"OB {displayName} 重新连接。",
                                resumedObserver.Id);
                            goto Complete;
                        }
                        if (observers.Count >= RaceProtocol.MaximumObservers)
                        {
                            rejected = new RaceLoginRejected(
                                "observerFull",
                                $"OB 席位已达到 {RaceProtocol.MaximumObservers} 人上限。");
                            goto Complete;
                        }
                        var observer = new ObserverState(
                            Guid.NewGuid(),
                            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                            displayName,
                            DateTimeOffset.UtcNow);
                        observers.Add(observer);
                        IncrementRevision();
                        published = BuildSnapshot(DateTimeOffset.UtcNow);
                        accepted = new RaceLoginAccepted(
                            observer.Id,
                            observer.ResumeToken,
                            published,
                            published.ServerTime,
                            true);
                        audit = new RaceAuditEntry(
                            published.ServerTime,
                            "observerJoined",
                            $"OB {displayName} 加入转播席。",
                            observer.Id);
                        goto Complete;
                    }
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
                    {
                        resumed.Status = flag == RaceControlFlag.Chequered &&
                                         phase is RaceSessionPhase.Race or RaceSessionPhase.Finished
                            ? RaceParticipantStatus.DidNotFinish
                            : phase is RaceSessionPhase.Race or RaceSessionPhase.Countdown or
                                RaceSessionPhase.Practice or RaceSessionPhase.OutLap or
                                RaceSessionPhase.FormationLap
                                ? RaceParticipantStatus.OnTrack
                                : RaceParticipantStatus.Connected;
                        if (resumed.Status == RaceParticipantStatus.DidNotFinish)
                            resumed.FinishedAt ??= DateTimeOffset.UtcNow;
                    }
                    TryCompleteRaceIfReady(DateTimeOffset.UtcNow);
                    IncrementRevision();
                    published = BuildSnapshot(DateTimeOffset.UtcNow);
                    accepted = new RaceLoginAccepted(resumed.Id, resumed.ResumeToken, published, published.ServerTime);
                    audit = new RaceAuditEntry(published.ServerTime, "participantResumed", $"{displayName} 重新连接。", resumed.Id);
                    goto Complete;
                }

                if (phase == RaceSessionPhase.Qualifying && qualifyingSessionCount > 1)
                {
                    rejected = new RaceLoginRejected(
                        "sessionLocked",
                        "多节排位赛已经开始，只允许已参赛车手重新连接。");
                    goto Complete;
                }
                if (phase is RaceSessionPhase.OutLap or RaceSessionPhase.FormationLap or
                    RaceSessionPhase.Countdown or RaceSessionPhase.Race or
                    RaceSessionPhase.Suspended or RaceSessionPhase.Finished)
                {
                    rejected = new RaceLoginRejected("sessionLocked", "比赛已开始，只允许已有车手重新连接。");
                    goto Complete;
                }
                if (participants.Count(candidate => candidate.ReservationActive) >= options.MaximumParticipants)
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
            if (phase is not (RaceSessionPhase.Lobby or RaceSessionPhase.Practice or
                    RaceSessionPhase.Qualifying or RaceSessionPhase.Grid))
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
        RaceSessionSnapshot? snapshot = null;
        var audits = new List<RaceAuditEntry>();
        var important = false;
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
            if (CanExecutePenalties(participant))
                UpdatePenaltyServiceState(participant, normalized, now, audits);
            else
                ResetLivePenaltyServiceState(participant);
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
                participant.LastReportedImpactSequence = Math.Max(
                    participant.LastReportedImpactSequence,
                    normalized.ImpactSequence);
                participant.LastProcessedImpactSequence = participant.LastReportedImpactSequence;
                participant.LastImpactAt = null;
                participant.LastImpactMagnitudeMps = 0;
                participant.LastImpactSpeedLossMps = 0;
                participant.LastImpactSmashableVelDiff = 0;
                participant.LastImpactSmashableMass = 0;
                participant.CollisionPositionSamples.Clear();
                IncrementRevision();
                goto Complete;
            }

            var shortcutDecision = EvaluateShortcut(participant, normalized, now);
            participant.TelemetryValid = true;
            participant.TrackProgress = normalized.TrackProgress;
            participant.LateralOffsetMeters = normalized.LateralOffsetMeters;
            participant.MapX = normalized.MapX;
            participant.MapY = normalized.MapY;
            participant.SpeedKph = normalized.SpeedKph;
            participant.CurrentSector = Math.Clamp(normalized.CurrentSector, 0, sectorCount - 1);
            participant.CurrentLapSeconds = normalized.CurrentLapSeconds;
            participant.TrackToleranceMeters = normalized.TrackToleranceMeters;
            participant.HasWorldPosition = normalized.HasWorldPosition;
            participant.WorldX = normalized.WorldX;
            participant.WorldY = normalized.WorldY;
            participant.WorldZ = normalized.WorldZ;
            participant.VelocityX = normalized.VelocityX;
            participant.VelocityY = normalized.VelocityY;
            participant.VelocityZ = normalized.VelocityZ;
            participant.LastTelemetryReceivedAt = now;
            RecordCollisionPositionSample(participant, normalized, now);
            participant.IsApproachingPit = normalized.IsApproachingPit;
            participant.IsOnPitRoute = normalized.IsOnPitRoute;
            participant.GripCondition = normalized.GripCondition;
            if (phase == RaceSessionPhase.Race)
                RecordRaceProgressSample(participant, now);
            if (participant.Status is not (RaceParticipantStatus.Finished or RaceParticipantStatus.DidNotFinish or
                    RaceParticipantStatus.Disqualified or RaceParticipantStatus.Disconnected))
                participant.Status = normalized.IsInServiceZone
                    ? RaceParticipantStatus.InService
                    : normalized.IsInPitLane
                        ? RaceParticipantStatus.InPitLane
                        : phase == RaceSessionPhase.Qualifying && !participant.QualifyingEligible
                            ? RaceParticipantStatus.Ready
                        : phase is RaceSessionPhase.Race or RaceSessionPhase.Countdown or RaceSessionPhase.Practice or
                            RaceSessionPhase.Qualifying or
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
            var trackLimitDecision = EvaluateTrackLimits(participant, normalized, now);
            AddTrackLimitAudit(audits, now, participant, trackLimitDecision, "automaticTrackLimitPenalty");
            AddTrackLimitAudit(audits, now, participant, shortcutDecision, "automaticShortcutPenalty");
            if (EvaluatePitSpeeding(participant, normalized, now) is { } pitSpeedPenalty)
                audits.Add(new RaceAuditEntry(
                    now,
                    "automaticPitSpeedPenalty",
                    $"{participant.DisplayName}：{PenaltyDescription(pitSpeedPenalty)}。",
                    participant.Id,
                    pitSpeedPenalty));
            var automaticYellowBefore = participant.AutomaticYellowActive;
            EvaluateAutomaticYellow(
                participant,
                now,
                normalized.IsOnPitRoute || normalized.IsApproachingPit);
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
            EvaluateCollisionInvestigations(participant, normalized, now, audits);
            IncrementRevision();
        Complete:;
            important = audits.Count > 0;
            if (ShouldPublishTelemetrySnapshot(important))
                snapshot = BuildSnapshot(now);
        }
        if (snapshot is not null) Publish(snapshot, important, audits);
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
            if (participant.Status is RaceParticipantStatus.Finished or RaceParticipantStatus.DidNotFinish or
                RaceParticipantStatus.Disqualified or RaceParticipantStatus.Disconnected)
                return RaceCommandResult.Reject("该车手已经结束比赛，不能继续提交圈速。");
            if (phase is not (RaceSessionPhase.Practice or RaceSessionPhase.Qualifying or RaceSessionPhase.Race))
                return RaceCommandResult.Reject("当前阶段不接收圈速成绩。");
            if (phase == RaceSessionPhase.Qualifying && !participant.QualifyingEligible)
                return RaceCommandResult.Reject("该车手已在本次排位赛中被淘汰。");
            if (phase == RaceSessionPhase.Qualifying && qualifyingTimeExpired &&
                !participant.QualifyingFinalLapPending)
                return RaceCommandResult.Reject("排位赛计时已结束，该车手没有待完成的最后一圈。");
            if (phase == RaceSessionPhase.Practice && practiceTimeExpired &&
                !participant.PracticeFinalLapPending)
                return RaceCommandResult.Reject("练习赛计时已结束，该车手没有待完成的最后一圈。");
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
                if (phase == RaceSessionPhase.Race)
                    RecordRaceProgressSample(participant, now, participant.CompletedLaps);
                participant.CurrentLapSeconds = 0;
                participant.ShortcutPenaltyIssued = false;
                participant.ProgressContinuityReady = false;
                if (bestLapEligible)
                {
                    if (improvesPersonalBest)
                    {
                        participant.BestLapSeconds = completed.LapSeconds;
                        ReplaceBestLapSectors(participant, completed.SectorSeconds);
                        if (phase == RaceSessionPhase.Qualifying && qualifyingSessionNumber > 0)
                            participant.QualifyingSessionBestLapSeconds[qualifyingSessionNumber - 1] =
                                completed.LapSeconds;
                        if (phase == RaceSessionPhase.Practice && practiceSessionNumber > 0)
                            participant.PracticeSessionBestLapSeconds[practiceSessionNumber - 1] =
                                completed.LapSeconds;
                    }
                    UpdateBestSectors(participant, completed.SectorSeconds);
                }
            }
            participant.LapHasTrackLimitIncident = false;
            if (phase == RaceSessionPhase.Qualifying && qualifyingTimeExpired)
            {
                participant.QualifyingFinalLapPending = false;
                CompleteQualifyingIfReady(now);
            }
            if (phase == RaceSessionPhase.Practice && practiceTimeExpired)
            {
                participant.PracticeFinalLapPending = false;
                CompletePracticeIfReady(now);
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

                if (participant.Status == RaceParticipantStatus.Finished)
                {
                    FinalizePendingPenaltiesAtFinish(participant, now, audits);
                    EnforceMinimumPitStopsAtFinish(participant, now, audits);
                }
                else
                    UpdateDriveThroughDeadline(participant, now, false, audits);

                TryCompleteRaceIfReady(now);
            }
            else if (phase == RaceSessionPhase.Race)
            {
                UpdateDriveThroughDeadline(participant, now, false, audits);
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
            if (participant is null)
            {
                var observer = observers.FirstOrDefault(candidate => candidate.Id == participantId);
                if (observer is null) return;
                observers.Remove(observer);
                IncrementRevision();
                snapshot = BuildSnapshot(DateTimeOffset.UtcNow);
                audit = new RaceAuditEntry(
                    snapshot.ServerTime,
                    "observerDisconnected",
                    $"OB {observer.DisplayName} 断开连接。",
                    observer.Id);
                goto Complete;
            }
            if (!participant.IsConnected) return;
            participant.IsConnected = false;
            if (participant.Status is not (RaceParticipantStatus.Finished or
                    RaceParticipantStatus.DidNotFinish or RaceParticipantStatus.Disqualified))
                participant.Status = RaceParticipantStatus.Disconnected;
            participant.AutomaticYellowActive = false;
            participant.HazardCandidateStartedAt = null;
            participant.HazardRecoveryStartedAt = null;
            ResetCollisionState(participant);
            participant.LastSeenAt = DateTimeOffset.UtcNow;
            participant.QualifyingFinalLapPending = false;
            participant.PracticeFinalLapPending = false;
            CompleteQualifyingIfReady(DateTimeOffset.UtcNow);
            CompletePracticeIfReady(DateTimeOffset.UtcNow);
            RefreshYellowFlag(DateTimeOffset.UtcNow);
            TryCompleteRaceIfReady(DateTimeOffset.UtcNow);
            IncrementRevision();
            snapshot = BuildSnapshot(DateTimeOffset.UtcNow);
            audit = new RaceAuditEntry(snapshot.ServerTime, "participantDisconnected", $"{participant.DisplayName} 断开连接。", participant.Id);
        Complete:;
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
            minimumRequiredPitStops = Math.Clamp(command.MinimumRequiredPitStops, 0, 20);
            sectorCount = Math.Clamp(command.SectorCount, 1, 20);
            automaticYellowEnabled = command.AutomaticYellowEnabled;
            automaticCollisionInvestigationsEnabled = command.AutomaticCollisionInvestigationsEnabled;
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
            if (!automaticCollisionInvestigationsEnabled)
            {
                foreach (var participant in participants)
                {
                    participant.LastProcessedImpactSequence = participant.LastReportedImpactSequence;
                    participant.LastImpactAt = null;
                    participant.LastImpactMagnitudeMps = 0;
                    participant.LastImpactSpeedLossMps = 0;
                    participant.CollisionPairCooldowns.Clear();
                }
            }
            IncrementRevision();
            snapshot = BuildSnapshot(DateTimeOffset.UtcNow);
            audit = new RaceAuditEntry(
                snapshot.ServerTime,
                "roomSettings",
                $"房间设置已保存：{sessionName}，{totalRaceLaps} 圈，最少 {minimumRequiredPitStops} 次进站，{sectorCount} 个分段，赛道边界模式 {trackLimitMode}。",
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
                case RaceSessionPhase.Practice:
                    if (TryStartNextPracticeSession(now)) break;
                    ResetCompetitiveState(clearParticipants: false);
                    ConfigurePractice(command);
                    phase = RaceSessionPhase.Practice;
                    flag = RaceControlFlag.Green;
                    practiceSessionNumber = 1;
                    practiceEndsAt = now.AddMinutes(practiceSessionMinutes[0]);
                    practiceTimeExpired = false;
                    foreach (var participant in participants)
                    {
                        participant.Status = participant.IsConnected
                            ? RaceParticipantStatus.OnTrack
                            : RaceParticipantStatus.Disconnected;
                        participant.IsReady = false;
                        participant.PracticeFinalLapPending = false;
                        Array.Fill(participant.PracticeSessionBestLapSeconds, null);
                    }
                    banner = NewBanner(
                        RaceBannerKind.Information,
                        PracticeSessionLabel() + " 开始",
                        $"{practiceSessionMinutes[0]} 分钟",
                        null,
                        TimeSpan.FromSeconds(5));
                    break;
                case RaceSessionPhase.Qualifying:
                    if (TryStartNextQualifyingSession(now)) break;
                    ResetCompetitiveState(clearParticipants: false);
                    ConfigureQualifying(command);
                    phase = RaceSessionPhase.Qualifying;
                    flag = RaceControlFlag.Green;
                    qualifyingSessionNumber = 1;
                    qualifyingEndsAt = now.AddMinutes(qualifyingSessionMinutes[0]);
                    qualifyingTimeExpired = false;
                    foreach (var participant in participants)
                    {
                        participant.Status = participant.IsConnected ? RaceParticipantStatus.OnTrack : RaceParticipantStatus.Disconnected;
                        participant.IsReady = false;
                        participant.QualifyingEligible = participant.IsConnected;
                        participant.QualifyingEliminatedInSession = null;
                        Array.Fill(participant.QualifyingSessionBestLapSeconds, null);
                    }
                    banner = NewBanner(
                        RaceBannerKind.Information,
                        qualifyingSessionCount == 1 ? "排位赛开始" : "Q1 开始",
                        qualifyingSessionCount == 1
                            ? sessionName
                            : $"{qualifyingSessionMinutes[0]} 分钟 · 本节淘汰 {qualifyingEliminationCounts[0]} 人",
                        null,
                        TimeSpan.FromSeconds(5));
                    break;
                case RaceSessionPhase.Grid:
                    CaptureCurrentQualifyingResults();
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

    public RaceCommandResult SetAutomaticCollisionInvestigations(bool enabled)
    {
        RaceSessionSnapshot snapshot;
        RaceAuditEntry audit;
        lock (sync)
        {
            automaticCollisionInvestigationsEnabled = enabled;
            if (!enabled)
            {
                foreach (var participant in participants)
                {
                    participant.LastProcessedImpactSequence = participant.LastReportedImpactSequence;
                    participant.LastImpactAt = null;
                    participant.LastImpactMagnitudeMps = 0;
                    participant.LastImpactSpeedLossMps = 0;
                    participant.CollisionPairCooldowns.Clear();
                }
            }
            IncrementRevision();
            snapshot = BuildSnapshot(DateTimeOffset.UtcNow);
            audit = new RaceAuditEntry(
                snapshot.ServerTime,
                "collisionInvestigationSetting",
                enabled ? "赛事总控已启用疑似碰撞自动调查。" : "赛事总控已关闭疑似碰撞自动调查；已有调查仍会保留。");
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
                    false,
                    command.Kind == RacePenaltyKind.Time && RequiresPostRaceAdjustment(participant));
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

    public RaceCommandResult UpdatePenalty(RaceAdminPenaltyUpdateCommand command)
    {
        RaceSessionSnapshot snapshot;
        RaceAuditEntry audit;
        lock (sync)
        {
            var now = DateTimeOffset.UtcNow;
            var index = penalties.FindIndex(candidate => candidate.Id == command.PenaltyId);
            if (index < 0) return RaceCommandResult.Reject("处罚记录不存在。");
            var existing = penalties[index];
            var participant = Find(existing.ParticipantId);
            if (participant is null) return RaceCommandResult.Reject("参赛者不存在。");
            var reason = NormalizeReason(command.Reason, 240);
            var updated = existing with
            {
                ValueSeconds = existing.Kind == RacePenaltyKind.Time && command.ValueSeconds is double seconds
                    ? Math.Clamp(Math.Round(seconds), 1, 60)
                    : existing.ValueSeconds,
                Reason = string.IsNullOrWhiteSpace(reason) ? existing.Reason : reason,
                IsRevoked = command.IsRevoked || existing.IsRevoked
            };
            penalties[index] = updated;
            if (updated.IsRevoked)
            {
                if (updated.Kind == RacePenaltyKind.Disqualification && participant.Status == RaceParticipantStatus.Disqualified)
                    participant.Status = participant.FinishedAt is null
                        ? RaceParticipantStatus.OnTrack
                        : RaceParticipantStatus.Finished;
                if (PendingTimePenaltySeconds(participant.Id) <= 0 && !HasPendingDriveThrough(participant.Id))
                    ResetLivePenaltyServiceState(participant);
            }
            IncrementRevision();
            snapshot = BuildSnapshot(now);
            audit = new RaceAuditEntry(
                now,
                updated.IsRevoked ? "penaltyRevoked" : "penaltyUpdated",
                updated.IsRevoked
                    ? $"赛事总控取消了 {participant.DisplayName} 的处罚：{PenaltyDescription(existing)}。"
                    : $"赛事总控修改了 {participant.DisplayName} 的处罚：{PenaltyDescription(updated)}。",
                participant.Id,
                updated);
        }
        Publish(snapshot, important: true, audit);
        return RaceCommandResult.Accepted;
    }

    public RaceCommandResult ResolveInvestigation(RaceAdminInvestigationCommand command)
    {
        RaceSessionSnapshot snapshot;
        RaceAuditEntry audit;
        lock (sync)
        {
            var now = DateTimeOffset.UtcNow;
            var index = investigations.FindIndex(candidate => candidate.Id == command.InvestigationId);
            if (index < 0) return RaceCommandResult.Reject("调查记录不存在。");
            var existing = investigations[index];
            if (existing.Status != RaceInvestigationStatus.Pending)
                return RaceCommandResult.Reject("该调查已经处理。");
            var targetParticipantId = command.ParticipantId ?? existing.ParticipantId;
            var relatedParticipantIds = existing.RelatedParticipantIds ?? [existing.ParticipantId];
            if (!relatedParticipantIds.Contains(targetParticipantId))
                return RaceCommandResult.Reject("所选车手不在该调查事件中。");
            var participant = Find(targetParticipantId);
            if (participant is null) return RaceCommandResult.Reject("参赛者不存在。");

            RacePenaltySnapshot? penalty = null;
            if (command.ApplyPenalty)
            {
                var kind = command.Kind ?? RacePenaltyKind.Time;
                if (kind is RacePenaltyKind.GridDrop or RacePenaltyKind.StopAndGo)
                    return RaceCommandResult.Reject("调查处理暂不支持该处罚类型。");
                var reason = NormalizeReason(command.Reason, 240) ?? existing.Offense;
                penalty = kind == RacePenaltyKind.DriveThrough
                    ? CreateDriveThroughPenalty(participant, reason, now) with
                    {
                        IsAutomatic = false,
                        InvestigationId = existing.Id
                    }
                    : new RacePenaltySnapshot(
                        Guid.NewGuid(),
                        participant.Id,
                        kind,
                        kind == RacePenaltyKind.Time
                            ? Math.Clamp(Math.Round(command.ValueSeconds ?? 5), 1, 60)
                            : null,
                        null,
                        reason,
                        now,
                        false,
                        false,
                        kind == RacePenaltyKind.Time && RequiresPostRaceAdjustment(participant),
                        false,
                        existing.Id);
                penalties.Add(penalty);
                if (kind == RacePenaltyKind.Disqualification)
                    participant.Status = RaceParticipantStatus.Disqualified;
                investigations[index] = existing with
                {
                    Status = RaceInvestigationStatus.Penalized,
                    PenaltyId = penalty.Id,
                    ResolvedAt = now
                };
                banner = NewBanner(
                    RaceBannerKind.Penalty,
                    $"调查结论 · {participant.DisplayName}",
                    PenaltyDescription(penalty),
                    participant.Id,
                    TimeSpan.FromSeconds(8));
            }
            else
            {
                investigations[index] = existing with
                {
                    Status = RaceInvestigationStatus.Dismissed,
                    ResolvedAt = now
                };
            }

            IncrementRevision();
            snapshot = BuildSnapshot(now);
            audit = new RaceAuditEntry(
                now,
                command.ApplyPenalty ? "investigationPenalized" : "investigationDismissed",
                command.ApplyPenalty
                    ? $"赛事总控确认 {participant.DisplayName} 的调查事件并下发：{PenaltyDescription(penalty!)}。"
                    : $"赛事总控结束对 {participant.DisplayName} 的调查，不予处罚：{existing.Offense}。",
                participant.Id,
                investigations[index]);
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

    public RaceCommandResult DisconnectAndReleaseClient(Guid clientId)
    {
        RaceSessionSnapshot snapshot;
        RaceAuditEntry audit;
        lock (sync)
        {
            var now = DateTimeOffset.UtcNow;
            var participant = Find(clientId);
            if (participant is not null)
            {
                if (!participant.ReservationActive)
                    return RaceCommandResult.Reject("该车手已经由总控断开。");
                var releasedName = participant.DisplayName;
                revokedResumeTokens.Add(participant.ResumeToken);
                participant.ReservationActive = false;
                participant.IsConnected = false;
                participant.DisplayName = $"{releasedName} · 已断开";
                if (participant.Status is not (RaceParticipantStatus.Finished or
                        RaceParticipantStatus.DidNotFinish or RaceParticipantStatus.Disqualified))
                    participant.Status = phase == RaceSessionPhase.Race
                        ? RaceParticipantStatus.DidNotFinish
                        : RaceParticipantStatus.Disconnected;
                participant.FinishedAt ??= phase == RaceSessionPhase.Race ? now : null;
                participant.AutomaticYellowActive = false;
                participant.HazardCandidateStartedAt = null;
                participant.HazardRecoveryStartedAt = null;
                participant.QualifyingFinalLapPending = false;
                participant.PracticeFinalLapPending = false;
                CompleteQualifyingIfReady(now);
                CompletePracticeIfReady(now);
                RefreshYellowFlag(now);
                TryCompleteRaceIfReady(now);
                IncrementRevision();
                snapshot = BuildSnapshot(now);
                audit = new RaceAuditEntry(
                    snapshot.ServerTime,
                    "participantRemoved",
                    $"赛事总控断开了 {releasedName}，显示名称已释放。",
                    participant.Id);
            }
            else
            {
                var observer = observers.FirstOrDefault(candidate => candidate.Id == clientId);
                if (observer is null) return RaceCommandResult.Reject("客户端不存在或已经离开房间。");
                revokedResumeTokens.Add(observer.ResumeToken);
                observers.Remove(observer);
                IncrementRevision();
                snapshot = BuildSnapshot(now);
                audit = new RaceAuditEntry(
                    snapshot.ServerTime,
                    "observerRemoved",
                    $"赛事总控断开了 OB {observer.DisplayName}，显示名称已释放。",
                    observer.Id);
            }
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
                var sessionLabel = QualifyingSessionLabel();
                banner = NewBanner(
                    RaceBannerKind.ChequeredFlag,
                    qualifyingSessionCount == 1 ? "排位计时结束" : $"{sessionLabel} 计时结束",
                    pending == 0 ? "成绩已冻结" : $"{pending} 名车手可完成已经开始的最后一圈",
                    null,
                    TimeSpan.FromSeconds(8));
                CompleteQualifyingIfReady(now);
                IncrementRevision();
                snapshot = BuildSnapshot(now);
                audit = new RaceAuditEntry(
                    now,
                    "qualifyingEnded",
                    pending == 0
                        ? $"{sessionLabel} 计时结束，成绩已冻结。"
                        : $"{sessionLabel} 计时结束，等待 {pending} 名车手完成最后一圈。");
            }
            else if (phase == RaceSessionPhase.Practice && !practiceTimeExpired &&
                     practiceEndsAt is DateTimeOffset practiceEnding && now >= practiceEnding)
            {
                flag = RaceControlFlag.Chequered;
                practiceTimeExpired = true;
                foreach (var participant in participants)
                    participant.PracticeFinalLapPending = IsEligibleForPracticeFinalLap(participant);
                var pending = participants.Count(participant => participant.PracticeFinalLapPending);
                var sessionLabel = PracticeSessionLabel();
                banner = NewBanner(
                    RaceBannerKind.ChequeredFlag,
                    $"{sessionLabel} 计时结束",
                    pending == 0 ? "本节成绩已冻结" : $"{pending} 名车手可完成已经开始的最后一圈",
                    null,
                    TimeSpan.FromSeconds(8));
                CompletePracticeIfReady(now);
                IncrementRevision();
                snapshot = BuildSnapshot(now);
                audit = new RaceAuditEntry(
                    now,
                    "practiceEnded",
                    pending == 0
                        ? $"{sessionLabel} 计时结束，本节成绩已冻结。"
                        : $"{sessionLabel} 计时结束，等待 {pending} 名车手完成最后一圈。");
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
            false,
            false,
            true);
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

    private bool CanExecutePenalties(ParticipantState participant) =>
        phase == RaceSessionPhase.Race &&
        flag != RaceControlFlag.Chequered &&
        participant.Status is not (RaceParticipantStatus.Finished or RaceParticipantStatus.DidNotFinish or
            RaceParticipantStatus.Disqualified or RaceParticipantStatus.Disconnected);

    private bool RequiresPostRaceAdjustment(ParticipantState participant) =>
        phase == RaceSessionPhase.Finished ||
        phase == RaceSessionPhase.Race && flag == RaceControlFlag.Chequered ||
        participant.Status is RaceParticipantStatus.Finished or RaceParticipantStatus.DidNotFinish or
            RaceParticipantStatus.Disqualified;

    private static void ResetLivePenaltyServiceState(ParticipantState participant)
    {
        participant.PenaltyServiceActive = false;
        participant.PenaltyServiceAttempted = false;
        participant.PenaltyServiceElapsedSeconds = 0;
        participant.PenaltyServiceRequiredSeconds = 0;
        participant.PenaltyServiceLastUpdatedAt = null;
        participant.DriveThroughVisitActive = false;
        participant.DriveThroughStopCandidateStartedAt = null;
        participant.PitVisitHadServiceStop = false;
        participant.PitVisitPaused = false;
    }

    private void UpdatePitServiceState(ParticipantState participant, RaceTelemetryUpdate telemetry)
    {
        participant.IsInPitLane = telemetry.IsInPitLane;
        participant.IsInServiceZone = telemetry.IsInServiceZone;
        var serviceBlocked = CanExecutePenalties(participant) &&
                             (PendingTimePenaltySeconds(participant.Id) > 0 ||
                              participant.PenaltyServiceActive);
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
        if (RequiresPostRaceAdjustment(participant) ||
            phase == RaceSessionPhase.Race && totalRaceLaps - participant.CompletedLaps <= 3)
        {
            return new RacePenaltySnapshot(
                Guid.NewGuid(),
                participant.Id,
                RacePenaltyKind.Time,
                20,
                null,
                RequiresPostRaceAdjustment(participant)
                    ? $"赛后下发的通过维修区处罚，按等效规则改为完赛加时：{reason}"
                    : $"最后三圈下发的通过维修区处罚，按等效规则改为完赛加时：{reason}",
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

    private void FinalizePendingPenaltiesAtFinish(
        ParticipantState participant,
        DateTimeOffset now,
        ICollection<RaceAuditEntry> audits)
    {
        ResetLivePenaltyServiceState(participant);
        for (var index = 0; index < penalties.Count; index++)
        {
            var penalty = penalties[index];
            if (penalty.ParticipantId != participant.Id || penalty.IsRevoked || penalty.IsServed)
                continue;
            if (penalty.Kind == RacePenaltyKind.Time && !penalty.IsPostRaceAdjustment)
                penalties[index] = penalty with { IsPostRaceAdjustment = true };
        }

        if (HasPendingDriveThrough(participant.Id))
            ConvertDriveThroughToTimeAdjustment(
                participant,
                now,
                "车手已经接收方格旗，改按等效完赛加时结算",
                audits,
                announce: false);

        var pendingStopAndGo = penalties
            .Where(candidate => candidate.ParticipantId == participant.Id &&
                                !candidate.IsRevoked && !candidate.IsServed &&
                                candidate.Kind == RacePenaltyKind.StopAndGo)
            .ToArray();
        if (pendingStopAndGo.Length == 0) return;
        MarkPendingPenaltiesServed(participant.Id, RacePenaltyKind.StopAndGo);
        var equivalentSeconds = pendingStopAndGo.Sum(candidate => candidate.ValueSeconds ?? 0) + 20;
        penalties.Add(new RacePenaltySnapshot(
            Guid.NewGuid(),
            participant.Id,
            RacePenaltyKind.Time,
            equivalentSeconds,
            null,
            "未执行的停车并通过维修区处罚，按维修区通行 20 秒加原停车时间计入完赛成绩",
            now,
            false,
            false,
            true,
            pendingStopAndGo.Any(candidate => candidate.IsAutomatic)));
        audits.Add(new RaceAuditEntry(
            now,
            "stopAndGoPostRaceAdjustment",
            $"{participant.DisplayName} 的未执行停车并通过维修区处罚已折算为 +{equivalentSeconds:0.#} 秒完赛加时。",
            participant.Id));
    }

    private void EnforceMinimumPitStopsAtFinish(
        ParticipantState participant,
        DateTimeOffset now,
        ICollection<RaceAuditEntry> audits)
    {
        if (minimumRequiredPitStops <= 0 ||
            participant.CompletedPitServices >= minimumRequiredPitStops)
            return;

        var reason =
            $"未完成规定的最少有效维修停留次数（{participant.CompletedPitServices}/{minimumRequiredPitStops}）。";
        var penalty = new RacePenaltySnapshot(
            Guid.NewGuid(),
            participant.Id,
            RacePenaltyKind.Disqualification,
            null,
            null,
            reason,
            now,
            false,
            false,
            true,
            true);
        penalties.Add(penalty);
        participant.Status = RaceParticipantStatus.Disqualified;
        audits.Add(new RaceAuditEntry(
            now,
            "minimumPitStopsNotMet",
            $"{participant.DisplayName} 完赛时只完成 {participant.CompletedPitServices}/{minimumRequiredPitStops} 次有效维修停留，判定未满足完赛条件。",
            participant.Id,
            penalty));
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
        ICollection<RaceAuditEntry> audits,
        bool announce = true)
    {
        if (!HasPendingDriveThrough(participant.Id)) return;
        var wasAutomatic = penalties.Any(candidate => candidate.ParticipantId == participant.Id &&
            candidate.Kind == RacePenaltyKind.DriveThrough && !candidate.IsRevoked && !candidate.IsServed &&
            candidate.IsAutomatic);
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
            true,
            wasAutomatic));
        participant.DriveThroughOverdue = true;
        participant.DriveThroughReminderAt = now;
        participant.DriveThroughVisitActive = false;
        participant.DriveThroughStopCandidateStartedAt = null;
        if (announce)
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
        var wasAutomatic = penalties.Any(candidate => candidate.ParticipantId == participant.Id &&
            candidate.Kind == RacePenaltyKind.Time && !candidate.IsRevoked && !candidate.IsServed &&
            candidate.IsAutomatic);
        MarkPendingPenaltiesServed(participant.Id, RacePenaltyKind.Time);
        RacePenaltySnapshot? replacement = null;
        if (!HasPendingDriveThrough(participant.Id))
        {
            replacement = CreateDriveThroughPenalty(
                participant,
                $"停车罚时执行失败：{reason}",
                now) with { IsAutomatic = wasAutomatic };
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

    private sealed record TrackLimitDecision(
        RacePenaltySnapshot? Penalty,
        RaceInvestigationSnapshot? Investigation);

    private static void AddTrackLimitAudit(
        ICollection<RaceAuditEntry> audits,
        DateTimeOffset now,
        ParticipantState participant,
        TrackLimitDecision? decision,
        string penaltyType)
    {
        if (decision?.Penalty is { } penalty)
            audits.Add(new RaceAuditEntry(
                now,
                penaltyType,
                $"{participant.DisplayName}：{PenaltyDescription(penalty)}。",
                participant.Id,
                penalty));
        else if (decision?.Investigation is { } investigation)
            audits.Add(new RaceAuditEntry(
                now,
                "investigationOpened",
                $"{participant.DisplayName} 正在接受调查：{investigation.Offense}（第 {investigation.LapNumber} 圈）。",
                participant.Id,
                investigation));
    }

    private TrackLimitDecision? EvaluateShortcut(
        ParticipantState participant,
        RaceTelemetryUpdate telemetry,
        DateTimeOffset now)
    {
        TrackLimitDecision? decision = null;
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
            var eligible = phase is RaceSessionPhase.Race or RaceSessionPhase.Practice or RaceSessionPhase.Qualifying &&
                           (phase != RaceSessionPhase.Qualifying || participant.QualifyingEligible) &&
                           !telemetry.IsInPitLane && !telemetry.IsInServiceZone && !telemetry.IsApproachingPit &&
                           !telemetry.IsOnPitRoute &&
                           participant.Status is not (RaceParticipantStatus.Finished or
                               RaceParticipantStatus.DidNotFinish or RaceParticipantStatus.Disqualified or
                               RaceParticipantStatus.Disconnected);
            if (eligible && elapsedSeconds is > 0 and <= 2 && progressDelta is > 0 and < 0.75 &&
                routeDistance > plausibleDistance && !participant.ShortcutPenaltyIssued)
            {
                participant.ShortcutPenaltyIssued = true;
                participant.TrackLimitSeverePenaltyIssued = true;
                decision = RegisterTrackLimitIncident(
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
        return decision;
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

    private void EvaluateCollisionInvestigations(
        ParticipantState participant,
        RaceTelemetryUpdate telemetry,
        DateTimeOffset now,
        ICollection<RaceAuditEntry> audits)
    {
        var incomingEvidenceIsNew = telemetry.ImpactSequence > participant.LastReportedImpactSequence;
        if (incomingEvidenceIsNew)
        {
            participant.LastReportedImpactSequence = telemetry.ImpactSequence;
            participant.LastImpactAt = now - TimeSpan.FromMilliseconds(telemetry.ImpactAgeMilliseconds);
            participant.LastImpactWorldX = telemetry.ImpactWorldX;
            participant.LastImpactWorldY = telemetry.ImpactWorldY;
            participant.LastImpactWorldZ = telemetry.ImpactWorldZ;
            participant.LastImpactMagnitudeMps = telemetry.ImpactMagnitudeMps;
            participant.LastImpactSpeedLossMps = telemetry.ImpactSpeedLossMps;
            participant.LastImpactSmashableVelDiff = telemetry.ImpactSmashableVelDiff;
            participant.LastImpactSmashableMass = telemetry.ImpactSmashableMass;
        }
        if (!automaticCollisionInvestigationsEnabled)
        {
            participant.LastProcessedImpactSequence = participant.LastReportedImpactSequence;
            return;
        }

        if (!incomingEvidenceIsNew || telemetry.ImpactSequence <= participant.LastProcessedImpactSequence)
            return;
        participant.LastProcessedImpactSequence = telemetry.ImpactSequence;

        if (phase != RaceSessionPhase.Race || flag == RaceControlFlag.Chequered ||
            !telemetry.HasWorldPosition ||
            telemetry.ImpactAgeMilliseconds < 0 ||
            telemetry.ImpactAgeMilliseconds > CollisionEvidenceLifetime.TotalMilliseconds ||
            telemetry.ImpactMagnitudeMps < MinimumCollisionImpactMagnitudeMps ||
            telemetry.ImpactSmashableVelDiff >= .2 || telemetry.ImpactSmashableMass >= .5 ||
            participant.IsInPitLane || participant.IsInServiceZone ||
            participant.IsApproachingPit || participant.IsOnPitRoute ||
            IsCollisionTerminal(participant))
            return;

        var incidentAt = now - TimeSpan.FromMilliseconds(telemetry.ImpactAgeMilliseconds);
        ParticipantState? nearest = null;
        CollisionPositionSample? nearestSample = null;
        var nearestHorizontalDistance = double.MaxValue;
        var nearestRelativeSpeed = 0d;
        var nearestApproachDistanceReduction = 0d;
        var nearestBothReportedImpact = false;
        foreach (var candidate in participants)
        {
            if (candidate.Id == participant.Id || !candidate.ReservationActive || !candidate.IsConnected ||
                !candidate.TelemetryValid || !candidate.HasWorldPosition ||
                candidate.IsInPitLane || candidate.IsInServiceZone ||
                candidate.IsApproachingPit || candidate.IsOnPitRoute ||
                IsCollisionTerminal(candidate) ||
                now - candidate.LastTelemetryReceivedAt > CollisionPeerFreshness)
                continue;

            if (ClosestCollisionPositionSample(candidate, incidentAt) is not CollisionPositionSample candidateSample)
                continue;
            var deltaX = telemetry.ImpactWorldX - candidateSample.WorldX;
            var deltaY = telemetry.ImpactWorldY - candidateSample.WorldY;
            var deltaZ = telemetry.ImpactWorldZ - candidateSample.WorldZ;
            var horizontalDistance = Math.Sqrt(deltaX * deltaX + deltaZ * deltaZ);
            if (horizontalDistance > MaximumCollisionHorizontalDistanceMeters ||
                Math.Abs(deltaY) > MaximumCollisionVerticalDistanceMeters)
                continue;

            var relativeSpeed = telemetry.HasWorldVelocity && candidateSample.HasWorldVelocity
                ? Math.Sqrt(
                    Math.Pow(telemetry.ImpactWorldVelocityX - candidateSample.WorldVelocityX, 2) +
                    Math.Pow(telemetry.ImpactWorldVelocityZ - candidateSample.WorldVelocityZ, 2))
                : 0;
            var bothReportedImpact = candidate.LastImpactAt is DateTimeOffset candidateImpactAt &&
                                     Math.Abs((candidateImpactAt - incidentAt).TotalMilliseconds) <=
                                         CollisionEvidenceLifetime.TotalMilliseconds &&
                                     candidate.LastImpactMagnitudeMps >= MinimumCollisionImpactMagnitudeMps &&
                                     candidate.LastImpactSmashableVelDiff < .2 &&
                                     candidate.LastImpactSmashableMass < .5;
            var approachDistanceReduction = CollisionApproachDistanceReduction(
                participant,
                candidate,
                incidentAt,
                horizontalDistance);
            var strongReporterEvidence =
                telemetry.ImpactMagnitudeMps >= StrongCollisionImpactMagnitudeMps ||
                telemetry.ImpactSpeedLossMps >= MinimumCollisionSpeedLossMps;
            var trajectoryConfirmed =
                approachDistanceReduction >= MinimumCollisionApproachMeters &&
                (relativeSpeed >= MinimumCollisionRelativeSpeedMps || strongReporterEvidence);
            var strongCloseContact = strongReporterEvidence &&
                                     relativeSpeed >= MinimumCollisionRelativeSpeedMps &&
                                     horizontalDistance <= 3.8;
            if (!bothReportedImpact && !trajectoryConfirmed && !strongCloseContact)
                continue;
            if (horizontalDistance >= nearestHorizontalDistance)
                continue;
            nearest = candidate;
            nearestSample = candidateSample;
            nearestHorizontalDistance = horizontalDistance;
            nearestRelativeSpeed = relativeSpeed;
            nearestApproachDistanceReduction = approachDistanceReduction;
            nearestBothReportedImpact = bothReportedImpact;
        }

        if (nearest is null || nearestSample is not CollisionPositionSample matchedSample) return;
        var pairKey = CollisionPairKey(participant.Id, nearest.Id);
        var lapNumber = Math.Max(1, Math.Max(participant.CompletedLaps, nearest.CompletedLaps) + 1);
        var currentEvidence = new RaceCollisionEvidenceSnapshot(
            incidentAt,
            participant.Id,
            nearest.Id,
            participant.DisplayName,
            nearest.DisplayName,
            participant.ThemeColor,
            nearest.ThemeColor,
            telemetry.ImpactWorldX,
            telemetry.ImpactWorldY,
            telemetry.ImpactWorldZ,
            matchedSample.WorldX,
            matchedSample.WorldY,
            matchedSample.WorldZ,
            telemetry.ImpactWorldVelocityX,
            telemetry.ImpactWorldVelocityZ,
            matchedSample.WorldVelocityX,
            matchedSample.WorldVelocityZ,
            nearestHorizontalDistance,
            Math.Abs(telemetry.ImpactWorldY - matchedSample.WorldY),
            nearestRelativeSpeed * 3.6,
            telemetry.ImpactMagnitudeMps,
            telemetry.ImpactSpeedLossMps,
            Math.Max(0, nearestApproachDistanceReduction),
            nearestBothReportedImpact,
            1,
            incidentAt);
        if (TryMergeCollisionInvestigation(pairKey, currentEvidence, lapNumber))
        {
            var groupedUntil = now + CollisionPairCooldown;
            participant.CollisionPairCooldowns[pairKey] = groupedUntil;
            nearest.CollisionPairCooldowns[pairKey] = groupedUntil;
            return;
        }
        if (investigations.Count(item => item.CollisionEvidence is not null) >=
            MaximumCollisionInvestigationsPerSession)
            return;
        if (participant.CollisionPairCooldowns.TryGetValue(pairKey, out var cooldownUntil) && cooldownUntil > now ||
            nearest.CollisionPairCooldowns.TryGetValue(pairKey, out cooldownUntil) && cooldownUntil > now)
            return;

        var nextAllowedAt = now + CollisionPairCooldown;
        participant.CollisionPairCooldowns[pairKey] = nextAllowedAt;
        nearest.CollisionPairCooldowns[pairKey] = nextAllowedAt;
        var related = new[] { participant.Id, nearest.Id };
        var investigation = new RaceInvestigationSnapshot(
            Guid.NewGuid(),
            participant.Id,
            CollisionOffense(currentEvidence),
            now,
            lapNumber,
            RaceInvestigationStatus.Pending,
            RelatedParticipantIds: related,
            CollisionEvidence: currentEvidence);
        investigations.Add(investigation);
        banner = NewBanner(
            RaceBannerKind.Information,
            "正在调查 · 疑似碰撞",
            $"{participant.DisplayName} ↔ {nearest.DisplayName} · 第 {lapNumber} 圈",
            null,
            TimeSpan.FromSeconds(8)) with { IsInvestigation = true };
        audits.Add(new RaceAuditEntry(
            now,
            "collisionInvestigationOpened",
            $"{participant.DisplayName} 与 {nearest.DisplayName} 发生疑似车辆接触，已交由总控调查（第 {lapNumber} 圈）。",
            participant.Id,
            investigation));
    }

    private bool TryMergeCollisionInvestigation(
        string pairKey,
        RaceCollisionEvidenceSnapshot current,
        int lapNumber)
    {
        for (var index = investigations.Count - 1; index >= 0; index--)
        {
            var existing = investigations[index];
            if (existing.Status != RaceInvestigationStatus.Pending ||
                existing.CollisionEvidence is not RaceCollisionEvidenceSnapshot previous ||
                CollisionPairKey(previous.ReporterParticipantId, previous.OtherParticipantId) != pairKey)
                continue;
            var previousLastAt = previous.LastIncidentAt ?? previous.IncidentAt;
            if (Math.Abs((current.IncidentAt - previousLastAt).TotalMilliseconds) >
                CollisionPairCooldown.TotalMilliseconds)
                continue;

            var useCurrentGeometry =
                current.ImpactMagnitudeMps > previous.ImpactMagnitudeMps ||
                current.HorizontalDistanceMeters < previous.HorizontalDistanceMeters;
            var geometry = useCurrentGeometry ? current : previous;
            var firstAt = current.IncidentAt < previous.IncidentAt
                ? current.IncidentAt
                : previous.IncidentAt;
            var lastAt = current.IncidentAt > previousLastAt
                ? current.IncidentAt
                : previousLastAt;
            var merged = geometry with
            {
                IncidentAt = firstAt,
                LastIncidentAt = lastAt,
                ContactCount = Math.Clamp(Math.Max(1, previous.ContactCount) + 1, 1, 99),
                HorizontalDistanceMeters = Math.Min(
                    previous.HorizontalDistanceMeters,
                    current.HorizontalDistanceMeters),
                VerticalDistanceMeters = Math.Min(
                    previous.VerticalDistanceMeters,
                    current.VerticalDistanceMeters),
                RelativeSpeedKph = Math.Max(previous.RelativeSpeedKph, current.RelativeSpeedKph),
                ImpactMagnitudeMps = Math.Max(
                    previous.ImpactMagnitudeMps,
                    current.ImpactMagnitudeMps),
                ImpactSpeedLossMps = Math.Max(
                    previous.ImpactSpeedLossMps,
                    current.ImpactSpeedLossMps),
                ApproachDistanceReductionMeters = Math.Max(
                    previous.ApproachDistanceReductionMeters,
                    current.ApproachDistanceReductionMeters),
                BothDriversReportedImpact =
                    previous.BothDriversReportedImpact || current.BothDriversReportedImpact
            };
            investigations[index] = existing with
            {
                Offense = CollisionOffense(merged),
                LapNumber = Math.Min(existing.LapNumber, lapNumber),
                CollisionEvidence = merged
            };
            return true;
        }
        return false;
    }

    private static string CollisionOffense(RaceCollisionEvidenceSnapshot evidence)
    {
        var count = Math.Max(1, evidence.ContactCount);
        var lastAt = evidence.LastIncidentAt ?? evidence.IncidentAt;
        var durationSeconds = Math.Max(0, (lastAt - evidence.IncidentAt).TotalSeconds);
        var prefix = count > 1
            ? $"连续疑似车辆接触（{count} 次，{durationSeconds:0.0} 秒内）"
            : "疑似车辆接触";
        return
            $"{prefix}：{evidence.ReporterName} 与 {evidence.OtherName}；" +
            $"最近距离 {evidence.HorizontalDistanceMeters:0.0} m，运动突变 " +
            $"{evidence.ImpactMagnitudeMps:0.0} m/s，相对速度 {evidence.RelativeSpeedKph:0} km/h，" +
            $"接触前距离收窄 {Math.Max(0, evidence.ApproachDistanceReductionMeters):0.0} m。" +
            "仅供总控结合画面核查，不代表责任判定";
    }

    private static void RecordCollisionPositionSample(
        ParticipantState participant,
        RaceTelemetryUpdate telemetry,
        DateTimeOffset now)
    {
        if (!telemetry.HasWorldPosition) return;
        var samples = participant.CollisionPositionSamples;
        samples.Add(new CollisionPositionSample(
            now,
            telemetry.WorldX,
            telemetry.WorldY,
            telemetry.WorldZ,
            telemetry.HasWorldVelocity,
            telemetry.WorldVelocityX,
            telemetry.WorldVelocityY,
            telemetry.WorldVelocityZ));
        var minimumAt = now - CollisionTrajectoryLifetime;
        var removeCount = 0;
        while (removeCount < samples.Count && samples[removeCount].At < minimumAt)
            removeCount++;
        if (removeCount > 0) samples.RemoveRange(0, removeCount);
        if (samples.Count > 32) samples.RemoveRange(0, samples.Count - 32);
    }

    private static CollisionPositionSample? ClosestCollisionPositionSample(
        ParticipantState participant,
        DateTimeOffset target)
    {
        CollisionPositionSample? nearest = null;
        var nearestDifference = double.MaxValue;
        foreach (var sample in participant.CollisionPositionSamples)
        {
            var difference = Math.Abs((sample.At - target).TotalMilliseconds);
            if (difference >= nearestDifference) continue;
            nearest = sample;
            nearestDifference = difference;
        }
        return nearestDifference <= CollisionTrajectoryMatchTolerance.TotalMilliseconds ? nearest : null;
    }

    private static double CollisionApproachDistanceReduction(
        ParticipantState reporter,
        ParticipantState other,
        DateTimeOffset incidentAt,
        double incidentDistance)
    {
        var lookbackAt = incidentAt - CollisionApproachLookback;
        var reporterBefore = ClosestCollisionPositionSample(reporter, lookbackAt);
        var otherBefore = ClosestCollisionPositionSample(other, lookbackAt);
        if (reporterBefore is not CollisionPositionSample left ||
            otherBefore is not CollisionPositionSample right)
            return 0;
        var beforeDistance = Math.Sqrt(
            Math.Pow(left.WorldX - right.WorldX, 2) +
            Math.Pow(left.WorldZ - right.WorldZ, 2));
        return beforeDistance - incidentDistance;
    }

    private static bool IsCollisionTerminal(ParticipantState participant) =>
        participant.Status is RaceParticipantStatus.Finished or RaceParticipantStatus.DidNotFinish or
            RaceParticipantStatus.Disqualified or RaceParticipantStatus.Disconnected;

    private static string CollisionPairKey(Guid left, Guid right) =>
        left.CompareTo(right) <= 0 ? $"{left:N}:{right:N}" : $"{right:N}:{left:N}";

    private TrackLimitDecision? EvaluateTrackLimits(
        ParticipantState participant,
        RaceTelemetryUpdate telemetry,
        DateTimeOffset now)
    {
        if (phase is not (RaceSessionPhase.Race or RaceSessionPhase.Practice or RaceSessionPhase.Qualifying) ||
            phase == RaceSessionPhase.Qualifying && !participant.QualifyingEligible ||
            telemetry.IsInPitLane || telemetry.IsInServiceZone || telemetry.IsApproachingPit ||
            telemetry.IsOnPitRoute ||
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
            if (trackLimitMode != TrackLimitEnforcementMode.Disabled)
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

    private TrackLimitDecision? RegisterTrackLimitIncident(
        ParticipantState participant,
        bool severe,
        double gainedDistanceMeters,
        string evidence,
        DateTimeOffset now)
    {
        if (trackLimitMode == TrackLimitEnforcementMode.Disabled) return null;

        participant.LapHasTrackLimitIncident = true;
        participant.TrackLimitWarnings++;
        if (trackLimitMode == TrackLimitEnforcementMode.WarningsOnly)
        {
            var investigation = new RaceInvestigationSnapshot(
                Guid.NewGuid(),
                participant.Id,
                $"疑似切弯获利：{evidence}",
                now,
                Math.Max(1, participant.CompletedLaps + 1),
                RaceInvestigationStatus.Pending);
            investigations.Add(investigation);
            banner = NewBanner(
                RaceBannerKind.Information,
                $"正在调查 · {participant.DisplayName}",
                $"{investigation.Offense} · 第 {investigation.LapNumber} 圈 · {now:HH:mm:ss}",
                participant.Id,
                TimeSpan.FromSeconds(8)) with { IsInvestigation = true };
            return new TrackLimitDecision(null, investigation);
        }

        if (severe)
            return new TrackLimitDecision(AddAutomaticTrackLimitPenalty(
                participant,
                RacePenaltyKind.Time,
                5,
                $"严重切弯：{evidence}",
                now), null);

        if (participant.TrackLimitWarnings <= 3)
            return new TrackLimitDecision(AddAutomaticTrackLimitPenalty(
                participant,
                RacePenaltyKind.Warning,
                null,
                $"轻微切弯获利：{evidence}（警告 {participant.TrackLimitWarnings}/3）",
                now), null);

        participant.TrackLimitWarnings = 0;
        return new TrackLimitDecision(AddAutomaticTrackLimitPenalty(
            participant,
            RacePenaltyKind.Time,
            5,
            $"轻微切弯警告累计超过 3 次：{evidence}",
            now), null);
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
            false,
            false,
            true);
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
        participant.QualifyingEligible &&
        participant.IsConnected &&
        participant.TelemetryValid &&
        !participant.IsInPitLane &&
        !participant.IsInServiceZone &&
        participant.CurrentLapSeconds > 0.05 &&
        participant.Status is not (RaceParticipantStatus.Disqualified or
            RaceParticipantStatus.DidNotFinish or RaceParticipantStatus.Disconnected);

    private bool IsEligibleForPracticeFinalLap(ParticipantState participant) =>
        participant.IsConnected &&
        participant.TelemetryValid &&
        !participant.IsInPitLane &&
        !participant.IsInServiceZone &&
        participant.CurrentLapSeconds > 0.05 &&
        participant.Status is not (RaceParticipantStatus.Disqualified or
            RaceParticipantStatus.DidNotFinish or RaceParticipantStatus.Disconnected);

    private void CompletePracticeIfReady(DateTimeOffset now)
    {
        if (phase != RaceSessionPhase.Practice || !practiceTimeExpired ||
            practiceEndsAt is null ||
            participants.Any(participant => participant.PracticeFinalLapPending))
            return;

        CaptureCurrentPracticeResults();
        if (practiceSessionNumber < practiceSessionCount)
        {
            practiceEndsAt = null;
            foreach (var participant in participants.Where(candidate => candidate.IsConnected))
            {
                participant.PracticeFinalLapPending = false;
                participant.Status = RaceParticipantStatus.Ready;
            }
            banner = NewBanner(
                RaceBannerKind.Information,
                $"{PracticeSessionLabel()} 已结束",
                $"等待总控开启 FP{practiceSessionNumber + 1}",
                null,
                TimeSpan.FromSeconds(7));
            return;
        }

        practiceEndsAt = null;
        foreach (var participant in participants.Where(candidate => candidate.IsConnected))
        {
            participant.PracticeFinalLapPending = false;
            participant.Status = RaceParticipantStatus.Ready;
        }
        banner = NewBanner(
            RaceBannerKind.Information,
            $"{PracticeSessionLabel()} 已结束",
            "本节成绩已冻结，等待总控下发后续流程",
            null,
            TimeSpan.FromSeconds(7));
    }

    private bool TryStartNextPracticeSession(DateTimeOffset now)
    {
        if (phase != RaceSessionPhase.Practice ||
            practiceSessionCount <= 1 ||
            practiceSessionNumber >= practiceSessionCount ||
            !practiceTimeExpired ||
            practiceEndsAt is not null ||
            participants.Any(participant => participant.PracticeFinalLapPending))
            return false;

        practiceSessionNumber++;
        practiceEndsAt = now.AddMinutes(practiceSessionMinutes[practiceSessionNumber - 1]);
        practiceTimeExpired = false;
        flag = RaceControlFlag.Green;
        flagMessage = null;
        receivedLapEvents.Clear();
        foreach (var participant in participants)
            ResetForNextPracticeSession(participant);
        banner = NewBanner(
            RaceBannerKind.Information,
            $"{PracticeSessionLabel()} 开始",
            $"{practiceSessionMinutes[practiceSessionNumber - 1]} 分钟",
            null,
            TimeSpan.FromSeconds(7));
        return true;
    }

    private void ConfigurePractice(RaceAdminSessionCommand command)
    {
        practiceSessionCount = Math.Clamp(command.PracticeSessionCount ?? 1, 1, 3);
        practiceSessionMinutes = Enumerable.Range(0, practiceSessionCount)
            .Select(index => Math.Clamp(
                command.PracticeSessionMinutes?.ElementAtOrDefault(index) is > 0 and <= 180
                    ? command.PracticeSessionMinutes[index]
                    : 60,
                1,
                180))
            .ToArray();
    }

    private void CaptureCurrentPracticeResults()
    {
        if (practiceSessionNumber is < 1 or > 3) return;
        foreach (var participant in participants)
            participant.PracticeSessionBestLapSeconds[practiceSessionNumber - 1] = participant.BestLapSeconds;
    }

    private static void ResetForNextPracticeSession(ParticipantState participant)
    {
        ResetForNextQualifyingSession(participant);
        participant.PracticeFinalLapPending = false;
    }

    private string PracticeSessionLabel() => practiceSessionCount == 1
        ? "练习赛"
        : $"FP{Math.Clamp(practiceSessionNumber, 1, practiceSessionCount)}";

    private void CompleteQualifyingIfReady(DateTimeOffset now)
    {
        if (phase != RaceSessionPhase.Qualifying || !qualifyingTimeExpired ||
            qualifyingEndsAt is null ||
            participants.Any(participant => participant.QualifyingFinalLapPending))
            return;

        CaptureCurrentQualifyingResults();
        if (qualifyingSessionNumber < qualifyingSessionCount)
        {
            EliminateFromCurrentQualifyingSession();
            qualifyingEndsAt = null;
            flag = RaceControlFlag.Green;
            flagMessage = null;
            foreach (var participant in participants.Where(candidate => candidate.IsConnected))
            {
                participant.QualifyingFinalLapPending = false;
                participant.Status = RaceParticipantStatus.Ready;
            }
            banner = NewBanner(
                RaceBannerKind.Information,
                $"{QualifyingSessionLabel()} 已结束",
                $"本节淘汰 {qualifyingEliminationCounts[qualifyingSessionNumber - 1]} 人 · 等待总控开启 Q{qualifyingSessionNumber + 1}",
                null,
                TimeSpan.FromSeconds(7));
            return;
        }

        phase = RaceSessionPhase.Grid;
        flag = RaceControlFlag.Green;
        flagMessage = null;
        qualifyingEndsAt = null;
        qualifyingTimeExpired = false;
        foreach (var participant in participants.Where(candidate => candidate.IsConnected))
            participant.Status = RaceParticipantStatus.Ready;
    }

    private bool TryStartNextQualifyingSession(DateTimeOffset now)
    {
        if (phase != RaceSessionPhase.Qualifying ||
            qualifyingSessionCount <= 1 ||
            qualifyingSessionNumber >= qualifyingSessionCount ||
            !qualifyingTimeExpired ||
            qualifyingEndsAt is not null ||
            participants.Any(participant => participant.QualifyingFinalLapPending))
            return false;

        qualifyingSessionNumber++;
        qualifyingEndsAt = now.AddMinutes(qualifyingSessionMinutes[qualifyingSessionNumber - 1]);
        qualifyingTimeExpired = false;
        flag = RaceControlFlag.Green;
        flagMessage = null;
        receivedLapEvents.Clear();
        foreach (var participant in participants.Where(candidate => candidate.QualifyingEligible))
            ResetForNextQualifyingSession(participant);
        var eliminationText = qualifyingSessionNumber < qualifyingSessionCount
            ? $" · 本节淘汰 {qualifyingEliminationCounts[qualifyingSessionNumber - 1]} 人"
            : string.Empty;
        banner = NewBanner(
            RaceBannerKind.Information,
            $"{QualifyingSessionLabel()} 开始",
            $"{qualifyingSessionMinutes[qualifyingSessionNumber - 1]} 分钟{eliminationText}",
            null,
            TimeSpan.FromSeconds(7));
        return true;
    }

    private void ConfigureQualifying(RaceAdminSessionCommand command)
    {
        qualifyingSessionCount = Math.Clamp(command.QualifyingSessionCount ?? 1, 1, 3);
        if (qualifyingSessionCount == 1)
        {
            qualifyingSessionMinutes = [Math.Clamp(command.QualifyingMinutes ?? 10, 1, 180)];
            qualifyingEliminationCounts = [];
            return;
        }

        var defaults = qualifyingSessionCount == 2 ? new[] { 15, 12 } : new[] { 18, 15, 12 };
        qualifyingSessionMinutes = Enumerable.Range(0, qualifyingSessionCount)
            .Select(index => Math.Clamp(
                command.QualifyingSessionMinutes?.ElementAtOrDefault(index) is > 0 and <= 180
                    ? command.QualifyingSessionMinutes[index]
                    : defaults[index],
                1,
                180))
            .ToArray();
        var eligibleCount = participants.Count(candidate => candidate.IsConnected &&
            candidate.Status != RaceParticipantStatus.Disqualified);
        var defaultEliminations = DefaultQualifyingEliminations(eligibleCount, qualifyingSessionCount);
        var remaining = eligibleCount;
        var resolved = new int[qualifyingSessionCount - 1];
        for (var index = 0; index < resolved.Length; index++)
        {
            var requested = command.QualifyingEliminationCounts?.ElementAtOrDefault(index);
            var value = requested is >= 0 ? requested.Value : defaultEliminations[index];
            resolved[index] = Math.Clamp(value, 0, Math.Max(0, remaining - 1));
            remaining -= resolved[index];
        }
        qualifyingEliminationCounts = resolved;
    }

    public static IReadOnlyList<int> DefaultQualifyingEliminations(int participantCount, int sessionCount)
    {
        var total = Math.Clamp(participantCount, 0, RaceProtocol.MaximumParticipants);
        var count = Math.Clamp(sessionCount, 1, 3);
        if (count == 1 || total <= 1) return Enumerable.Repeat(0, count - 1).ToArray();
        if (count == 2) return [Math.Max(0, total - Math.Max(1, (int)Math.Ceiling(total / 2d)))];
        var finalists = total <= 1 ? total : Math.Max(2, (int)Math.Ceiling(total / 2d));
        var eliminated = Math.Max(0, total - finalists);
        var q1 = (int)Math.Ceiling(eliminated / 2d);
        return [q1, eliminated - q1];
    }

    private void CaptureCurrentQualifyingResults()
    {
        if (qualifyingSessionNumber is < 1 or > 3) return;
        foreach (var participant in participants.Where(candidate => candidate.QualifyingEligible))
            participant.QualifyingSessionBestLapSeconds[qualifyingSessionNumber - 1] = participant.BestLapSeconds;
    }

    private void EliminateFromCurrentQualifyingSession()
    {
        if (qualifyingSessionNumber < 1 || qualifyingSessionNumber >= qualifyingSessionCount) return;
        var eliminate = qualifyingEliminationCounts.ElementAtOrDefault(qualifyingSessionNumber - 1);
        if (eliminate <= 0) return;
        var candidates = participants
            .Where(candidate => candidate.QualifyingEligible)
            .OrderBy(candidate => candidate.BestLapSeconds is null)
            .ThenBy(candidate => candidate.BestLapSeconds)
            .ThenBy(candidate => candidate.JoinedAt)
            .ToArray();
        foreach (var participant in candidates.TakeLast(Math.Min(eliminate, Math.Max(0, candidates.Length - 1))))
        {
            participant.QualifyingEligible = false;
            participant.QualifyingEliminatedInSession = qualifyingSessionNumber;
            participant.QualifyingFinalLapPending = false;
            participant.Status = participant.IsConnected
                ? RaceParticipantStatus.Ready
                : RaceParticipantStatus.Disconnected;
            participant.AutomaticYellowActive = false;
            participant.HazardCandidateStartedAt = null;
            participant.HazardRecoveryStartedAt = null;
        }
    }

    private static void ResetForNextQualifyingSession(ParticipantState participant)
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
        participant.QualifyingFinalLapPending = false;
        participant.LapHasTrackLimitIncident = false;
        participant.ProgressContinuityReady = false;
        participant.LastTelemetryMonotonicMilliseconds = 0;
        participant.LastContinuityProgress = 0;
        participant.ShortcutPenaltyIssued = false;
        ResetCollisionState(participant);
        participant.Status = participant.IsConnected
            ? RaceParticipantStatus.OnTrack
            : RaceParticipantStatus.Disconnected;
    }

    private string QualifyingSessionLabel() => qualifyingSessionCount == 1
        ? "排位赛"
        : $"Q{Math.Clamp(qualifyingSessionNumber, 1, qualifyingSessionCount)}";

    private void EvaluateAutomaticYellow(
        ParticipantState participant,
        DateTimeOffset now,
        bool isOnPitRoute = false)
    {
        if (!automaticYellowEnabled ||
            phase is not (RaceSessionPhase.Race or RaceSessionPhase.Practice or RaceSessionPhase.Qualifying) ||
            phase == RaceSessionPhase.Qualifying && !participant.QualifyingEligible ||
            participant.IsInPitLane || participant.IsInServiceZone || isOnPitRoute ||
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

    private bool TryCompleteRaceIfReady(DateTimeOffset now)
    {
        if (phase != RaceSessionPhase.Race || flag != RaceControlFlag.Chequered) return false;
        var awaitingFinish = participants.Any(candidate =>
            candidate.IsConnected &&
            candidate.Status is not (RaceParticipantStatus.Finished or
                RaceParticipantStatus.DidNotFinish or RaceParticipantStatus.Disqualified or
                RaceParticipantStatus.Disconnected));
        if (awaitingFinish) return false;

        phase = RaceSessionPhase.Finished;
        raceEndedAt = now;
        var winner = OrderParticipants(now).FirstOrDefault(candidate =>
            candidate.ReservationActive && candidate.Status == RaceParticipantStatus.Finished);
        if (winner is not null)
            banner = NewBanner(
                RaceBannerKind.Winner,
                "比赛胜者",
                $"{winner.DisplayName}  {FormatRaceTime(AdjustedRaceTotalSeconds(winner, now))}",
                winner.Id,
                null);
        return true;
    }

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
        if (!IsRaceClassificationPhase(phase)) return null;
        if (reference.Status == RaceParticipantStatus.Finished &&
            participant.Status == RaceParticipantStatus.Finished)
            return AdjustedRaceTotalSeconds(participant, now) - AdjustedRaceTotalSeconds(reference, now);
        if (LiveRaceDeltaSeconds(reference, participant) is double liveDelta)
            return liveDelta;
        if (reference.CompletedLaps != participant.CompletedLaps) return null;
        if (reference.LastLapCompletedAt is DateTimeOffset referenceCrossing &&
            participant.LastLapCompletedAt is DateTimeOffset participantCrossing)
            return (participantCrossing - referenceCrossing).TotalSeconds;
        return null;
    }

    private void RecordRaceProgressSample(
        ParticipantState participant,
        DateTimeOffset now,
        double? exactDistanceLaps = null)
    {
        var distance = exactDistanceLaps ?? participant.CompletedLaps + participant.TrackProgress;
        if (!double.IsFinite(distance) || distance < 0) return;
        var elapsedSeconds = RaceElapsedSeconds(now);
        var samples = participant.RaceProgressSamples;
        if (samples.Count > 0)
        {
            var last = samples[^1];
            if (distance < last.DistanceLaps - LiveGapProgressJitter)
                return;
            if (distance <= last.DistanceLaps + LiveGapProgressJitter)
            {
                samples[^1] = new RaceProgressSample(
                    Math.Max(distance, last.DistanceLaps),
                    elapsedSeconds);
                return;
            }
        }

        samples.Add(new RaceProgressSample(distance, elapsedSeconds));
        var minimumDistance = distance - LiveGapHistoryLaps;
        var removeCount = 0;
        while (removeCount < samples.Count - 2 && samples[removeCount].DistanceLaps < minimumDistance)
            removeCount++;
        if (removeCount > 0) samples.RemoveRange(0, removeCount);
        if (samples.Count > MaximumLiveGapSamples)
            samples.RemoveRange(0, samples.Count - MaximumLiveGapSamples);
    }

    private static double? EstimatePassageTime(
        IReadOnlyList<RaceProgressSample> samples,
        double distanceLaps)
    {
        if (samples.Count == 0 ||
            distanceLaps < samples[0].DistanceLaps - LiveGapProgressJitter ||
            distanceLaps > samples[^1].DistanceLaps + LiveGapProgressJitter)
            return null;

        var lower = 0;
        var upper = samples.Count - 1;
        while (lower < upper)
        {
            var middle = lower + (upper - lower) / 2;
            if (samples[middle].DistanceLaps < distanceLaps) lower = middle + 1;
            else upper = middle;
        }

        var next = samples[lower];
        if (Math.Abs(next.DistanceLaps - distanceLaps) <= LiveGapProgressJitter)
            return next.ElapsedSeconds;
        if (lower == 0) return null;
        var previous = samples[lower - 1];
        var span = next.DistanceLaps - previous.DistanceLaps;
        if (span <= 0) return previous.ElapsedSeconds;
        var fraction = Math.Clamp((distanceLaps - previous.DistanceLaps) / span, 0, 1);
        return previous.ElapsedSeconds + (next.ElapsedSeconds - previous.ElapsedSeconds) * fraction;
    }

    private static double? LiveRaceDeltaSeconds(
        ParticipantState reference,
        ParticipantState participant)
    {
        var referenceSamples = reference.RaceProgressSamples;
        var participantSamples = participant.RaceProgressSamples;
        if (referenceSamples.Count == 0 || participantSamples.Count == 0) return null;

        var referenceDistance = referenceSamples[^1].DistanceLaps;
        var participantDistance = participantSamples[^1].DistanceLaps;
        if (referenceDistance - participantDistance >= MaximumLiveGapDistanceLaps) return null;
        var commonDistance = Math.Min(referenceDistance, participantDistance);
        if (commonDistance < Math.Max(referenceSamples[0].DistanceLaps, participantSamples[0].DistanceLaps))
            return null;
        var referenceTime = EstimatePassageTime(referenceSamples, commonDistance);
        var participantTime = EstimatePassageTime(participantSamples, commonDistance);
        if (referenceTime is null || participantTime is null) return null;
        return Math.Max(0, participantTime.Value - referenceTime.Value);
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
        var ordered = OrderParticipants(now)
            .Where(candidate => candidate.ReservationActive)
            .ToList();
        var snapshots = new List<RaceParticipantSnapshot>(ordered.Count);
        var leader = ordered.FirstOrDefault();
        ParticipantState? prior = null;
        for (var index = 0; index < ordered.Count; index++)
        {
            var participant = ordered[index];
            var displayedBestLap = QualifyingDisplayedBestLap(participant);
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
            if (phase is RaceSessionPhase.Practice or RaceSessionPhase.Qualifying or RaceSessionPhase.Grid)
            {
                var leaderBestLap = leader is null ? null : QualifyingDisplayedBestLap(leader);
                var priorBestLap = prior is null ? null : QualifyingDisplayedBestLap(prior);
                gapToLeader = displayedBestLap is double value && leaderBestLap is double leaderValue
                    ? value - leaderValue
                    : null;
                interval = displayedBestLap is double intervalValue && priorBestLap is double priorValue
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
                displayedBestLap,
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
                participant.DriveThroughVisitActive && participant.IsInPitLane,
                participant.QualifyingEligible,
                participant.QualifyingEliminatedInSession,
                participant.QualifyingSessionBestLapSeconds.ToArray(),
                participant.PracticeFinalLapPending,
                participant.PracticeSessionBestLapSeconds.ToArray()));
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
            fastest?.Participant.BestLapSectorSeconds.ToArray(),
            null,
            null,
            null,
            penalties.OrderBy(candidate => candidate.IssuedAt).ToArray(),
            investigations.OrderBy(candidate => candidate.DetectedAt).ToArray(),
            qualifyingSessionNumber,
            qualifyingSessionCount,
            qualifyingSessionMinutes,
            qualifyingEliminationCounts,
            practiceEndsAt,
            practiceTimeExpired,
            practiceSessionNumber,
            practiceSessionCount,
            practiceSessionMinutes,
            observers
                .OrderBy(candidate => candidate.ConnectedAt)
                .Select(candidate => new RaceObserverSnapshot(
                    candidate.Id,
                    candidate.DisplayName,
                    candidate.ConnectedAt))
                .ToArray(),
            minimumRequiredPitStops);
    }

    private List<ParticipantState> OrderParticipants(DateTimeOffset now)
    {
        var activeParticipants = participants.Where(candidate => candidate.ReservationActive);
        if ((phase is RaceSessionPhase.Qualifying or RaceSessionPhase.Grid) && qualifyingSessionCount > 1)
            return activeParticipants
                .OrderBy(QualifyingClassificationGroup)
                .ThenBy(candidate => QualifyingDisplayedBestLap(candidate) is null)
                .ThenBy(QualifyingDisplayedBestLap)
                .ThenBy(candidate => candidate.JoinedAt)
                .ToList();

        if (phase is RaceSessionPhase.Practice or RaceSessionPhase.Qualifying or RaceSessionPhase.Grid)
            return activeParticipants
                .OrderBy(candidate => candidate.BestLapSeconds is null)
                .ThenBy(candidate => candidate.BestLapSeconds)
                .ThenBy(candidate => candidate.JoinedAt)
                .ToList();

        if (phase is RaceSessionPhase.OutLap or RaceSessionPhase.FormationLap or
            RaceSessionPhase.Race or RaceSessionPhase.Countdown or
            RaceSessionPhase.Suspended or RaceSessionPhase.Finished)
            return activeParticipants
                .OrderBy(candidate => TerminalRank(candidate.Status))
                .ThenByDescending(candidate => candidate.CompletedLaps)
                .ThenBy(candidate => candidate.Status == RaceParticipantStatus.Finished
                    ? AdjustedRaceTotalSeconds(candidate, now)
                    : double.MaxValue)
                .ThenByDescending(candidate => candidate.TrackProgress)
                .ThenBy(candidate => candidate.JoinedAt)
                .ToList();

        return activeParticipants
            .OrderByDescending(candidate => candidate.IsReady)
            .ThenBy(candidate => candidate.JoinedAt)
            .ToList();
    }

    private int QualifyingClassificationGroup(ParticipantState participant) =>
        participant.QualifyingEliminatedInSession is int eliminatedIn
            ? qualifyingSessionCount - eliminatedIn
            : 0;

    private double? QualifyingDisplayedBestLap(ParticipantState participant)
    {
        if (qualifyingSessionCount <= 1 || phase is not (RaceSessionPhase.Qualifying or RaceSessionPhase.Grid))
            return participant.BestLapSeconds;
        if (participant.QualifyingEliminatedInSession is int eliminatedIn)
            return participant.QualifyingSessionBestLapSeconds.ElementAtOrDefault(eliminatedIn - 1);
        return participant.BestLapSeconds ??
               participant.QualifyingSessionBestLapSeconds.ElementAtOrDefault(
                   Math.Clamp(qualifyingSessionNumber - 1, 0, 2));
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
        practiceEndsAt = null;
        qualifyingSessionNumber = 0;
        practiceSessionNumber = 0;
        illuminatedStartLights = 0;
        startLightsOut = false;
        qualifyingTimeExpired = false;
        practiceTimeExpired = false;
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
            participant.RaceProgressSamples.Clear();
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
            participant.QualifyingEligible = true;
            participant.QualifyingEliminatedInSession = null;
            Array.Fill(participant.QualifyingSessionBestLapSeconds, null);
            participant.PracticeFinalLapPending = false;
            Array.Fill(participant.PracticeSessionBestLapSeconds, null);
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
            ResetCollisionState(participant);
            ResetPenaltyServiceState(participant);
            participant.Status = participant.IsConnected ? RaceParticipantStatus.OnTrack : RaceParticipantStatus.Disconnected;
        }
    }

    private void ResetCompetitiveState(bool clearParticipants)
    {
        ClearYellowState();
        chequeredImminent = false;
        penalties.Clear();
        investigations.Clear();
        receivedLapEvents.Clear();
        startsAt = null;
        startSequenceAt = null;
        raceSuspendedAt = null;
        raceSuspendedDuration = TimeSpan.Zero;
        raceEndedAt = null;
        qualifyingEndsAt = null;
        practiceEndsAt = null;
        qualifyingSessionNumber = 0;
        qualifyingSessionCount = 1;
        qualifyingSessionMinutes = [10];
        qualifyingEliminationCounts = [];
        practiceSessionNumber = 0;
        practiceSessionCount = 1;
        practiceSessionMinutes = [60];
        illuminatedStartLights = 0;
        startLightsOut = false;
        qualifyingTimeExpired = false;
        practiceTimeExpired = false;
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
            participant.RaceProgressSamples.Clear();
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
            participant.QualifyingEligible = true;
            participant.QualifyingEliminatedInSession = null;
            Array.Fill(participant.QualifyingSessionBestLapSeconds, null);
            participant.PracticeFinalLapPending = false;
            Array.Fill(participant.PracticeSessionBestLapSeconds, null);
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
            ResetCollisionState(participant);
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

    private static void ResetCollisionState(ParticipantState participant)
    {
        participant.TelemetryValid = false;
        participant.HasWorldPosition = false;
        participant.IsApproachingPit = false;
        participant.IsOnPitRoute = false;
        participant.LastTelemetryReceivedAt = default;
        participant.LastProcessedImpactSequence = participant.LastReportedImpactSequence;
        participant.LastImpactAt = null;
        participant.LastImpactMagnitudeMps = 0;
        participant.LastImpactSpeedLossMps = 0;
        participant.LastImpactSmashableVelDiff = 0;
        participant.LastImpactSmashableMass = 0;
        participant.CollisionPositionSamples.Clear();
        participant.CollisionPairCooldowns.Clear();
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
            .Where(candidate => candidate.ReservationActive)
            .Where(candidate => phase != RaceSessionPhase.Qualifying || candidate.QualifyingEligible)
            .Where(candidate => candidate.BestLapSeconds is not null)
            .OrderBy(candidate => candidate.BestLapSeconds)
            .ThenBy(candidate => candidate.JoinedAt)
            .FirstOrDefault();
        return fastest?.BestLapSeconds is double time ? (fastest, time) : null;
    }

    private IReadOnlyList<double?> FastestSectors()
    {
        var activeParticipants = participants.Where(candidate => candidate.ReservationActive).ToArray();
        var count = activeParticipants.Length == 0
            ? 0
            : activeParticipants.Max(candidate => candidate.BestSectorSeconds.Count);
        var result = new double?[count];
        for (var index = 0; index < count; index++)
        {
            var candidates = activeParticipants
                .Where(candidate => phase != RaceSessionPhase.Qualifying || candidate.QualifyingEligible)
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
            candidate.ReservationActive &&
            candidate.Id != exceptParticipantId &&
            string.Equals(candidate.TeamId, teamId, StringComparison.OrdinalIgnoreCase)) < driversPerTeam;

    private RaceTeamDefinition? SelectLegacyTeam(Guid? exceptParticipantId)
        => teams
            .Select((team, index) => new
            {
                Team = team,
                Index = index,
                Members = participants.Count(candidate =>
                    candidate.ReservationActive &&
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
            : participants.FirstOrDefault(candidate =>
                candidate.ReservationActive && ConstantTimeEquals(candidate.ResumeToken, resumeToken));

    private ObserverState? FindObserverByResumeToken(string? resumeToken) =>
        string.IsNullOrWhiteSpace(resumeToken)
            ? null
            : observers.FirstOrDefault(candidate => ConstantTimeEquals(candidate.ResumeToken, resumeToken));

    private bool HasDuplicateName(string displayName, Guid? exceptParticipantId) =>
        participants.Any(candidate =>
            candidate.ReservationActive &&
            candidate.Id != exceptParticipantId &&
            string.Equals(candidate.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)) ||
        observers.Any(candidate =>
            candidate.Id != exceptParticipantId &&
            string.Equals(candidate.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static bool ConstantTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Hash(left), Hash(right));

    private void IncrementRevision() => revision++;

    private bool ShouldPublishTelemetrySnapshot(bool important)
    {
        var timestamp = Stopwatch.GetTimestamp();
        if (!important && lastTelemetrySnapshotTimestamp != 0 &&
            Stopwatch.GetElapsedTime(lastTelemetrySnapshotTimestamp, timestamp) < MinimumTelemetrySnapshotInterval)
            return false;
        lastTelemetrySnapshotTimestamp = timestamp;
        return true;
    }

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

    private sealed class ObserverState(
        Guid id,
        string resumeToken,
        string displayName,
        DateTimeOffset connectedAt)
    {
        public Guid Id { get; } = id;
        public string ResumeToken { get; } = resumeToken;
        public string DisplayName { get; set; } = displayName;
        public DateTimeOffset ConnectedAt { get; } = connectedAt;
    }

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
        public bool ReservationActive { get; set; } = true;
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
        public bool HasWorldPosition { get; set; }
        public double WorldX { get; set; }
        public double WorldY { get; set; }
        public double WorldZ { get; set; }
        public double VelocityX { get; set; }
        public double VelocityY { get; set; }
        public double VelocityZ { get; set; }
        public DateTimeOffset LastTelemetryReceivedAt { get; set; }
        public bool IsApproachingPit { get; set; }
        public bool IsOnPitRoute { get; set; }
        public long LastReportedImpactSequence { get; set; }
        public long LastProcessedImpactSequence { get; set; }
        public DateTimeOffset? LastImpactAt { get; set; }
        public double LastImpactWorldX { get; set; }
        public double LastImpactWorldY { get; set; }
        public double LastImpactWorldZ { get; set; }
        public double LastImpactMagnitudeMps { get; set; }
        public double LastImpactSpeedLossMps { get; set; }
        public double LastImpactSmashableVelDiff { get; set; }
        public double LastImpactSmashableMass { get; set; }
        public List<CollisionPositionSample> CollisionPositionSamples { get; } = [];
        public Dictionary<string, DateTimeOffset> CollisionPairCooldowns { get; } = [];
        public double CurrentLapSeconds { get; set; }
        public double? LastLapSeconds { get; set; }
        public double? BestLapSeconds { get; set; }
        public DateTimeOffset? LastLapCompletedAt { get; set; }
        public List<RaceProgressSample> RaceProgressSamples { get; } = [];
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
        public bool QualifyingEligible { get; set; } = true;
        public int? QualifyingEliminatedInSession { get; set; }
        public double?[] QualifyingSessionBestLapSeconds { get; } = new double?[3];
        public bool PracticeFinalLapPending { get; set; }
        public double?[] PracticeSessionBestLapSeconds { get; } = new double?[3];
        public double? FalseStartBaselineProgress { get; set; }
        public DateTimeOffset? FalseStartCandidateStartedAt { get; set; }
        public bool FalseStartPenalized { get; set; }
    }

    private readonly record struct RaceProgressSample(double DistanceLaps, double ElapsedSeconds);
    private readonly record struct CollisionPositionSample(
        DateTimeOffset At,
        double WorldX,
        double WorldY,
        double WorldZ,
        bool HasWorldVelocity,
        double WorldVelocityX,
        double WorldVelocityY,
        double WorldVelocityZ);
}
