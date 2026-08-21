using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LazyForza.RaceServer.Core;
using LazyForza.RaceServer.Protocol;

namespace LazyForza.RaceServer.Web;

public sealed record RaceServerInitialSetupRequest(
    string PlayerPassword,
    string AdminPassword,
    string SessionName,
    int TotalRaceLaps,
    int SectorCount);

public sealed class RaceServerConfigurationStore
{
    private const int Iterations = 180_000;
    private readonly object sync = new();
    private readonly RaceServerOptions fallback;
    private readonly string settingsPath;
    private StoredServerConfiguration? stored;

    public RaceServerConfigurationStore(RaceServerOptions options)
    {
        fallback = options;
        var root = Path.GetFullPath(options.DataDirectory);
        Directory.CreateDirectory(root);
        settingsPath = Path.Combine(root, "server-settings.json");
        stored = Load(settingsPath);
        if (stored is null && HasExplicitPasswords(options))
            stored = CreateConfiguration(
                options.PlayerPassword,
                options.AdminPassword,
                new RaceRoomSettingsSnapshot(
                    options.SessionName,
                    options.TotalRaceLaps,
                    options.SectorCount,
                    options.AutomaticYellowEnabled,
                    options.SlowSpeedKph,
                    options.SlowDurationSeconds,
                    options.SevereLateralOffsetMeters,
                    options.RecoveryDurationSeconds,
                    true,
                    options.TrackName,
                    options.TrackId,
                    options.TrackRevision,
                    options.TrackPackageHash,
                    options.TeamCount,
                    options.DriversPerTeam,
                    options.Teams,
                    options.TrackLimitMode,
                    options.MinimumRequiredPitStops,
                    options.AutomaticCollisionInvestigationsEnabled,
                    options.DisconnectedLapRecoveryEnabled));
    }

    public bool IsConfigured
    {
        get { lock (sync) return stored is not null; }
    }

    public RaceRoomSettingsSnapshot InitialRoomSettings
    {
        get
        {
            lock (sync)
                return stored?.Room ?? new RaceRoomSettingsSnapshot(
                    fallback.SessionName,
                    fallback.TotalRaceLaps,
                    fallback.SectorCount,
                    fallback.AutomaticYellowEnabled,
                    fallback.SlowSpeedKph,
                    fallback.SlowDurationSeconds,
                    fallback.SevereLateralOffsetMeters,
                    fallback.RecoveryDurationSeconds,
                    true,
                    fallback.TrackName,
                    fallback.TrackId,
                    fallback.TrackRevision,
                    fallback.TrackPackageHash,
                    fallback.TeamCount,
                    fallback.DriversPerTeam,
                    fallback.Teams,
                    fallback.TrackLimitMode,
                    fallback.MinimumRequiredPitStops,
                    fallback.AutomaticCollisionInvestigationsEnabled,
                    fallback.DisconnectedLapRecoveryEnabled);
        }
    }

    public bool PlayerPasswordMatches(string password)
    {
        lock (sync) return stored is not null && Verify(password, stored.PlayerPassword);
    }

    public bool AdminPasswordMatches(string password)
    {
        lock (sync) return stored is not null && Verify(password, stored.AdminPassword);
    }

    public (bool Success, string? Error, RaceRoomSettingsSnapshot? Settings) ConfigureInitial(
        RaceServerInitialSetupRequest request)
    {
        lock (sync)
        {
            if (stored is not null) return (false, "服务端已经完成首次设置。", null);
            var validation = ValidatePasswords(request.PlayerPassword, request.AdminPassword);
            if (validation is not null) return (false, validation, null);
            var name = NormalizeName(request.SessionName);
            if (name is null) return (false, "赛事名称不能为空。", null);
            var room = new RaceRoomSettingsSnapshot(
                name,
                Math.Clamp(request.TotalRaceLaps, 1, 999),
                Math.Clamp(request.SectorCount, 1, 20),
                true,
                fallback.SlowSpeedKph,
                fallback.SlowDurationSeconds,
                fallback.SevereLateralOffsetMeters,
                fallback.RecoveryDurationSeconds,
                true,
                fallback.TrackName,
                fallback.TrackId,
                fallback.TrackRevision,
                fallback.TrackPackageHash,
                fallback.TeamCount,
                fallback.DriversPerTeam,
                fallback.Teams,
                fallback.TrackLimitMode,
                fallback.MinimumRequiredPitStops,
                fallback.AutomaticCollisionInvestigationsEnabled,
                fallback.DisconnectedLapRecoveryEnabled);
            stored = CreateConfiguration(request.PlayerPassword, request.AdminPassword, room);
            Save(stored);
            return (true, null, room);
        }
    }

    public void SaveRoomSettings(RaceRoomSettingsSnapshot settings)
    {
        lock (sync)
        {
            if (stored is null) throw new InvalidOperationException("服务端尚未完成首次设置。");
            stored = stored with { Room = settings };
            Save(stored);
        }
    }

    private void Save(StoredServerConfiguration configuration)
    {
        var json = JsonSerializer.Serialize(configuration, new JsonSerializerOptions { WriteIndented = true });
        var temporary = settingsPath + ".tmp";
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        File.Move(temporary, settingsPath, true);
    }

    private static StoredServerConfiguration? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<StoredServerConfiguration>(File.ReadAllText(path));
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"服务端设置文件无法读取：{path}", exception);
        }
    }

    private static bool HasExplicitPasswords(RaceServerOptions options) =>
        options.PlayerPassword != "change-me" &&
        options.AdminPassword != "change-admin-me";

    private static string? ValidatePasswords(string player, string admin)
    {
        if (player.Length > 128) return "房间密码不能超过 128 个字符。";
        if (admin.Length is < 8 or > 128) return "总控密码需要 8–128 个字符。";
        if (string.Equals(player, admin, StringComparison.Ordinal)) return "房间密码和总控密码不能相同。";
        return null;
    }

    private static string? NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = new string(value.Trim().Where(character => !char.IsControl(character)).Take(64).ToArray());
        return clean.Length == 0 ? null : clean;
    }

    private static StoredServerConfiguration CreateConfiguration(
        string playerPassword,
        string adminPassword,
        RaceRoomSettingsSnapshot room) => new(
            1,
            Hash(playerPassword),
            Hash(adminPassword),
            room);

    private static StoredPassword Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            32);
        return new StoredPassword(Convert.ToBase64String(salt), Convert.ToBase64String(hash), Iterations);
    }

    private static bool Verify(string password, StoredPassword stored)
    {
        try
        {
            var salt = Convert.FromBase64String(stored.Salt);
            var expected = Convert.FromBase64String(stored.Hash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                Math.Clamp(stored.Iterations, 100_000, 1_000_000),
                HashAlgorithmName.SHA256,
                expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed record StoredPassword(string Salt, string Hash, int Iterations);
    private sealed record StoredServerConfiguration(
        int Version,
        StoredPassword PlayerPassword,
        StoredPassword AdminPassword,
        RaceRoomSettingsSnapshot Room);
}
