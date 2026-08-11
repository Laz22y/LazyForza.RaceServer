using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace LazyForza.RaceServer.Web;

public sealed record HostedTrackPackageMetadata(
    string TrackId,
    string TrackName,
    string? TrackRevision,
    string TrackPackageHash,
    string FileSha256,
    long SizeBytes,
    DateTimeOffset UploadedAt,
    string FileName);

public sealed class HostedTrackPackageStore
{
    public const long MaximumPackageBytes = 1_572_864;
    private readonly SemaphoreSlim sync = new(1, 1);
    private readonly string packagePath;
    private readonly string metadataPath;
    private HostedTrackPackageMetadata? metadata;

    public HostedTrackPackageStore(LazyForza.RaceServer.Core.RaceServerOptions options)
    {
        var root = Path.GetFullPath(options.DataDirectory);
        Directory.CreateDirectory(root);
        packagePath = Path.Combine(root, "hosted-track.lfzestate");
        metadataPath = Path.Combine(root, "hosted-track.json");
        metadata = LoadMetadata();
    }

    public HostedTrackPackageMetadata? Current =>
        metadata is not null && File.Exists(packagePath) ? metadata : null;

    public HostedTrackPackageMetadata? Matching(
        string? trackId,
        string? trackRevision,
        string? trackPackageHash)
    {
        var current = Current;
        return current is not null &&
               string.Equals(current.TrackId, trackId, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(current.TrackPackageHash, trackPackageHash, StringComparison.OrdinalIgnoreCase) &&
               (string.IsNullOrWhiteSpace(trackRevision) ||
                string.IsNullOrWhiteSpace(current.TrackRevision) ||
                string.Equals(current.TrackRevision, trackRevision, StringComparison.Ordinal))
            ? current
            : null;
    }

    public async Task<HostedTrackPackageMetadata> SaveAsync(
        Stream source,
        string fileName,
        string trackId,
        string trackName,
        string? trackRevision,
        string trackPackageHash,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(trackId, trackName, trackPackageHash);
        await using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > MaximumPackageBytes)
                throw new InvalidDataException("赛道文件超过 1.5 MiB 托管上限。");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        if (buffer.Length == 0) throw new InvalidDataException("赛道文件为空。");
        var bytes = buffer.ToArray();
        ValidateArchive(bytes, trackId, trackName, trackRevision, trackPackageHash);
        var created = new HostedTrackPackageMetadata(
            trackId.Trim(), trackName.Trim(), NullIfWhiteSpace(trackRevision), trackPackageHash.Trim().ToUpperInvariant(),
            Convert.ToHexString(SHA256.HashData(bytes)), bytes.LongLength, DateTimeOffset.UtcNow,
            SafeFileName(fileName, trackName));

        await sync.WaitAsync(cancellationToken);
        try
        {
            var packageTemporary = packagePath + ".tmp";
            var metadataTemporary = metadataPath + ".tmp";
            await File.WriteAllBytesAsync(packageTemporary, bytes, cancellationToken);
            await File.WriteAllTextAsync(metadataTemporary, JsonSerializer.Serialize(created), cancellationToken);
            File.Move(packageTemporary, packagePath, true);
            File.Move(metadataTemporary, metadataPath, true);
            metadata = created;
        }
        finally { sync.Release(); }
        return created;
    }

    public async Task<byte[]?> ReadAsync(
        string? trackId,
        string? trackRevision,
        string? trackPackageHash,
        CancellationToken cancellationToken)
    {
        if (Matching(trackId, trackRevision, trackPackageHash) is null) return null;
        await sync.WaitAsync(cancellationToken);
        try { return File.Exists(packagePath) ? await File.ReadAllBytesAsync(packagePath, cancellationToken) : null; }
        finally { sync.Release(); }
    }

    public async Task DeleteAsync(CancellationToken cancellationToken)
    {
        await sync.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(packagePath)) File.Delete(packagePath);
            if (File.Exists(metadataPath)) File.Delete(metadataPath);
            metadata = null;
        }
        finally { sync.Release(); }
    }

    private HostedTrackPackageMetadata? LoadMetadata()
    {
        if (!File.Exists(metadataPath) || !File.Exists(packagePath)) return null;
        try
        {
            var loaded = JsonSerializer.Deserialize<HostedTrackPackageMetadata>(File.ReadAllText(metadataPath));
            return loaded is not null && new FileInfo(packagePath).Length == loaded.SizeBytes ? loaded : null;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    private static void ValidateArchive(
        byte[] bytes,
        string trackId,
        string trackName,
        string? trackRevision,
        string trackPackageHash)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var manifestEntry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("文件缺少 manifest.json。");
        var trackEntry = archive.GetEntry("track.json") ?? throw new InvalidDataException("文件缺少 track.json。");
        if (archive.Entries.Count != 2) throw new InvalidDataException("赛道包结构不正确。");
        using var manifestStream = manifestEntry.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        var root = manifest.RootElement;
        var manifestTrackId = root.GetProperty("trackId").GetGuid().ToString("D");
        var manifestTrackName = root.GetProperty("trackName").GetString();
        var manifestRevision = root.GetProperty("mapRevision").GetString();
        var manifestHash = root.GetProperty("payloadSha256").GetString();
        if (!string.Equals(trackId, manifestTrackId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(trackName.Trim(), manifestTrackName, StringComparison.Ordinal) ||
            (!string.IsNullOrWhiteSpace(trackRevision) && !string.Equals(trackRevision.Trim(), manifestRevision, StringComparison.Ordinal)) ||
            !string.Equals(trackPackageHash, manifestHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("赛道包清单与网页填写的赛道信息不一致。");
        using var trackStream = trackEntry.Open();
        var computed = Convert.ToHexString(SHA256.HashData(trackStream));
        if (!string.Equals(computed, manifestHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("赛道包内部 SHA-256 校验失败。");
    }

    private static void ValidateIdentity(string trackId, string trackName, string trackPackageHash)
    {
        if (!Guid.TryParse(trackId, out _)) throw new InvalidDataException("赛道标识不是有效 UUID。");
        if (string.IsNullOrWhiteSpace(trackName) || trackName.Trim().Length > 128)
            throw new InvalidDataException("赛道名称不能为空且不能超过 128 个字符。");
        if (trackPackageHash.Length != 64 || !trackPackageHash.All(Uri.IsHexDigit))
            throw new InvalidDataException("赛道数据 SHA-256 必须是 64 位十六进制字符。");
    }

    private static string SafeFileName(string fileName, string trackName)
    {
        var source = string.IsNullOrWhiteSpace(fileName) ? trackName + ".lfzestate" : Path.GetFileName(fileName);
        if (!source.EndsWith(".lfzestate", StringComparison.OrdinalIgnoreCase)) source += ".lfzestate";
        return new string(source.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '-' : character).ToArray());
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
