using LazyForza.RaceServer.Protocol;

namespace LazyForza.RaceServer.Web;

public sealed record RacePublicTimingAccessStatus(
    bool Enabled,
    DateTimeOffset? GeneratedAt);

public sealed record RacePublicTimingTokenSecret(
    string Token,
    DateTimeOffset GeneratedAt);

public sealed record RacePublicTimingPenaltySnapshot(
    RacePenaltyKind Kind,
    double? ValueSeconds,
    int? GridPlaces,
    string Reason,
    DateTimeOffset IssuedAt,
    bool IsServed,
    bool IsRevoked,
    bool IsPostRaceAdjustment,
    bool IsAutomatic);

public sealed record RacePublicTimingYellowZoneSnapshot(
    int? SectorIndex,
    bool IsAutomatic,
    string Reason,
    string? ParticipantName);

public sealed record RacePublicTimingParticipantSnapshot(
    int Position,
    string DisplayName,
    string ThemeColor,
    string? TeamName,
    string? TeamColor,
    RaceParticipantStatus Status,
    bool IsConnected,
    int CompletedLaps,
    int CurrentSector,
    double TrackProgress,
    double CurrentLapSeconds,
    double? LastLapSeconds,
    double? BestLapSeconds,
    double? GapToLeaderSeconds,
    double? IntervalSeconds,
    bool IsInPitLane,
    bool IsInServiceZone,
    double PitServiceElapsedSeconds,
    bool PitServiceRequirementMet,
    int CompletedPitServices,
    double TimePenaltySeconds,
    double PendingTimePenaltySeconds,
    bool IsServingTimePenalty,
    bool HasPendingDriveThrough,
    bool IsServingDriveThrough,
    bool DriveThroughOverdue,
    IReadOnlyList<RacePublicTimingPenaltySnapshot> Penalties);

public sealed record RacePublicTimingSessionSnapshot(
    long Revision,
    string SessionName,
    RaceSessionPhase Phase,
    RaceSessionPhase? SuspendedFromPhase,
    RaceControlFlag Flag,
    string? FlagMessage,
    string? TrackName,
    int TotalRaceLaps,
    DateTimeOffset? StartsAt,
    DateTimeOffset? PracticeEndsAt,
    DateTimeOffset? QualifyingEndsAt,
    double? RaceElapsedSeconds,
    string? FastestDriverName,
    double? FastestLapSeconds,
    int PracticeSessionNumber,
    int PracticeSessionCount,
    int QualifyingSessionNumber,
    int QualifyingSessionCount,
    int MinimumRequiredPitStops,
    IReadOnlyList<RacePublicTimingYellowZoneSnapshot> YellowZones,
    IReadOnlyList<RacePublicTimingParticipantSnapshot> Participants,
    DateTimeOffset ServerTime);

public sealed record RacePublicTimingStageParticipantSnapshot(
    int Position,
    string DisplayName,
    string ThemeColor,
    string? TeamName,
    string? TeamColor,
    RaceParticipantStatus Status,
    int CompletedLaps,
    double? BestLapSeconds,
    double? RaceTotalSeconds,
    double? AdjustedRaceTotalSeconds,
    double? GapToLeaderSeconds,
    double TimePenaltySeconds,
    IReadOnlyList<RacePublicTimingPenaltySnapshot> Penalties);

public sealed record RacePublicTimingStageResultSnapshot(
    RaceSessionPhase Phase,
    string Label,
    int SessionNumber,
    int SessionCount,
    bool IsComplete,
    DateTimeOffset CompletedAt,
    string SessionName,
    string? TrackName,
    string? FastestDriverName,
    double? FastestLapSeconds,
    IReadOnlyList<RacePublicTimingStageParticipantSnapshot> Participants);

public sealed record RacePublicTimingPayload(
    RacePublicTimingSessionSnapshot State,
    IReadOnlyList<RacePublicTimingStageResultSnapshot> Results);

public static class RacePublicTimingProjection
{
    public static RacePublicTimingPayload Create(
        RaceSessionSnapshot snapshot,
        IReadOnlyList<RaceStageResultSnapshot> results)
    {
        var participantNames = snapshot.Participants.ToDictionary(item => item.Id, item => item.DisplayName);
        return new RacePublicTimingPayload(
            new RacePublicTimingSessionSnapshot(
                snapshot.Revision,
                snapshot.SessionName,
                snapshot.Phase,
                snapshot.SuspendedFromPhase,
                snapshot.Flag,
                snapshot.FlagMessage,
                snapshot.TrackName,
                snapshot.TotalRaceLaps,
                snapshot.StartsAt,
                snapshot.PracticeEndsAt,
                snapshot.QualifyingEndsAt,
                snapshot.RaceElapsedSeconds,
                snapshot.FastestParticipantId is Guid fastestId && participantNames.TryGetValue(fastestId, out var fastestName)
                    ? fastestName
                    : null,
                snapshot.FastestLapSeconds,
                snapshot.PracticeSessionNumber,
                snapshot.PracticeSessionCount,
                snapshot.QualifyingSessionNumber,
                snapshot.QualifyingSessionCount,
                snapshot.MinimumRequiredPitStops,
                (snapshot.YellowZones ?? []).Select(zone => new RacePublicTimingYellowZoneSnapshot(
                    zone.SectorIndex,
                    zone.IsAutomatic,
                    zone.Reason,
                    zone.ParticipantName)).ToArray(),
                snapshot.Participants.Select(Participant).ToArray(),
                snapshot.ServerTime),
            results.Select(Result).ToArray());
    }

    private static RacePublicTimingParticipantSnapshot Participant(RaceParticipantSnapshot participant) => new(
        participant.Position,
        participant.DisplayName,
        participant.ThemeColor,
        participant.TeamName,
        participant.TeamColor,
        participant.Status,
        participant.IsConnected,
        participant.CompletedLaps,
        participant.CurrentSector,
        participant.TrackProgress,
        participant.CurrentLapSeconds,
        participant.LastLapSeconds,
        participant.BestLapSeconds,
        participant.GapToLeaderSeconds,
        participant.IntervalSeconds,
        participant.IsInPitLane,
        participant.IsInServiceZone,
        participant.PitServiceElapsedSeconds,
        participant.PitServiceRequirementMet,
        participant.CompletedPitServices,
        participant.TimePenaltySeconds,
        participant.PendingTimePenaltySeconds,
        participant.IsServingTimePenalty,
        participant.HasPendingDriveThrough,
        participant.IsServingDriveThrough,
        participant.DriveThroughOverdue,
        participant.Penalties.Select(Penalty).ToArray());

    private static RacePublicTimingStageResultSnapshot Result(RaceStageResultSnapshot result)
    {
        var participantNames = result.Participants.ToDictionary(item => item.Id, item => item.DisplayName);
        return new RacePublicTimingStageResultSnapshot(
            result.Phase,
            result.Label,
            result.SessionNumber,
            result.SessionCount,
            result.IsComplete,
            result.CompletedAt,
            result.SessionName,
            result.TrackName,
            result.FastestParticipantId is Guid fastestId && participantNames.TryGetValue(fastestId, out var fastestName)
                ? fastestName
                : null,
            result.FastestLapSeconds,
            result.Participants.Select(StageParticipant).ToArray());
    }

    private static RacePublicTimingStageParticipantSnapshot StageParticipant(
        RaceStageResultParticipantSnapshot participant) => new(
        participant.Position,
        participant.DisplayName,
        participant.ThemeColor,
        participant.TeamName,
        participant.TeamColor,
        participant.Status,
        participant.CompletedLaps,
        participant.BestLapSeconds,
        participant.RaceTotalSeconds,
        participant.AdjustedRaceTotalSeconds,
        participant.GapToLeaderSeconds,
        participant.TimePenaltySeconds,
        participant.Penalties.Select(Penalty).ToArray());

    private static RacePublicTimingPenaltySnapshot Penalty(RacePenaltySnapshot penalty) => new(
        penalty.Kind,
        penalty.ValueSeconds,
        penalty.GridPlaces,
        penalty.Reason,
        penalty.IssuedAt,
        penalty.IsServed,
        penalty.IsRevoked,
        penalty.IsPostRaceAdjustment,
        penalty.IsAutomatic);
}
