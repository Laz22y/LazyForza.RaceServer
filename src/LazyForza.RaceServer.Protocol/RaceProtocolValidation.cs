using System.Text.RegularExpressions;

namespace LazyForza.RaceServer.Protocol;

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
        ImpactAgeMilliseconds = Math.Clamp(value.ImpactAgeMilliseconds, 0, 2_000),
        WorldVelocityX = FiniteClamp(value.WorldVelocityX, -500, 500),
        WorldVelocityY = FiniteClamp(value.WorldVelocityY, -500, 500),
        WorldVelocityZ = FiniteClamp(value.WorldVelocityZ, -500, 500),
        ImpactWorldVelocityX = FiniteClamp(value.ImpactWorldVelocityX, -500, 500),
        ImpactWorldVelocityY = FiniteClamp(value.ImpactWorldVelocityY, -500, 500),
        ImpactWorldVelocityZ = FiniteClamp(value.ImpactWorldVelocityZ, -500, 500),
        ImpactSmashableVelDiff = FiniteClamp(value.ImpactSmashableVelDiff, 0, 200),
        ImpactSmashableMass = FiniteClamp(value.ImpactSmashableMass, 0, 100_000),
        ShortcutEvidence = NormalizeShortcutEvidence(value.ShortcutEvidence)
    };

    private static RaceShortcutEvidence? NormalizeShortcutEvidence(RaceShortcutEvidence? value) => value is null
        ? null
        : value with
        {
            DetectedAtMonotonicMilliseconds = Math.Max(0, value.DetectedAtMonotonicMilliseconds),
            StartProgress = FiniteClamp(value.StartProgress, 0, 1),
            EndProgress = FiniteClamp(value.EndProgress, 0, 1),
            RouteDistanceMeters = FiniteClamp(value.RouteDistanceMeters, 0, 1_000),
            WorldDistanceMeters = FiniteClamp(value.WorldDistanceMeters, 0, 1_000),
            GainMeters = FiniteClamp(value.GainMeters, 0, 1_000),
            MaximumLateralOffsetMeters = FiniteClamp(value.MaximumLateralOffsetMeters, 0, 1_000),
            ProtectedRouteMeters = FiniteClamp(value.ProtectedRouteMeters, 0, 1_000),
            TheoreticalSavingMeters = FiniteClamp(value.TheoreticalSavingMeters, 0, 1_000),
            MissedCriticalGates = Math.Clamp(value.MissedCriticalGates, 0, 32),
            Confidence = FiniteClamp(value.Confidence, 0, 1),
            Flags = Math.Clamp(value.Flags, 0, 255)
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
