using LazyForza.RaceServer.Protocol;

namespace LazyForza.RaceServer.Core;

public sealed record RaceServerOptions
{
    public string ServerName { get; init; } = "LazyForza 地产赛事";
    public string PlayerPassword { get; init; } = "change-me";
    public string AdminPassword { get; init; } = "change-admin-me";
    public string SessionName { get; init; } = "地产赛事";
    public int MaximumParticipants { get; init; } = RaceProtocol.MaximumParticipants;
    public int TotalRaceLaps { get; init; } = 10;
    public int SectorCount { get; init; } = 3;
    public bool AutomaticYellowEnabled { get; init; } = true;
    public double SlowSpeedKph { get; init; } = 12;
    public double SlowDurationSeconds { get; init; } = 3;
    public double SevereLateralOffsetMeters { get; init; } = 25;
    public double RecoveryDurationSeconds { get; init; } = 3;
    public string? TrackId { get; init; }
    public string? TrackName { get; init; }
    public string? TrackRevision { get; init; }
    public string? TrackPackageHash { get; init; }
    public string DataDirectory { get; init; } = "data";

    public RaceServerOptions Normalize() => this with
    {
        ServerName = NormalizeRequired(ServerName, "LazyForza 地产赛事", 64),
        SessionName = NormalizeRequired(SessionName, "地产赛事", 64),
        PlayerPassword = NormalizePlayerPassword(PlayerPassword),
        AdminPassword = NormalizeAdminPassword(AdminPassword),
        MaximumParticipants = Math.Clamp(MaximumParticipants, 2, RaceProtocol.MaximumParticipants),
        TotalRaceLaps = Math.Clamp(TotalRaceLaps, 1, 999),
        SectorCount = Math.Clamp(SectorCount, 1, 20),
        SlowSpeedKph = Math.Clamp(SlowSpeedKph, 3, 50),
        SlowDurationSeconds = Math.Clamp(SlowDurationSeconds, 1, 15),
        SevereLateralOffsetMeters = Math.Clamp(SevereLateralOffsetMeters, 5, 200),
        RecoveryDurationSeconds = Math.Clamp(RecoveryDurationSeconds, 1, 15),
        TrackId = NormalizeOptional(TrackId, 128),
        TrackName = NormalizeOptional(TrackName, 128),
        TrackRevision = NormalizeOptional(TrackRevision, 64),
        TrackPackageHash = NormalizeOptional(TrackPackageHash, 128),
        DataDirectory = string.IsNullOrWhiteSpace(DataDirectory) ? "data" : DataDirectory.Trim()
    };

    private static string NormalizeRequired(string? value, string fallback, int maximum) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim()[..Math.Min(value.Trim().Length, maximum)];

    private static string? NormalizeOptional(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, maximum)];

    private static string NormalizePlayerPassword(string? value)
    {
        var normalized = value ?? string.Empty;
        if (normalized.Length > 128)
            throw new InvalidOperationException("RaceServer:PlayerPassword must not exceed 128 characters.");
        return normalized;
    }

    private static string NormalizeAdminPassword(string? value)
    {
        var normalized = value ?? string.Empty;
        if (normalized.Length is < 8 or > 128)
            throw new InvalidOperationException("RaceServer:AdminPassword must contain 8-128 characters.");
        return normalized;
    }
}
