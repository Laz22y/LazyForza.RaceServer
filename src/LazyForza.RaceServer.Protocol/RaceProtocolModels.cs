using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LazyForza.RaceServer.Protocol;

public static class RaceProtocol
{
    public const int CurrentVersion = 2;
    public const int MaximumParticipants = 12;
    public const int MaximumObservers = 12;
    public const int MaximumMessageBytes = 64 * 1024;
    public const int MaximumDisplayNameLength = 20;
    public const int MaximumTeamNameLength = 24;
}

public enum RaceSessionPhase
{
    Lobby,
    Practice,
    Qualifying,
    Grid,
    OutLap,
    FormationLap,
    Countdown,
    Race,
    Suspended,
    Finished
}

public enum RaceControlFlag
{
    Green,
    Yellow,
    Red,
    Chequered
}

public enum RaceParticipantStatus
{
    Connected,
    Ready,
    OnTrack,
    InPitLane,
    InService,
    Finished,
    DidNotFinish,
    Disqualified,
    Disconnected
}

public enum RaceGripCondition
{
    Unknown,
    SlightlyReduced,
    ModeratelyReduced,
    SeverelyReduced,
    AtLimit
}

public enum RacePenaltyKind
{
    Warning,
    Time,
    DriveThrough,
    StopAndGo,
    GridDrop,
    Disqualification
}

public enum TrackLimitEnforcementMode
{
    WarningsOnly,
    Automatic,
    Disabled
}

public enum RaceBannerKind
{
    Information,
    FastestLap,
    Penalty,
    YellowFlag,
    RedFlag,
    BlueFlag,
    ChequeredFlag,
    Winner
}

public enum RaceInvestigationStatus
{
    Pending,
    Penalized,
    Dismissed
}

public static class RaceMessageTypes
{
    public const string Login = "login";
    public const string LoginAccepted = "loginAccepted";
    public const string LoginRejected = "loginRejected";
    public const string Ready = "ready";
    public const string Telemetry = "telemetry";
    public const string LapCompleted = "lapCompleted";
    public const string Snapshot = "snapshot";
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string Error = "error";
}

public sealed record RaceEnvelope(
    int ProtocolVersion,
    string Type,
    long Sequence,
    JsonElement Payload);

public sealed record RaceServerDescriptor(
    string ServerName,
    int ProtocolVersion,
    int MaximumParticipants,
    bool RequiresPassword,
    string WebSocketPath,
    string ControlPanelPath,
    string? ActiveTrackId,
    string? ActiveTrackRevision,
    RaceSessionPhase Phase,
    DateTimeOffset ServerTime,
    string? ActiveTrackName = null,
    string? ActiveTrackPackageHash = null,
    bool AllowTeams = true,
    int SectorCount = 0,
    int DriversPerTeam = 6,
    IReadOnlyList<RaceTeamDefinition>? Teams = null,
    bool TrackPackageAvailable = false,
    long? TrackPackageSizeBytes = null,
    string? TrackPackageDownloadPath = null,
    string? TrackPackageFileSha256 = null,
    string? OrganizerLogoHash = null,
    string? OrganizerLogoMimeType = null,
    string? OrganizerLogoDownloadPath = null,
    bool SupportsObservers = true,
    int MaximumObservers = RaceProtocol.MaximumObservers);

public sealed record RaceTeamDefinition(
    string Id,
    string Name,
    string ThemeColor);

public sealed record RaceLoginRequest(
    string Password,
    string DisplayName,
    string ThemeColor,
    string? TeamName,
    string ClientVersion,
    string? ResumeToken,
    string? TrackId,
    string? TrackRevision,
    string? TrackPackageHash,
    int? SectorCount = null,
    string? TeamId = null,
    bool IsObserver = false);

public sealed record RaceLoginAccepted(
    Guid ParticipantId,
    string ResumeToken,
    RaceSessionSnapshot Snapshot,
    DateTimeOffset ServerTime,
    bool IsObserver = false);

public sealed record RaceLoginRejected(string Code, string Message);

public sealed record RaceReadyUpdate(bool IsReady);

public sealed record RaceClockPing(long ClientMonotonicMilliseconds);

public sealed record RaceClockPong(
    long ClientMonotonicMilliseconds,
    long ServerUnixMilliseconds);

public sealed record RaceTelemetryUpdate(
    long ClientMonotonicMilliseconds,
    double TrackProgress,
    double LateralOffsetMeters,
    double MapX,
    double MapY,
    double SpeedKph,
    int CompletedLaps,
    int CurrentSector,
    double CurrentLapSeconds,
    bool IsInPitLane,
    bool IsInServiceZone,
    bool IsTelemetryValid,
    bool IsPausedOrRewinding,
    RaceGripCondition GripCondition,
    double PitServiceElapsedSeconds,
    bool PitServiceRequirementMet,
    int CompletedPitServices,
    double TrackToleranceMeters = 18,
    double TrackLengthMeters = 0,
    double PitSpeedLimitKph = 0,
    double PitLaneElapsedSeconds = 0,
    bool IsApproachingPit = false,
    bool IsOnPitRoute = false,
    bool HasWorldPosition = false,
    double WorldX = 0,
    double WorldY = 0,
    double WorldZ = 0,
    double VelocityX = 0,
    double VelocityY = 0,
    double VelocityZ = 0,
    long ImpactSequence = 0,
    double ImpactMagnitudeMps = 0,
    double ImpactSpeedLossMps = 0,
    double ImpactWorldX = 0,
    double ImpactWorldY = 0,
    double ImpactWorldZ = 0,
    int ImpactAgeMilliseconds = 0);

public sealed record RaceLapCompleted(
    Guid EventId,
    int LapNumber,
    double LapSeconds,
    IReadOnlyList<double> SectorSeconds,
    bool IsValid,
    string? InvalidReason,
    long ClientMonotonicMilliseconds,
    bool IsBestLapEligible = true);

public sealed record RacePenaltySnapshot(
    Guid Id,
    Guid ParticipantId,
    RacePenaltyKind Kind,
    double? ValueSeconds,
    int? GridPlaces,
    string Reason,
    DateTimeOffset IssuedAt,
    bool IsServed,
    bool IsRevoked,
    bool IsPostRaceAdjustment = false,
    bool IsAutomatic = false,
    Guid? InvestigationId = null);

public sealed record RaceInvestigationSnapshot(
    Guid Id,
    Guid ParticipantId,
    string Offense,
    DateTimeOffset DetectedAt,
    int LapNumber,
    RaceInvestigationStatus Status,
    Guid? PenaltyId = null,
    DateTimeOffset? ResolvedAt = null,
    IReadOnlyList<Guid>? RelatedParticipantIds = null,
    RaceCollisionEvidenceSnapshot? CollisionEvidence = null);

public sealed record RaceCollisionEvidenceSnapshot(
    DateTimeOffset IncidentAt,
    Guid ReporterParticipantId,
    Guid OtherParticipantId,
    string ReporterName,
    string OtherName,
    string ReporterThemeColor,
    string OtherThemeColor,
    double ReporterWorldX,
    double ReporterWorldY,
    double ReporterWorldZ,
    double OtherWorldX,
    double OtherWorldY,
    double OtherWorldZ,
    double ReporterVelocityX,
    double ReporterVelocityZ,
    double OtherVelocityX,
    double OtherVelocityZ,
    double HorizontalDistanceMeters,
    double VerticalDistanceMeters,
    double RelativeSpeedKph,
    double ImpactMagnitudeMps,
    double ImpactSpeedLossMps);

public sealed record RaceParticipantSnapshot(
    Guid Id,
    int Position,
    string DisplayName,
    string ThemeColor,
    string? TeamName,
    RaceParticipantStatus Status,
    bool IsConnected,
    bool IsReady,
    int CompletedLaps,
    int CurrentSector,
    double TrackProgress,
    double MapX,
    double MapY,
    double SpeedKph,
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
    RaceGripCondition GripCondition,
    IReadOnlyList<double?> BestSectorSeconds,
    IReadOnlyList<RacePenaltySnapshot> Penalties,
    DateTimeOffset LastSeenAt,
    bool QualifyingFinalLapPending = false,
    double? RaceTotalSeconds = null,
    double? AdjustedRaceTotalSeconds = null,
    double TimePenaltySeconds = 0,
    int TrackLimitWarnings = 0,
    string? TeamId = null,
    string? TeamColor = null,
    double PitLaneElapsedSeconds = 0,
    double PendingTimePenaltySeconds = 0,
    bool IsServingTimePenalty = false,
    double PenaltyServiceElapsedSeconds = 0,
    double PenaltyServiceRequiredSeconds = 0,
    bool HasPendingDriveThrough = false,
    bool PenaltyServiceCompleted = false,
    int? DriveThroughLapsRemaining = null,
    DateTimeOffset? DriveThroughReminderAt = null,
    bool DriveThroughOverdue = false,
    bool IsServingDriveThrough = false,
    bool QualifyingEligible = true,
    int? QualifyingEliminatedInSession = null,
    IReadOnlyList<double?>? QualifyingSessionBestLapSeconds = null,
    bool PracticeFinalLapPending = false,
    IReadOnlyList<double?>? PracticeSessionBestLapSeconds = null);

public sealed record RaceObserverSnapshot(
    Guid Id,
    string DisplayName,
    DateTimeOffset ConnectedAt);

public sealed record RaceEventSnapshot(
    long Sequence,
    DateTimeOffset At,
    string Type,
    string Message,
    Guid? ParticipantId = null);

public sealed record RaceBannerSnapshot(
    Guid Id,
    RaceBannerKind Kind,
    string Title,
    string? Detail,
    Guid? ParticipantId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    bool IsInvestigation = false);

public sealed record RaceYellowZoneSnapshot(
    int? SectorIndex,
    bool IsAutomatic,
    string Reason,
    Guid? ParticipantId,
    string? ParticipantName);

public sealed record RaceBlueFlagSnapshot(
    Guid RecipientParticipantId,
    Guid ApproachingParticipantId,
    double DistanceAhead);

public sealed record RaceSessionSnapshot(
    long Revision,
    string SessionName,
    RaceSessionPhase Phase,
    RaceControlFlag Flag,
    string? FlagMessage,
    string? TrackId,
    string? TrackRevision,
    string? TrackPackageHash,
    int TotalRaceLaps,
    DateTimeOffset? StartsAt,
    DateTimeOffset? QualifyingEndsAt,
    Guid? FastestParticipantId,
    double? FastestLapSeconds,
    IReadOnlyList<double?> FastestSectorSeconds,
    RaceBannerSnapshot? Banner,
    IReadOnlyList<RaceParticipantSnapshot> Participants,
    DateTimeOffset ServerTime,
    IReadOnlyList<RaceYellowZoneSnapshot>? YellowZones = null,
    int SectorCount = 0,
    bool AllowTeams = true,
    string? TrackName = null,
    IReadOnlyList<RaceBlueFlagSnapshot>? BlueFlags = null,
    DateTimeOffset? StartSequenceAt = null,
    int IlluminatedStartLights = 0,
    bool StartLightsOut = false,
    bool QualifyingTimeExpired = false,
    double? RaceElapsedSeconds = null,
    RaceSessionPhase? SuspendedFromPhase = null,
    int DriversPerTeam = 6,
    IReadOnlyList<RaceTeamDefinition>? Teams = null,
    bool ChequeredImminent = false,
    IReadOnlyList<double?>? FastestLapSectorSeconds = null,
    string? OrganizerLogoHash = null,
    string? OrganizerLogoMimeType = null,
    string? OrganizerLogoDownloadPath = null,
    IReadOnlyList<RacePenaltySnapshot>? Penalties = null,
    IReadOnlyList<RaceInvestigationSnapshot>? Investigations = null,
    int QualifyingSessionNumber = 0,
    int QualifyingSessionCount = 1,
    IReadOnlyList<int>? QualifyingSessionMinutes = null,
    IReadOnlyList<int>? QualifyingEliminationCounts = null,
    DateTimeOffset? PracticeEndsAt = null,
    bool PracticeTimeExpired = false,
    int PracticeSessionNumber = 0,
    int PracticeSessionCount = 1,
    IReadOnlyList<int>? PracticeSessionMinutes = null,
    IReadOnlyList<RaceObserverSnapshot>? Observers = null,
    int MinimumRequiredPitStops = 1);

public sealed record RaceAdminLoginRequest(string Password);

public sealed record RaceAdminSessionCommand(
    RaceSessionPhase Phase,
    string? SessionName,
    int? TotalRaceLaps,
    int? CountdownSeconds,
    int? QualifyingMinutes,
    int? QualifyingSessionCount = null,
    IReadOnlyList<int>? QualifyingSessionMinutes = null,
    IReadOnlyList<int?>? QualifyingEliminationCounts = null,
    int? PracticeSessionCount = null,
    IReadOnlyList<int>? PracticeSessionMinutes = null);

public sealed record RaceAdminFlagCommand(
    RaceControlFlag Flag,
    string? Message,
    int? SectorIndex = null);

public sealed record RaceAdminRoomSettingsCommand(
    string SessionName,
    int TotalRaceLaps,
    int SectorCount,
    bool AutomaticYellowEnabled,
    double SlowSpeedKph,
    double SlowDurationSeconds,
    double SevereLateralOffsetMeters,
    double RecoveryDurationSeconds,
    bool AllowTeams = true,
    string? TrackName = null,
    string? TrackId = null,
    string? TrackRevision = null,
    string? TrackPackageHash = null,
    int TeamCount = 2,
    int DriversPerTeam = 6,
    IReadOnlyList<RaceTeamDefinition>? Teams = null,
    TrackLimitEnforcementMode TrackLimitMode = TrackLimitEnforcementMode.WarningsOnly,
    int MinimumRequiredPitStops = 1,
    bool AutomaticCollisionInvestigationsEnabled = false);

public sealed record RaceRoomSettingsSnapshot(
    string SessionName,
    int TotalRaceLaps,
    int SectorCount,
    bool AutomaticYellowEnabled,
    double SlowSpeedKph,
    double SlowDurationSeconds,
    double SevereLateralOffsetMeters,
    double RecoveryDurationSeconds,
    bool AllowTeams = true,
    string? TrackName = null,
    string? TrackId = null,
    string? TrackRevision = null,
    string? TrackPackageHash = null,
    int TeamCount = 2,
    int DriversPerTeam = 6,
    IReadOnlyList<RaceTeamDefinition>? Teams = null,
    TrackLimitEnforcementMode TrackLimitMode = TrackLimitEnforcementMode.WarningsOnly,
    int MinimumRequiredPitStops = 1,
    bool AutomaticCollisionInvestigationsEnabled = false);

public sealed record RaceAdminPenaltyCommand(
    Guid ParticipantId,
    RacePenaltyKind Kind,
    double? ValueSeconds,
    int? GridPlaces,
    string Reason);

public sealed record RaceAdminPenaltyUpdateCommand(
    Guid PenaltyId,
    double? ValueSeconds,
    string? Reason,
    bool IsRevoked);

public sealed record RaceAdminInvestigationCommand(
    Guid InvestigationId,
    bool ApplyPenalty,
    RacePenaltyKind? Kind,
    double? ValueSeconds,
    string? Reason,
    Guid? ParticipantId = null);

public sealed record RaceAdminParticipantCommand(
    Guid ParticipantId,
    RaceParticipantStatus Status,
    string? Reason);

public sealed record RaceAdminDisconnectCommand(Guid ClientId);

public sealed record RaceAdminCollisionInvestigationSettingsCommand(bool Enabled);

public static partial class RaceProtocolValidation
{
    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex ThemeColorPattern();

    public static string NormalizeDisplayName(string? value)
    {
        var normalized = NormalizeSingleLine(value, RaceProtocol.MaximumDisplayNameLength);
        if (normalized.Length is < 2 or > RaceProtocol.MaximumDisplayNameLength)
            throw new ArgumentException($"Display name must contain 2-{RaceProtocol.MaximumDisplayNameLength} characters.", nameof(value));
        return normalized;
    }

    public static string? NormalizeTeamName(string? value)
    {
        var normalized = NormalizeSingleLine(value, RaceProtocol.MaximumTeamNameLength);
        return normalized.Length == 0 ? null : normalized;
    }

    public static string NormalizeThemeColor(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (!ThemeColorPattern().IsMatch(normalized))
            throw new ArgumentException("Theme color must use #RRGGBB format.", nameof(value));
        return normalized.ToUpperInvariant();
    }

    public static RaceTelemetryUpdate NormalizeTelemetry(RaceTelemetryUpdate value) => value with
    {
        TrackProgress = FiniteClamp(value.TrackProgress, 0, 1),
        LateralOffsetMeters = FiniteClamp(value.LateralOffsetMeters, -500, 500),
        MapX = FiniteClamp(value.MapX, 0, 1),
        MapY = FiniteClamp(value.MapY, 0, 1),
        SpeedKph = FiniteClamp(value.SpeedKph, 0, 800),
        CompletedLaps = Math.Clamp(value.CompletedLaps, 0, 9999),
        CurrentSector = Math.Clamp(value.CurrentSector, 0, 99),
        CurrentLapSeconds = FiniteClamp(value.CurrentLapSeconds, 0, 86_400),
        PitServiceElapsedSeconds = FiniteClamp(value.PitServiceElapsedSeconds, 0, 60),
        CompletedPitServices = Math.Clamp(value.CompletedPitServices, 0, 999),
        TrackToleranceMeters = value.TrackToleranceMeters > 0
            ? FiniteClamp(value.TrackToleranceMeters, 4, 50)
            : 18,
        TrackLengthMeters = value.TrackLengthMeters > 0
            ? FiniteClamp(value.TrackLengthMeters, 50, 100_000)
            : 0,
        PitSpeedLimitKph = value.PitSpeedLimitKph > 0
            ? FiniteClamp(value.PitSpeedLimitKph, 10, 300)
            : 0,
        PitLaneElapsedSeconds = FiniteClamp(value.PitLaneElapsedSeconds, 0, 86_400),
        WorldX = FiniteClamp(value.WorldX, -10_000_000, 10_000_000),
        WorldY = FiniteClamp(value.WorldY, -10_000_000, 10_000_000),
        WorldZ = FiniteClamp(value.WorldZ, -10_000_000, 10_000_000),
        VelocityX = FiniteClamp(value.VelocityX, -500, 500),
        VelocityY = FiniteClamp(value.VelocityY, -500, 500),
        VelocityZ = FiniteClamp(value.VelocityZ, -500, 500),
        ImpactSequence = Math.Max(0, value.ImpactSequence),
        ImpactMagnitudeMps = FiniteClamp(value.ImpactMagnitudeMps, 0, 200),
        ImpactSpeedLossMps = FiniteClamp(value.ImpactSpeedLossMps, 0, 200),
        ImpactWorldX = FiniteClamp(value.ImpactWorldX, -10_000_000, 10_000_000),
        ImpactWorldY = FiniteClamp(value.ImpactWorldY, -10_000_000, 10_000_000),
        ImpactWorldZ = FiniteClamp(value.ImpactWorldZ, -10_000_000, 10_000_000),
        ImpactAgeMilliseconds = Math.Clamp(value.ImpactAgeMilliseconds, 0, 2_000)
    };

    private static string NormalizeSingleLine(string? value, int maximumLength)
    {
        var source = value?.Trim() ?? string.Empty;
        var filtered = new string(source
            .Where(character => !char.IsControl(character) && character is not '\r' and not '\n')
            .Take(maximumLength)
            .ToArray());
        return string.Join(' ', filtered.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static double FiniteClamp(double value, double minimum, double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : minimum;
}
