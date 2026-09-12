using System.Text.Json;
using LazyForza.RaceServer.Protocol;

namespace LazyForza.RaceServer.Core;

public sealed partial class RaceCoordinator
{
    private const string RecoveryPendingMessage = "赛事已恢复并冻结，请管理员核对成绩后发布全场绿旗续赛，或返回大厅开始新赛事。";
    private bool recoveryPending;
    private DateTimeOffset? recoveryFrozenAt;
    private RaceControlFlag recoveryFlag;
    private DateTimeOffset lastCheckpointAt;
    private static readonly JsonSerializerOptions RecoveryJsonOptions = new(RaceProtocolJson.Options)
    {
        RespectNullableAnnotations = true
    };

    public bool AwaitingRecoveryConfirmation { get { lock (sync) return recoveryPending; } }

    public void RestorePersistedState()
    {
        lock (sync)
        {
            if (participants.Count != 0 || revision != 0)
                throw new InvalidOperationException("只能在启动时向空协调器加载恢复状态。");
            var envelope = persistence.LoadRecoveryState();
            if (envelope is null) return;
            if (envelope.Version != RaceRecoveryState.CurrentVersion)
                throw new InvalidDataException("不支持的赛事恢复版本。");
            var saved = envelope.State.Deserialize<CoordinatorRecoveryData>(RecoveryJsonOptions)
                ?? throw new InvalidDataException("赛事恢复状态无效。");
            if (saved.Participants.Select(item => item.Id).Distinct().Count() != saved.Participants.Count ||
                saved.Participants.Any(item => item.Id == Guid.Empty || string.IsNullOrWhiteSpace(item.ResumeToken)) ||
                saved.Participants.Select(item => item.ResumeToken).Distinct().Count() != saved.Participants.Count ||
                !Enum.IsDefined(saved.Phase))
                throw new InvalidDataException("赛事恢复身份或阶段无效。");
            participants.Clear();
            participants.AddRange(saved.Participants);
            observers.Clear();
            observers.AddRange(saved.Observers);
            penalties.Clear();
            penalties.AddRange(saved.Penalties);
            investigations.Clear();
            investigations.AddRange(saved.Investigations);
            events.Clear();
            events.AddRange(saved.Events);
            resultHistory.Clear();
            resultHistory.AddRange(saved.ResultHistory);
            receivedLapEvents.Clear();
            receivedLapEvents.UnionWith(saved.ReceivedLapEvents);
            receivedPitServiceEvents.Clear();
            receivedPitServiceEvents.UnionWith(saved.ReceivedPitServiceEvents);
            revokedResumeTokens.Clear();
            revokedResumeTokens.UnionWith(saved.RevokedResumeTokens);
            manualSectorYellows.Clear();
            foreach (var item in saved.ManualSectorYellows) manualSectorYellows.Add(item.Key, item.Value);
            manualFullCourseYellow = saved.ManualFullCourseYellow;
            phase = saved.Phase;
            phaseBeforeSuspension = saved.PhaseBeforeSuspension;
            flag = saved.Flag;
            flagMessage = saved.FlagMessage;
            sessionName = saved.SessionName;
            totalRaceLaps = saved.TotalRaceLaps;
            minimumRequiredPitStops = saved.MinimumRequiredPitStops;
            sectorCount = saved.SectorCount;
            automaticYellowEnabled = saved.AutomaticYellowEnabled;
            automaticCollisionInvestigationsEnabled = saved.AutomaticCollisionInvestigationsEnabled;
            disconnectedLapRecoveryEnabled = saved.DisconnectedLapRecoveryEnabled;
            slowSpeedKph = saved.SlowSpeedKph;
            slowDurationSeconds = saved.SlowDurationSeconds;
            severeLateralOffsetMeters = saved.SevereLateralOffsetMeters;
            recoveryDurationSeconds = saved.RecoveryDurationSeconds;
            trackLimitMode = saved.TrackLimitMode;
            allowTeams = saved.AllowTeams;
            driversPerTeam = saved.DriversPerTeam;
            teams = saved.Teams;
            chequeredImminent = saved.ChequeredImminent;
            trackName = saved.TrackName;
            trackId = saved.TrackId;
            trackRevision = saved.TrackRevision;
            trackPackageHash = saved.TrackPackageHash;
            startsAt = saved.StartsAt;
            startSequenceAt = saved.StartSequenceAt;
            raceSuspendedAt = saved.RaceSuspendedAt;
            raceSuspendedDuration = saved.RaceSuspendedDuration;
            raceEndedAt = saved.RaceEndedAt;
            qualifyingEndsAt = saved.QualifyingEndsAt;
            practiceEndsAt = saved.PracticeEndsAt;
            qualifyingSessionNumber = saved.QualifyingSessionNumber;
            qualifyingSessionCount = saved.QualifyingSessionCount;
            qualifyingSessionMinutes = saved.QualifyingSessionMinutes;
            qualifyingEliminationCounts = saved.QualifyingEliminationCounts;
            practiceSessionNumber = saved.PracticeSessionNumber;
            practiceSessionCount = saved.PracticeSessionCount;
            practiceSessionMinutes = saved.PracticeSessionMinutes;
            illuminatedStartLights = saved.IlluminatedStartLights;
            startLightsOut = saved.StartLightsOut;
            qualifyingTimeExpired = saved.QualifyingTimeExpired;
            practiceTimeExpired = saved.PracticeTimeExpired;
            banner = saved.Banner;
            revision = saved.Revision;
            eventSequence = saved.EventSequence;
            activeResultStageId = saved.ActiveResultStageId;
            recoveryPending = saved.RecoveryPending;
            recoveryFrozenAt = saved.RecoveryFrozenAt;
            recoveryFlag = saved.RecoveryFlag;
            collisionReplays.Clear();
            foreach (var replay in saved.CollisionReplays)
            {
                var restored = new CollisionReplayState(replay.InvestigationId, replay.FirstIncidentAt,
                    replay.ReporterParticipantId, replay.OtherParticipantId, replay.ReporterName,
                    replay.OtherName, replay.ReporterThemeColor, replay.OtherThemeColor);
                foreach (var at in replay.IncidentTimes) restored.AddIncident(at);
                foreach (var sample in replay.ReporterSamples)
                    restored.AddSample(replay.ReporterParticipantId, RestoreReplaySample(sample));
                foreach (var sample in replay.OtherSamples)
                    restored.AddSample(replay.OtherParticipantId, RestoreReplaySample(sample));
                collisionReplays.Add(replay.InvestigationId, restored);
            }
            if (phase is not (RaceSessionPhase.Lobby or RaceSessionPhase.Finished))
            {
                if (!recoveryPending)
                {
                    recoveryFrozenAt = envelope.SavedAt;
                    recoveryFlag = flag;
                }
                recoveryPending = true;
                if (phase != RaceSessionPhase.Suspended) phaseBeforeSuspension = phase;
                if (phaseBeforeSuspension == RaceSessionPhase.Race)
                    raceSuspendedAt ??= recoveryFrozenAt;
                phase = RaceSessionPhase.Suspended;
                flag = RaceControlFlag.Red;
                flagMessage = RecoveryPendingMessage;
                banner = NewBanner(RaceBannerKind.Information, "等待确认续赛", RecoveryPendingMessage, null, null);
            }
            foreach (var participant in participants)
            {
                participant.IsConnected = false;
                if (participant.Status is not (RaceParticipantStatus.Finished or RaceParticipantStatus.DidNotFinish or RaceParticipantStatus.Disqualified))
                    participant.Status = RaceParticipantStatus.Disconnected;
                participant.AwaitingFreshTelemetryAfterResume = true;
                ResetCollisionState(participant);
                ResetLivePenaltyServiceState(participant);
                ResetTrackLimitExcursion(participant);
                participant.ProgressContinuityReady = false;
                participant.RaceProgressContinuityReady = false;
                participant.RaceProgressSamples.Clear();
                participant.RaceProgressInitialized = false;
                participant.RaceProgressAwaitingWrap = false;
                participant.RaceProgressPitTransitActive = false;
                participant.LapValidationSamples.Clear();
                participant.AutomaticYellowActive = false;
                participant.HazardCandidateStartedAt = null;
                participant.HazardRecoveryStartedAt = null;
                participant.IsInPitLane = false;
                participant.IsInServiceZone = false;
                participant.PitServiceElapsedSeconds = 0;
            }
            IncrementRevision();
            CommitRecoveryLocked();
        }
    }

    public void Checkpoint(DateTimeOffset? observedAt = null)
    {
        lock (sync)
        {
            var now = observedAt ?? DateTimeOffset.UtcNow;
            if (now - lastCheckpointAt < TimeSpan.FromSeconds(2)) return;
            CommitRecoveryLocked(now);
        }
    }

    private void CommitRecoveryLocked(DateTimeOffset? observedAt = null)
    {
        if (ReferenceEquals(persistence, NullRaceStatePersistence.Instance)) return;
        var now = observedAt ?? DateTimeOffset.UtcNow;
        var saved = new CoordinatorRecoveryData
        {
            Participants = participants,
            Observers = observers,
            Penalties = penalties,
            Investigations = investigations,
            Events = events,
            ResultHistory = resultHistory,
            ReceivedLapEvents = receivedLapEvents,
            ReceivedPitServiceEvents = receivedPitServiceEvents,
            RevokedResumeTokens = revokedResumeTokens,
            ManualSectorYellows = manualSectorYellows,
            ManualFullCourseYellow = manualFullCourseYellow,
            Phase = phase,
            PhaseBeforeSuspension = phaseBeforeSuspension,
            Flag = flag,
            FlagMessage = flagMessage,
            SessionName = sessionName,
            TotalRaceLaps = totalRaceLaps,
            MinimumRequiredPitStops = minimumRequiredPitStops,
            SectorCount = sectorCount,
            AutomaticYellowEnabled = automaticYellowEnabled,
            AutomaticCollisionInvestigationsEnabled = automaticCollisionInvestigationsEnabled,
            DisconnectedLapRecoveryEnabled = disconnectedLapRecoveryEnabled,
            SlowSpeedKph = slowSpeedKph,
            SlowDurationSeconds = slowDurationSeconds,
            SevereLateralOffsetMeters = severeLateralOffsetMeters,
            RecoveryDurationSeconds = recoveryDurationSeconds,
            TrackLimitMode = trackLimitMode,
            AllowTeams = allowTeams,
            DriversPerTeam = driversPerTeam,
            Teams = teams,
            ChequeredImminent = chequeredImminent,
            TrackName = trackName,
            TrackId = trackId,
            TrackRevision = trackRevision,
            TrackPackageHash = trackPackageHash,
            StartsAt = startsAt,
            StartSequenceAt = startSequenceAt,
            RaceSuspendedAt = raceSuspendedAt,
            RaceSuspendedDuration = raceSuspendedDuration,
            RaceEndedAt = raceEndedAt,
            QualifyingEndsAt = qualifyingEndsAt,
            PracticeEndsAt = practiceEndsAt,
            QualifyingSessionNumber = qualifyingSessionNumber,
            QualifyingSessionCount = qualifyingSessionCount,
            QualifyingSessionMinutes = qualifyingSessionMinutes,
            QualifyingEliminationCounts = qualifyingEliminationCounts,
            PracticeSessionNumber = practiceSessionNumber,
            PracticeSessionCount = practiceSessionCount,
            PracticeSessionMinutes = practiceSessionMinutes,
            IlluminatedStartLights = illuminatedStartLights,
            StartLightsOut = startLightsOut,
            QualifyingTimeExpired = qualifyingTimeExpired,
            PracticeTimeExpired = practiceTimeExpired,
            Banner = banner,
            Revision = revision,
            EventSequence = eventSequence,
            ActiveResultStageId = activeResultStageId,
            RecoveryPending = recoveryPending,
            RecoveryFrozenAt = recoveryFrozenAt,
            RecoveryFlag = recoveryFlag,
            CollisionReplays = collisionReplays.Values.Select(item => item.Snapshot(now)).ToArray()
        };
        persistence.SaveRecoveryState(new RaceRecoveryState(RaceRecoveryState.CurrentVersion, now,
            JsonSerializer.SerializeToElement(saved, RaceProtocolJson.Options)));
        lastCheckpointAt = now;
    }

    private void ResumeRecoveryClockLocked(DateTimeOffset now)
    {
        var pause = now - (recoveryFrozenAt ?? now);
        if (pause < TimeSpan.Zero) pause = TimeSpan.Zero;
        if (phaseBeforeSuspension == RaceSessionPhase.Practice) practiceEndsAt += pause;
        if (phaseBeforeSuspension == RaceSessionPhase.Qualifying) qualifyingEndsAt += pause;
        if (phaseBeforeSuspension == RaceSessionPhase.Countdown)
        {
            startsAt += pause;
            startSequenceAt += pause;
        }
        foreach (var participant in participants)
        {
            participant.DisconnectedLapRecoveryUntil += pause;
            participant.DriveThroughReminderAt += pause;
        }
        recoveryPending = false;
        recoveryFrozenAt = null;
        banner = null;
    }

    private static CollisionPositionSample RestoreReplaySample(RaceCollisionReplaySampleSnapshot value) => new(
        value.At, value.WorldX, value.WorldY, value.WorldZ, true,
        value.VelocityX, value.VelocityY, value.VelocityZ);

    private sealed class CoordinatorRecoveryData
    {
        public required List<ParticipantState> Participants { get; init; }
        public required List<ObserverState> Observers { get; init; }
        public required List<RacePenaltySnapshot> Penalties { get; init; }
        public required List<RaceInvestigationSnapshot> Investigations { get; init; }
        public required List<RaceEventSnapshot> Events { get; init; }
        public required List<RaceStageResultSnapshot> ResultHistory { get; init; }
        public required HashSet<Guid> ReceivedLapEvents { get; init; }
        public required HashSet<Guid> ReceivedPitServiceEvents { get; init; }
        public required HashSet<string> RevokedResumeTokens { get; init; }
        public required Dictionary<int, string> ManualSectorYellows { get; init; }
        public required string? ManualFullCourseYellow { get; init; }
        public required RaceSessionPhase Phase { get; init; }
        public required RaceSessionPhase PhaseBeforeSuspension { get; init; }
        public required RaceControlFlag Flag { get; init; }
        public required string? FlagMessage { get; init; }
        public required string SessionName { get; init; }
        public required int TotalRaceLaps { get; init; }
        public required int MinimumRequiredPitStops { get; init; }
        public required int SectorCount { get; init; }
        public required bool AutomaticYellowEnabled { get; init; }
        public required bool AutomaticCollisionInvestigationsEnabled { get; init; }
        public required bool DisconnectedLapRecoveryEnabled { get; init; }
        public required double SlowSpeedKph { get; init; }
        public required double SlowDurationSeconds { get; init; }
        public required double SevereLateralOffsetMeters { get; init; }
        public required double RecoveryDurationSeconds { get; init; }
        public required TrackLimitEnforcementMode TrackLimitMode { get; init; }
        public required bool AllowTeams { get; init; }
        public required int DriversPerTeam { get; init; }
        public required IReadOnlyList<RaceTeamDefinition> Teams { get; init; }
        public required bool ChequeredImminent { get; init; }
        public required string? TrackName { get; init; }
        public required string? TrackId { get; init; }
        public required string? TrackRevision { get; init; }
        public required string? TrackPackageHash { get; init; }
        public required DateTimeOffset? StartsAt { get; init; }
        public required DateTimeOffset? StartSequenceAt { get; init; }
        public required DateTimeOffset? RaceSuspendedAt { get; init; }
        public required TimeSpan RaceSuspendedDuration { get; init; }
        public required DateTimeOffset? RaceEndedAt { get; init; }
        public required DateTimeOffset? QualifyingEndsAt { get; init; }
        public required DateTimeOffset? PracticeEndsAt { get; init; }
        public required int QualifyingSessionNumber { get; init; }
        public required int QualifyingSessionCount { get; init; }
        public required IReadOnlyList<int> QualifyingSessionMinutes { get; init; }
        public required IReadOnlyList<int> QualifyingEliminationCounts { get; init; }
        public required int PracticeSessionNumber { get; init; }
        public required int PracticeSessionCount { get; init; }
        public required IReadOnlyList<int> PracticeSessionMinutes { get; init; }
        public required int IlluminatedStartLights { get; init; }
        public required bool StartLightsOut { get; init; }
        public required bool QualifyingTimeExpired { get; init; }
        public required bool PracticeTimeExpired { get; init; }
        public required RaceBannerSnapshot? Banner { get; init; }
        public required long Revision { get; init; }
        public required long EventSequence { get; init; }
        public required Guid? ActiveResultStageId { get; init; }
        public required bool RecoveryPending { get; init; }
        public required DateTimeOffset? RecoveryFrozenAt { get; init; }
        public required RaceControlFlag RecoveryFlag { get; init; }
        public required RaceCollisionReplaySnapshot[] CollisionReplays { get; init; }
    }
}
