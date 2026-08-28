using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LazyForza.RaceServer.Core;
using LazyForza.RaceServer.Protocol;

namespace LazyForza.RaceServer.Web;

public sealed record RaceRuleTemplateRules(
    int TotalRaceLaps = 10,
    int MinimumRequiredPitStops = 1,
    int SectorCount = 3,
    bool AutomaticYellowEnabled = true,
    bool AutomaticCollisionInvestigationsEnabled = false,
    bool DisconnectedLapRecoveryEnabled = false,
    double SlowSpeedKph = 12,
    double SlowDurationSeconds = 3,
    double SevereLateralOffsetMeters = 25,
    double RecoveryDurationSeconds = 3,
    TrackLimitEnforcementMode TrackLimitMode = TrackLimitEnforcementMode.WarningsOnly,
    bool AllowTeams = true,
    int TeamCount = 2,
    int DriversPerTeam = 6,
    int CountdownSeconds = 10,
    int PracticeSessionCount = 1,
    IReadOnlyList<int>? PracticeSessionMinutes = null,
    int QualifyingSessionCount = 1,
    IReadOnlyList<int>? QualifyingSessionMinutes = null,
    IReadOnlyList<int?>? QualifyingEliminationCounts = null);

public sealed record RaceRuleTemplateSaveRequest(string Name, RaceRuleTemplateRules? Rules);

public sealed record RaceRuleTemplateSnapshot(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    RaceRuleTemplateRules Rules);

public sealed class RaceRuleTemplateStore
{
    public const int MaximumTemplates = 32;
    private static readonly string[] FallbackTeamColors =
    [
        "#42D7E8", "#FF4057", "#5A8CFF", "#FFD328", "#B86CFF", "#34D17B",
        "#FF8A3D", "#EE4FA6", "#B8F34A", "#8FA3B8", "#6FD6A7", "#F28B82"
    ];
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly object sync = new();
    private readonly string path;
    private List<RaceRuleTemplateSnapshot> templates;

    public RaceRuleTemplateStore(RaceServerOptions options)
    {
        var root = Path.GetFullPath(options.DataDirectory);
        Directory.CreateDirectory(root);
        path = Path.Combine(root, "race-rule-templates.json");
        templates = Load(path);
    }

    public IReadOnlyList<RaceRuleTemplateSnapshot> List()
    {
        lock (sync)
            return templates
                .OrderByDescending(item => item.UpdatedAt)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
    }

    public RaceRuleTemplateSnapshot? Find(Guid id)
    {
        lock (sync) return templates.FirstOrDefault(item => item.Id == id);
    }

    public RaceRuleTemplateSnapshot Create(
        RaceRuleTemplateSaveRequest request,
        DateTimeOffset? createdAt = null)
    {
        lock (sync)
        {
            if (templates.Count >= MaximumTemplates)
                throw new InvalidDataException($"规则模板最多保存 {MaximumTemplates} 个。");
            var name = NormalizeName(request.Name) ??
                throw new InvalidDataException("规则模板名称不能为空。");
            if (templates.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("已经存在同名规则模板。");
            var now = createdAt ?? DateTimeOffset.UtcNow;
            var created = new RaceRuleTemplateSnapshot(
                Guid.NewGuid(),
                name,
                now,
                now,
                NormalizeRules(request.Rules));
            templates.Add(created);
            Save();
            return created;
        }
    }

    public RaceRuleTemplateSnapshot Update(
        Guid id,
        RaceRuleTemplateSaveRequest request,
        DateTimeOffset? updatedAt = null)
    {
        lock (sync)
        {
            var index = templates.FindIndex(item => item.Id == id);
            if (index < 0) throw new KeyNotFoundException("规则模板不存在。");
            var name = NormalizeName(request.Name) ??
                throw new InvalidDataException("规则模板名称不能为空。");
            if (templates.Any(item => item.Id != id &&
                    string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("已经存在同名规则模板。");
            var previous = templates[index];
            var updated = previous with
            {
                Name = name,
                UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow,
                Rules = NormalizeRules(request.Rules)
            };
            templates[index] = updated;
            Save();
            return updated;
        }
    }

    public bool Delete(Guid id)
    {
        lock (sync)
        {
            var removed = templates.RemoveAll(item => item.Id == id) > 0;
            if (removed) Save();
            return removed;
        }
    }

    public static RaceAdminRoomSettingsCommand MergeWithRoom(
        RaceRuleTemplateSnapshot template,
        RaceRoomSettingsSnapshot current)
    {
        var rules = NormalizeRules(template.Rules);
        var teams = ResizeTeams(current.Teams, rules.TeamCount);
        return new RaceAdminRoomSettingsCommand(
            current.SessionName,
            rules.TotalRaceLaps,
            rules.SectorCount,
            rules.AutomaticYellowEnabled,
            rules.SlowSpeedKph,
            rules.SlowDurationSeconds,
            rules.SevereLateralOffsetMeters,
            rules.RecoveryDurationSeconds,
            rules.AllowTeams,
            current.TrackName,
            current.TrackId,
            current.TrackRevision,
            current.TrackPackageHash,
            rules.TeamCount,
            rules.DriversPerTeam,
            teams,
            rules.TrackLimitMode,
            rules.MinimumRequiredPitStops,
            rules.AutomaticCollisionInvestigationsEnabled,
            rules.DisconnectedLapRecoveryEnabled);
    }

    public static RaceRuleTemplateRules NormalizeRules(RaceRuleTemplateRules? rules)
    {
        rules ??= new RaceRuleTemplateRules();
        var practiceCount = Math.Clamp(rules.PracticeSessionCount, 1, 3);
        var qualifyingCount = Math.Clamp(rules.QualifyingSessionCount, 1, 3);
        return rules with
        {
            TotalRaceLaps = Math.Clamp(rules.TotalRaceLaps, 1, 999),
            MinimumRequiredPitStops = Math.Clamp(rules.MinimumRequiredPitStops, 0, 20),
            SectorCount = Math.Clamp(rules.SectorCount, 1, 20),
            SlowSpeedKph = Math.Clamp(rules.SlowSpeedKph, 3, 50),
            SlowDurationSeconds = Math.Clamp(rules.SlowDurationSeconds, 1, 15),
            SevereLateralOffsetMeters = Math.Clamp(rules.SevereLateralOffsetMeters, 5, 200),
            RecoveryDurationSeconds = Math.Clamp(rules.RecoveryDurationSeconds, 1, 15),
            TrackLimitMode = Enum.IsDefined(rules.TrackLimitMode)
                ? rules.TrackLimitMode
                : TrackLimitEnforcementMode.WarningsOnly,
            TeamCount = Math.Clamp(rules.TeamCount, 1, RaceProtocol.MaximumParticipants),
            DriversPerTeam = Math.Clamp(rules.DriversPerTeam, 1, RaceProtocol.MaximumParticipants),
            CountdownSeconds = Math.Clamp(rules.CountdownSeconds, 0, 120),
            PracticeSessionCount = practiceCount,
            PracticeSessionMinutes = NormalizeMinutes(rules.PracticeSessionMinutes, practiceCount, [60, 60, 60]),
            QualifyingSessionCount = qualifyingCount,
            QualifyingSessionMinutes = NormalizeMinutes(
                rules.QualifyingSessionMinutes,
                qualifyingCount,
                qualifyingCount == 1 ? [10] : [18, 15, 12]),
            QualifyingEliminationCounts = Enumerable.Range(0, Math.Max(0, qualifyingCount - 1))
                .Select(index => (int?)(index < (rules.QualifyingEliminationCounts?.Count ?? 0)
                    ? rules.QualifyingEliminationCounts![index] is int value
                        ? Math.Clamp(value, 0, 11)
                        : null
                    : null))
                .ToArray()
        };
    }

    private static IReadOnlyList<int> NormalizeMinutes(
        IReadOnlyList<int>? source,
        int count,
        IReadOnlyList<int> fallback) =>
        Enumerable.Range(0, count)
            .Select(index => Math.Clamp(
                index < (source?.Count ?? 0)
                    ? source![index]
                    : fallback[Math.Min(index, fallback.Count - 1)],
                1,
                180))
            .ToArray();

    private static IReadOnlyList<RaceTeamDefinition> ResizeTeams(
        IReadOnlyList<RaceTeamDefinition>? source,
        int requestedCount)
    {
        var count = Math.Clamp(requestedCount, 1, RaceProtocol.MaximumParticipants);
        var current = source ?? [];
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<RaceTeamDefinition>(count);
        for (var index = 0; index < count; index++)
        {
            var existing = index < current.Count ? current[index] : null;
            var id = string.IsNullOrWhiteSpace(existing?.Id) ? $"team-{index + 1}" : existing.Id;
            while (!ids.Add(id)) id += "-next";
            var name = string.IsNullOrWhiteSpace(existing?.Name) ? $"车队 {index + 1}" : existing.Name;
            while (!names.Add(name)) name += "-";
            var color = existing?.ThemeColor;
            try { color = RaceProtocolValidation.NormalizeThemeColor(color); }
            catch (ArgumentException) { color = FallbackTeamColors[index % FallbackTeamColors.Length]; }
            result.Add(new RaceTeamDefinition(id, name, color));
        }
        return result;
    }

    private void Save()
    {
        var document = new StoredDocument(1, templates);
        var json = JsonSerializer.Serialize(document, JsonOptions);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }

    private static List<RaceRuleTemplateSnapshot> Load(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            var document = JsonSerializer.Deserialize<StoredDocument>(File.ReadAllText(path), JsonOptions);
            if (document is null || document.Version != 1 || document.Templates is null)
                throw new JsonException("Unsupported rule-template document.");
            return document.Templates
                .Where(item => item.Id != Guid.Empty && NormalizeName(item.Name) is not null)
                .Take(MaximumTemplates)
                .Select(item => item with
                {
                    Name = NormalizeName(item.Name)!,
                    Rules = NormalizeRules(item.Rules)
                })
                .ToList();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"规则模板文件无法读取：{path}", exception);
        }
    }

    private static string? NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = new string(value.Trim().Where(character => !char.IsControl(character)).Take(64).ToArray());
        return normalized.Length == 0 ? null : normalized;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record StoredDocument(int Version, IReadOnlyList<RaceRuleTemplateSnapshot>? Templates);
}
