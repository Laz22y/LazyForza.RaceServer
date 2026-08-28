using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LazyForza.RaceServer.Core;
using LazyForza.RaceServer.Protocol;

namespace LazyForza.RaceServer.Web;

public enum RaceEventProjectStatus
{
    Draft,
    Active,
    Completed,
    Archived
}

public sealed record RaceEventSchedule(
    int CountdownSeconds = 10,
    int PracticeSessionCount = 1,
    IReadOnlyList<int>? PracticeSessionMinutes = null,
    int QualifyingSessionCount = 1,
    IReadOnlyList<int>? QualifyingSessionMinutes = null,
    IReadOnlyList<int?>? QualifyingEliminationCounts = null);

public sealed record RaceEventProjectSaveRequest(
    string Name,
    string? ShortName,
    string? Organizer,
    string? Description,
    DateTimeOffset? ScheduledStartAt,
    string? TimeZoneId,
    RaceEventSchedule? Schedule);

public sealed record RaceEventProjectCopyRequest(string? Name);

public sealed record RaceEventProjectAssetSnapshot(
    string PackagePath,
    string FileName,
    string MimeType,
    string Sha256,
    long SizeBytes);

public sealed record RaceEventProjectAuditSnapshot(
    long Sequence,
    DateTimeOffset OccurredAt,
    string Type,
    string Message,
    Guid? ParticipantId = null);

public sealed record RaceEventProjectSnapshot(
    Guid Id,
    string Name,
    string? ShortName,
    string? Organizer,
    string? Description,
    DateTimeOffset? ScheduledStartAt,
    string TimeZoneId,
    RaceEventProjectStatus Status,
    int Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? CompletedAt,
    RaceRoomSettingsSnapshot Room,
    RaceEventSchedule Schedule,
    RaceEventProjectAssetSnapshot? TrackPackage,
    RaceEventProjectAssetSnapshot? OrganizerLogo,
    IReadOnlyList<RaceStageResultSnapshot> Results,
    IReadOnlyList<RaceEventProjectAuditSnapshot> AuditEvents);

public sealed record RaceEventProjectSummary(
    Guid Id,
    string Name,
    string? ShortName,
    string? Organizer,
    DateTimeOffset? ScheduledStartAt,
    string TimeZoneId,
    RaceEventProjectStatus Status,
    int Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? TrackName,
    int ResultCount,
    int EntrantCount,
    int AuditEventCount,
    bool HasTrackPackage,
    bool HasOrganizerLogo);

public sealed record RaceEventProjectAssets(byte[]? TrackPackage, byte[]? OrganizerLogo);

public sealed class RaceEventProjectStore
{
    public const int MaximumProjects = 64;
    public const long MaximumPackageBytes = 4L * 1024 * 1024;
    private const int MaximumEntries = 10;
    private const int MaximumJsonEntryBytes = 1024 * 1024;
    private const int MaximumAuditEvents = 2_000;
    private const string Format = "lazyforza-event-project";
    private const int FormatVersion = 1;
    private const string ManifestPath = "manifest.json";
    private const string EventPath = "event.json";
    private const string SchedulePath = "schedule.json";
    private const string RulesPath = "rules.json";
    private const string EntrantsPath = "entrants.json";
    private const string ResultsPath = "results/stages.json";
    private const string AuditPath = "audit/events.json";
    private const string TrackPath = "track/current.lfzestate";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly HashSet<string> AllowedPaths =
    [
        ManifestPath, EventPath, SchedulePath, RulesPath, EntrantsPath,
        ResultsPath, AuditPath, TrackPath, "assets/organizer-logo.png", "assets/organizer-logo.jpg"
    ];

    private readonly object sync = new();
    private readonly string root;
    private readonly string indexPath;
    private List<RaceEventProjectSnapshot> projects;

    public RaceEventProjectStore(RaceServerOptions options)
    {
        root = Path.Combine(Path.GetFullPath(options.DataDirectory), "event-projects");
        Directory.CreateDirectory(root);
        indexPath = Path.Combine(root, "index.json");
        projects = Load(indexPath);
    }

    public IReadOnlyList<RaceEventProjectSummary> List()
    {
        lock (sync)
            return projects
                .OrderBy(item => item.Status == RaceEventProjectStatus.Active ? 0 : 1)
                .ThenByDescending(item => item.UpdatedAt)
                .Select(Summarize)
                .ToArray();
    }

    public RaceEventProjectSnapshot? Find(Guid id)
    {
        lock (sync) return projects.FirstOrDefault(item => item.Id == id);
    }

    public RaceEventProjectSnapshot Create(
        RaceEventProjectSaveRequest request,
        RaceRoomSettingsSnapshot room,
        IReadOnlyList<RaceStageResultSnapshot> results,
        IReadOnlyList<RaceEventSnapshot> events,
        HostedTrackPackageMetadata? trackMetadata,
        byte[]? trackBytes,
        HostedOrganizerLogoMetadata? logoMetadata,
        byte[]? logoBytes,
        DateTimeOffset? createdAt = null)
    {
        lock (sync)
        {
            if (projects.Count >= MaximumProjects)
                throw new InvalidDataException($"赛事项目最多保存 {MaximumProjects} 个。");
            var now = createdAt ?? DateTimeOffset.UtcNow;
            var id = Guid.NewGuid();
            var project = BuildSnapshot(
                id, request, room, results, ConvertEvents(events),
                AssetForTrack(trackMetadata, trackBytes), AssetForLogo(logoMetadata, logoBytes),
                RaceEventProjectStatus.Draft, 1, now, now, null, null);
            SaveAssets(id, project, trackBytes, logoBytes);
            projects.Add(project);
            Save();
            return project;
        }
    }

    public RaceEventProjectSnapshot Capture(
        Guid id,
        RaceEventProjectSaveRequest request,
        RaceRoomSettingsSnapshot room,
        IReadOnlyList<RaceStageResultSnapshot> results,
        IReadOnlyList<RaceEventSnapshot> events,
        HostedTrackPackageMetadata? trackMetadata,
        byte[]? trackBytes,
        HostedOrganizerLogoMetadata? logoMetadata,
        byte[]? logoBytes,
        DateTimeOffset? updatedAt = null)
    {
        lock (sync)
        {
            var index = projects.FindIndex(item => item.Id == id);
            if (index < 0) throw new KeyNotFoundException("赛事项目不存在。");
            var previous = projects[index];
            if (previous.Status == RaceEventProjectStatus.Archived)
                throw new InvalidDataException("已归档的赛事项目不能再修改。");
            var now = updatedAt ?? DateTimeOffset.UtcNow;
            var updated = BuildSnapshot(
                id, request, room, results, ConvertEvents(events),
                AssetForTrack(trackMetadata, trackBytes), AssetForLogo(logoMetadata, logoBytes),
                previous.Status, previous.Revision + 1, previous.CreatedAt, now,
                previous.ActivatedAt, previous.CompletedAt);
            SaveAssets(id, updated, trackBytes, logoBytes);
            projects[index] = updated;
            Save();
            return updated;
        }
    }

    public RaceEventProjectSnapshot Copy(Guid id, string? requestedName, DateTimeOffset? copiedAt = null)
    {
        lock (sync)
        {
            if (projects.Count >= MaximumProjects)
                throw new InvalidDataException($"赛事项目最多保存 {MaximumProjects} 个。");
            var source = projects.FirstOrDefault(item => item.Id == id) ??
                         throw new KeyNotFoundException("赛事项目不存在。");
            var now = copiedAt ?? DateTimeOffset.UtcNow;
            var newId = Guid.NewGuid();
            var name = NormalizeRequired(requestedName, 96) ?? $"{source.Name} - 副本";
            var copy = source with
            {
                Id = newId,
                Name = name,
                Status = RaceEventProjectStatus.Draft,
                Revision = 1,
                CreatedAt = now,
                UpdatedAt = now,
                ActivatedAt = null,
                CompletedAt = null,
                Results = [],
                AuditEvents = []
            };
            var assets = ReadAssetsInternal(source);
            SaveAssets(newId, copy, assets.TrackPackage, assets.OrganizerLogo);
            projects.Add(copy);
            Save();
            return copy;
        }
    }

    public RaceEventProjectSnapshot Activate(Guid id, DateTimeOffset? activatedAt = null)
    {
        lock (sync)
        {
            var index = projects.FindIndex(item => item.Id == id);
            if (index < 0) throw new KeyNotFoundException("赛事项目不存在。");
            if (projects[index].Status == RaceEventProjectStatus.Archived)
                throw new InvalidDataException("已归档的赛事项目不能直接启用，请先复制为新项目。");
            var now = activatedAt ?? DateTimeOffset.UtcNow;
            for (var candidateIndex = 0; candidateIndex < projects.Count; candidateIndex++)
            {
                if (candidateIndex == index || projects[candidateIndex].Status != RaceEventProjectStatus.Active) continue;
                var previous = projects[candidateIndex];
                projects[candidateIndex] = previous with
                {
                    Status = previous.Results.Count > 0 ? RaceEventProjectStatus.Completed : RaceEventProjectStatus.Draft,
                    Revision = previous.Revision + 1,
                    UpdatedAt = now,
                    CompletedAt = previous.Results.Count > 0 ? now : null
                };
            }
            var project = projects[index];
            project = project with
            {
                Status = RaceEventProjectStatus.Active,
                Revision = project.Revision + 1,
                UpdatedAt = now,
                ActivatedAt = project.ActivatedAt ?? now,
                CompletedAt = null
            };
            projects[index] = project;
            Save();
            return project;
        }
    }

    public RaceEventProjectSnapshot SetStatus(
        Guid id,
        RaceEventProjectStatus status,
        DateTimeOffset? changedAt = null)
    {
        lock (sync)
        {
            if (status is RaceEventProjectStatus.Active or RaceEventProjectStatus.Draft)
                throw new InvalidDataException("请使用启用或复制操作改变赛事项目状态。");
            var index = projects.FindIndex(item => item.Id == id);
            if (index < 0) throw new KeyNotFoundException("赛事项目不存在。");
            var previous = projects[index];
            if (status == RaceEventProjectStatus.Archived && previous.Status == RaceEventProjectStatus.Active)
                throw new InvalidDataException("请先完成赛事，再归档项目。");
            var now = changedAt ?? DateTimeOffset.UtcNow;
            var updated = previous with
            {
                Status = status,
                Revision = previous.Revision + 1,
                UpdatedAt = now,
                CompletedAt = status == RaceEventProjectStatus.Completed ? now : previous.CompletedAt
            };
            projects[index] = updated;
            Save();
            return updated;
        }
    }

    public bool Delete(Guid id)
    {
        lock (sync)
        {
            var project = projects.FirstOrDefault(item => item.Id == id);
            if (project is null) return false;
            if (project.Status == RaceEventProjectStatus.Active)
                throw new InvalidDataException("正在使用的赛事项目不能删除。");
            projects.Remove(project);
            var assetsPath = AssetsPath(id);
            if (Directory.Exists(assetsPath)) Directory.Delete(assetsPath, recursive: true);
            Save();
            return true;
        }
    }

    public RaceEventProjectAssets ReadAssets(Guid id)
    {
        lock (sync)
        {
            var project = projects.FirstOrDefault(item => item.Id == id) ??
                          throw new KeyNotFoundException("赛事项目不存在。");
            return ReadAssetsInternal(project);
        }
    }

    public byte[] Export(Guid id)
    {
        lock (sync)
        {
            var project = projects.FirstOrDefault(item => item.Id == id) ??
                          throw new KeyNotFoundException("赛事项目不存在。");
            return BuildPackage(project, ReadAssetsInternal(project));
        }
    }

    public RaceEventProjectSnapshot Import(byte[] packageBytes, DateTimeOffset? importedAt = null)
    {
        if (packageBytes.LongLength is <= 0 or > MaximumPackageBytes)
            throw new InvalidDataException("赛事项目包为空或超过 4 MiB 上限。");
        var imported = ReadPackage(packageBytes);
        lock (sync)
        {
            if (projects.Count >= MaximumProjects)
                throw new InvalidDataException($"赛事项目最多保存 {MaximumProjects} 个。");
            var now = importedAt ?? DateTimeOffset.UtcNow;
            var id = projects.Any(item => item.Id == imported.Project.Id) ? Guid.NewGuid() : imported.Project.Id;
            var project = imported.Project with
            {
                Id = id,
                Status = RaceEventProjectStatus.Draft,
                Revision = Math.Max(1, imported.Project.Revision),
                UpdatedAt = now,
                ActivatedAt = null,
                CompletedAt = null
            };
            SaveAssets(id, project, imported.Assets.TrackPackage, imported.Assets.OrganizerLogo);
            projects.Add(project);
            Save();
            return project;
        }
    }

    public void SyncActive(
        IReadOnlyList<RaceStageResultSnapshot> results,
        IReadOnlyList<RaceEventSnapshot> events,
        DateTimeOffset? synchronizedAt = null)
    {
        lock (sync)
        {
            var index = projects.FindIndex(item => item.Status == RaceEventProjectStatus.Active);
            if (index < 0) return;
            var project = projects[index];
            var mergedResults = MergeResults(project.Results, results);
            var mergedEvents = MergeEvents(project.AuditEvents, events);
            if (SerializedEquals(mergedResults, project.Results) &&
                SerializedEquals(mergedEvents, project.AuditEvents)) return;
            projects[index] = project with
            {
                Results = mergedResults,
                AuditEvents = mergedEvents,
                Revision = project.Revision + 1,
                UpdatedAt = synchronizedAt ?? DateTimeOffset.UtcNow
            };
            Save();
        }
    }

    public static string SafeExportFileName(RaceEventProjectSnapshot project)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(project.Name.Select(character => invalid.Contains(character) ? '-' : character).ToArray()).Trim();
        return (string.IsNullOrWhiteSpace(name) ? "lazyforza-event" : name) + ".lfzevent";
    }

    private static RaceEventProjectSnapshot BuildSnapshot(
        Guid id,
        RaceEventProjectSaveRequest request,
        RaceRoomSettingsSnapshot room,
        IReadOnlyList<RaceStageResultSnapshot> results,
        IReadOnlyList<RaceEventProjectAuditSnapshot> events,
        RaceEventProjectAssetSnapshot? track,
        RaceEventProjectAssetSnapshot? logo,
        RaceEventProjectStatus status,
        int revision,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? activatedAt,
        DateTimeOffset? completedAt)
    {
        var name = NormalizeRequired(request.Name, 96) ??
                   throw new InvalidDataException("赛事项目名称不能为空。");
        return new RaceEventProjectSnapshot(
            id,
            name,
            NormalizeOptional(request.ShortName, 32),
            NormalizeOptional(request.Organizer, 96),
            NormalizeOptional(request.Description, 1_000),
            request.ScheduledStartAt?.ToUniversalTime(),
            NormalizeOptional(request.TimeZoneId, 80) ?? "UTC",
            status,
            revision,
            createdAt,
            updatedAt,
            activatedAt,
            completedAt,
            room,
            NormalizeSchedule(request.Schedule),
            track,
            logo,
            NormalizeResults(results),
            NormalizeEvents(events));
    }

    private static RaceEventSchedule NormalizeSchedule(RaceEventSchedule? schedule)
    {
        schedule ??= new RaceEventSchedule();
        var practiceCount = Math.Clamp(schedule.PracticeSessionCount, 1, 3);
        var qualifyingCount = Math.Clamp(schedule.QualifyingSessionCount, 1, 3);
        return new RaceEventSchedule(
            Math.Clamp(schedule.CountdownSeconds, 0, 120),
            practiceCount,
            NormalizeMinutes(schedule.PracticeSessionMinutes, practiceCount, 60),
            qualifyingCount,
            NormalizeMinutes(schedule.QualifyingSessionMinutes, qualifyingCount, 10),
            NormalizeEliminations(schedule.QualifyingEliminationCounts, qualifyingCount - 1));
    }

    private static IReadOnlyList<int> NormalizeMinutes(IReadOnlyList<int>? source, int count, int fallback) =>
        Enumerable.Range(0, count)
            .Select(index => Math.Clamp(source is not null && index < source.Count ? source[index] : fallback, 1, 180))
            .ToArray();

    private static IReadOnlyList<int?> NormalizeEliminations(IReadOnlyList<int?>? source, int count) =>
        Enumerable.Range(0, count)
            .Select(index => source is not null && index < source.Count && source[index].HasValue
                ? Math.Clamp(source[index]!.Value, 0, 11)
                : (int?)null)
            .ToArray();

    private static IReadOnlyList<RaceStageResultSnapshot> NormalizeResults(
        IEnumerable<RaceStageResultSnapshot> values)
    {
        var byId = new Dictionary<Guid, RaceStageResultSnapshot>();
        foreach (var item in values.OrderBy(item => item.CompletedAt)) byId[item.Id] = item;
        return byId.Values.OrderBy(item => item.CompletedAt).TakeLast(24).ToArray();
    }

    private static IReadOnlyList<RaceEventProjectAuditSnapshot> NormalizeEvents(
        IEnumerable<RaceEventProjectAuditSnapshot> values) => values
        .GroupBy(item => item.Sequence)
        .Select(group => group.OrderByDescending(item => item.OccurredAt).First())
        .OrderBy(item => item.Sequence)
        .TakeLast(MaximumAuditEvents)
        .ToArray();

    private static IReadOnlyList<RaceEventProjectAuditSnapshot> ConvertEvents(
        IEnumerable<RaceEventSnapshot> values) => NormalizeEvents(values.Select(item =>
            new RaceEventProjectAuditSnapshot(
                item.Sequence, item.At, item.Type, item.Message, item.ParticipantId)));

    private static IReadOnlyList<RaceStageResultSnapshot> MergeResults(
        IReadOnlyList<RaceStageResultSnapshot> existing,
        IReadOnlyList<RaceStageResultSnapshot> incoming) => NormalizeResults(existing.Concat(incoming));

    private static IReadOnlyList<RaceEventProjectAuditSnapshot> MergeEvents(
        IReadOnlyList<RaceEventProjectAuditSnapshot> existing,
        IReadOnlyList<RaceEventSnapshot> incoming) => NormalizeEvents(existing.Concat(ConvertEvents(incoming)));

    private static bool SerializedEquals<T>(IReadOnlyList<T> left, IReadOnlyList<T> right) =>
        Serialize(left).AsSpan().SequenceEqual(Serialize(right));

    private static RaceEventProjectAssetSnapshot? AssetForTrack(
        HostedTrackPackageMetadata? metadata,
        byte[]? bytes)
    {
        if (metadata is null || bytes is null || bytes.LongLength != metadata.SizeBytes) return null;
        return new RaceEventProjectAssetSnapshot(
            TrackPath, metadata.FileName, "application/vnd.lazyforza.estate-track",
            Convert.ToHexString(SHA256.HashData(bytes)), bytes.LongLength);
    }

    private static RaceEventProjectAssetSnapshot? AssetForLogo(
        HostedOrganizerLogoMetadata? metadata,
        byte[]? bytes)
    {
        if (metadata is null || bytes is null || bytes.LongLength != metadata.SizeBytes) return null;
        var packagePath = metadata.MimeType == "image/png" ? "assets/organizer-logo.png" : "assets/organizer-logo.jpg";
        return new RaceEventProjectAssetSnapshot(
            packagePath, metadata.FileName, metadata.MimeType,
            Convert.ToHexString(SHA256.HashData(bytes)), bytes.LongLength);
    }

    private void SaveAssets(
        Guid id,
        RaceEventProjectSnapshot project,
        byte[]? trackBytes,
        byte[]? logoBytes)
    {
        var directory = AssetsPath(id);
        Directory.CreateDirectory(directory);
        SaveAsset(Path.Combine(directory, "track.lfzestate"), project.TrackPackage, trackBytes);
        SaveAsset(Path.Combine(directory, "organizer-logo.bin"), project.OrganizerLogo, logoBytes);
    }

    private static void SaveAsset(string path, RaceEventProjectAssetSnapshot? metadata, byte[]? bytes)
    {
        if (metadata is null || bytes is null)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        if (bytes.LongLength != metadata.SizeBytes ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), metadata.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("赛事项目素材的长度或摘要不一致。");
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, true);
    }

    private RaceEventProjectAssets ReadAssetsInternal(RaceEventProjectSnapshot project) => new(
        ReadAsset(Path.Combine(AssetsPath(project.Id), "track.lfzestate"), project.TrackPackage),
        ReadAsset(Path.Combine(AssetsPath(project.Id), "organizer-logo.bin"), project.OrganizerLogo));

    private static byte[]? ReadAsset(string path, RaceEventProjectAssetSnapshot? metadata)
    {
        if (metadata is null) return null;
        if (!File.Exists(path)) throw new InvalidDataException("赛事项目素材文件不存在。");
        var bytes = File.ReadAllBytes(path);
        if (bytes.LongLength != metadata.SizeBytes ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), metadata.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("赛事项目素材文件的长度或摘要不一致。");
        return bytes;
    }

    private string AssetsPath(Guid id)
    {
        var path = Path.GetFullPath(Path.Combine(root, id.ToString("D")));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("赛事项目素材目录无效。");
        return path;
    }

    private static byte[] BuildPackage(RaceEventProjectSnapshot project, RaceEventProjectAssets assets)
    {
        var entrants = project.Results
            .SelectMany(result => result.Participants)
            .GroupBy(item => item.Id)
            .Select(group => group.Last())
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(item => new RaceEventEntrant(
                item.Id, item.DisplayName, item.ThemeColor, item.TeamName, item.TeamColor))
            .ToArray();
        var document = new RaceEventDocument(
            project.Id, project.Name, project.ShortName, project.Organizer, project.Description,
            project.ScheduledStartAt, project.TimeZoneId, project.Status, project.Revision,
            project.CreatedAt, project.UpdatedAt, project.ActivatedAt, project.CompletedAt,
            project.TrackPackage, project.OrganizerLogo);
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [EventPath] = Serialize(document),
            [SchedulePath] = Serialize(project.Schedule),
            [RulesPath] = Serialize(project.Room),
            [EntrantsPath] = Serialize(entrants),
            [ResultsPath] = Serialize(project.Results),
            [AuditPath] = Serialize(project.AuditEvents)
        };
        if (project.TrackPackage is not null && assets.TrackPackage is not null)
            payloads.Add(TrackPath, assets.TrackPackage);
        if (project.OrganizerLogo is not null && assets.OrganizerLogo is not null)
            payloads.Add(project.OrganizerLogo.PackagePath, assets.OrganizerLogo);
        var manifest = new RaceEventManifest(
            Format, FormatVersion, project.Id, DateTimeOffset.UtcNow,
            payloads.Select(pair => new RaceEventManifestEntry(
                pair.Key, pair.Value.LongLength, Convert.ToHexString(SHA256.HashData(pair.Value)))).ToArray());

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, ManifestPath, Serialize(manifest));
            foreach (var pair in payloads) WriteEntry(archive, pair.Key, pair.Value);
        }
        if (output.Length > MaximumPackageBytes)
            throw new InvalidDataException("赛事项目包超过 4 MiB 上限。");
        return output.ToArray();
    }

    private static ImportedProject ReadPackage(byte[] packageBytes)
    {
        try
        {
            using var stream = new MemoryStream(packageBytes, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            if (archive.Entries.Count is < 7 or > MaximumEntries)
                throw new InvalidDataException("赛事项目包结构不正确。");
            var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName;
                if (!AllowedPaths.Contains(name) || name.Contains('\\') || name.StartsWith('/') ||
                    name.Split('/').Any(part => part is "" or "." or "..") || entries.ContainsKey(name))
                    throw new InvalidDataException("赛事项目包包含未知、重复或不安全的文件路径。");
                var limit = name is TrackPath ? HostedTrackPackageStore.MaximumPackageBytes :
                    name.StartsWith("assets/", StringComparison.Ordinal) ? HostedOrganizerLogoStore.MaximumLogoBytes :
                    MaximumJsonEntryBytes;
                var bytes = ReadEntry(entry, limit);
                total += bytes.LongLength;
                if (total > MaximumPackageBytes) throw new InvalidDataException("赛事项目包解压后超过 4 MiB 上限。");
                entries.Add(name, bytes);
            }
            var manifest = Deserialize<RaceEventManifest>(Required(entries, ManifestPath));
            if (manifest.Format != Format || manifest.FormatVersion != FormatVersion)
                throw new InvalidDataException("这不是当前服务端支持的 LazyForza 赛事项目包。");
            if (manifest.Entries.Count != entries.Count - 1 ||
                manifest.Entries.Select(item => item.Path).Distinct(StringComparer.Ordinal).Count() != manifest.Entries.Count)
                throw new InvalidDataException("赛事项目包清单与文件数量不一致。");
            foreach (var item in manifest.Entries)
            {
                if (!entries.TryGetValue(item.Path, out var bytes) || bytes.LongLength != item.SizeBytes ||
                    !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), item.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("赛事项目包清单校验失败。");
            }

            var document = Deserialize<RaceEventDocument>(Required(entries, EventPath));
            if (document.Id != manifest.ProjectId)
                throw new InvalidDataException("赛事项目包中的项目标识不一致。");
            var schedule = NormalizeSchedule(Deserialize<RaceEventSchedule>(Required(entries, SchedulePath)));
            var room = Deserialize<RaceRoomSettingsSnapshot>(Required(entries, RulesPath));
            _ = JsonDocument.Parse(Required(entries, EntrantsPath));
            var results = NormalizeResults(Deserialize<List<RaceStageResultSnapshot>>(Required(entries, ResultsPath)));
            var events = NormalizeEvents(Deserialize<List<RaceEventProjectAuditSnapshot>>(Required(entries, AuditPath)));
            var trackBytes = ReadDeclaredAsset(entries, document.TrackPackage, TrackPath);
            var logoBytes = ReadDeclaredAsset(entries, document.OrganizerLogo, document.OrganizerLogo?.PackagePath);
            if (document.OrganizerLogo is null &&
                (entries.ContainsKey("assets/organizer-logo.png") || entries.ContainsKey("assets/organizer-logo.jpg")))
                throw new InvalidDataException("赛事项目包包含未声明的素材文件。");
            if (trackBytes is not null)
            {
                var identity = HostedTrackPackageStore.InspectPackage(trackBytes);
                if (!string.Equals(identity.TrackId, room.TrackId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(identity.TrackRevision, room.TrackRevision, StringComparison.Ordinal) ||
                    !string.Equals(identity.TrackPackageHash, room.TrackPackageHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("赛事项目内的赛道文件与规则快照不一致。");
            }
            if (logoBytes is not null) ValidateLogo(logoBytes, document.OrganizerLogo!.MimeType);

            var request = new RaceEventProjectSaveRequest(
                document.Name, document.ShortName, document.Organizer, document.Description,
                document.ScheduledStartAt, document.TimeZoneId, schedule);
            var project = BuildSnapshot(
                document.Id, request, room, results, events,
                document.TrackPackage, document.OrganizerLogo, document.Status,
                Math.Max(1, document.Revision), document.CreatedAt, document.UpdatedAt,
                document.ActivatedAt, document.CompletedAt);
            return new ImportedProject(project, new RaceEventProjectAssets(trackBytes, logoBytes));
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is JsonException or IOException or NotSupportedException or ArgumentException)
        {
            throw new InvalidDataException("无法读取赛事项目包。", exception);
        }
    }

    private static byte[]? ReadDeclaredAsset(
        IReadOnlyDictionary<string, byte[]> entries,
        RaceEventProjectAssetSnapshot? asset,
        string? expectedPath)
    {
        if (asset is null)
        {
            if (expectedPath is not null && entries.ContainsKey(expectedPath))
                throw new InvalidDataException("赛事项目包包含未声明的素材文件。");
            return null;
        }
        if (expectedPath is null || asset.PackagePath != expectedPath || !entries.TryGetValue(expectedPath, out var bytes) ||
            bytes.LongLength != asset.SizeBytes ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("赛事项目素材清单校验失败。");
        return bytes;
    }

    private static void ValidateLogo(byte[] bytes, string mimeType)
    {
        var png = bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var jpeg = bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
        if ((mimeType == "image/png" && !png) || (mimeType == "image/jpeg" && !jpeg) ||
            mimeType is not ("image/png" or "image/jpeg"))
            throw new InvalidDataException("赛事项目内的 Logo 文件格式无效。");
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, long maximum)
    {
        if (entry.Length > maximum) throw new InvalidDataException("赛事项目包中的文件超过允许大小。");
        using var source = entry.Open();
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (output.Length + read > maximum) throw new InvalidDataException("赛事项目包中的文件超过允许大小。");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] bytes)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using var output = entry.Open();
        output.Write(bytes);
    }

    private static byte[] Required(IReadOnlyDictionary<string, byte[]> entries, string path) =>
        entries.TryGetValue(path, out var bytes) ? bytes : throw new InvalidDataException($"赛事项目包缺少 {path}。");

    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

    private static T Deserialize<T>(byte[] bytes) =>
        JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new InvalidDataException("赛事项目包中的 JSON 内容为空。");

    private static RaceEventProjectSummary Summarize(RaceEventProjectSnapshot project) => new(
        project.Id, project.Name, project.ShortName, project.Organizer, project.ScheduledStartAt,
        project.TimeZoneId, project.Status, project.Revision, project.CreatedAt, project.UpdatedAt,
        project.Room.TrackName, project.Results.Count,
        project.Results.SelectMany(result => result.Participants).Select(item => item.Id).Distinct().Count(),
        project.AuditEvents.Count, project.TrackPackage is not null, project.OrganizerLogo is not null);

    private void Save()
    {
        var temporary = indexPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(projects, JsonOptions), new UTF8Encoding(false));
        File.Move(temporary, indexPath, true);
    }

    private static List<RaceEventProjectSnapshot> Load(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<RaceEventProjectSnapshot>>(File.ReadAllText(path), JsonOptions) ?? [];
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"赛事项目索引无法读取：{path}", exception);
        }
    }

    private static string? NormalizeRequired(string? value, int maximum) => NormalizeOptional(value, maximum);

    private static string? NormalizeOptional(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = new string(value.Trim().Where(character => !char.IsControl(character)).Take(maximum).ToArray());
        return normalized.Length == 0 ? null : normalized;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record RaceEventManifest(
        string Format,
        int FormatVersion,
        Guid ProjectId,
        DateTimeOffset ExportedAt,
        IReadOnlyList<RaceEventManifestEntry> Entries);

    private sealed record RaceEventManifestEntry(string Path, long SizeBytes, string Sha256);

    private sealed record RaceEventDocument(
        Guid Id,
        string Name,
        string? ShortName,
        string? Organizer,
        string? Description,
        DateTimeOffset? ScheduledStartAt,
        string TimeZoneId,
        RaceEventProjectStatus Status,
        int Revision,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? ActivatedAt,
        DateTimeOffset? CompletedAt,
        RaceEventProjectAssetSnapshot? TrackPackage,
        RaceEventProjectAssetSnapshot? OrganizerLogo);

    private sealed record RaceEventEntrant(
        Guid Id,
        string DisplayName,
        string ThemeColor,
        string? TeamName,
        string? TeamColor);

    private sealed record ImportedProject(RaceEventProjectSnapshot Project, RaceEventProjectAssets Assets);
}

public sealed class RaceEventProjectSyncService(
    RaceCoordinator coordinator,
    RaceEventProjectStore projects,
    ILogger<RaceEventProjectSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { projects.SyncActive(coordinator.Results(), coordinator.Events(500)); }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Active event project synchronization failed; the loop will continue.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
