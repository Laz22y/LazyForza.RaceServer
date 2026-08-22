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
        CancellationToken cancellationToken)
    {
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
        var identity = InspectArchive(bytes);
        var created = new HostedTrackPackageMetadata(
            identity.TrackId, identity.TrackName, identity.TrackRevision, identity.TrackPackageHash,
            Convert.ToHexString(SHA256.HashData(bytes)), bytes.LongLength, DateTimeOffset.UtcNow,
            SafeFileName(fileName, identity.TrackName));

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

    private static HostedTrackIdentity InspectArchive(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var manifestEntry = archive.GetEntry("manifest.json") ??
                                throw new InvalidDataException("文件缺少 manifest.json。");
            var trackEntry = archive.GetEntry("track.json") ??
                             throw new InvalidDataException("文件缺少 track.json。");
            if (archive.Entries.Count != 2) throw new InvalidDataException("赛道包结构不正确。");
            using var manifestStream = manifestEntry.Open();
            using var manifest = JsonDocument.Parse(manifestStream);
            var root = manifest.RootElement;
            if (!string.Equals(RequiredString(root, "format"), "lazyforza-estate-track", StringComparison.Ordinal) ||
                root.GetProperty("formatVersion").GetInt32() != 1)
                throw new InvalidDataException("这不是当前服务端支持的 LazyForza 地产环道文件。");
            var trackId = root.GetProperty("trackId").GetGuid().ToString("D");
            var trackName = RequiredString(root, "trackName");
            var trackRevision = RequiredString(root, "mapRevision");
            var payloadHash = RequiredSha256(root, "payloadSha256");
            var fingerprint = root.TryGetProperty("trackFingerprintSha256", out var fingerprintElement) &&
                              fingerprintElement.ValueKind == JsonValueKind.String &&
                              !string.IsNullOrWhiteSpace(fingerprintElement.GetString())
                ? RequiredSha256(root, "trackFingerprintSha256")
                : payloadHash;

            using var trackBuffer = new MemoryStream();
            using (var trackStream = trackEntry.Open()) trackStream.CopyTo(trackBuffer);
            var trackBytes = trackBuffer.ToArray();
            var computed = Convert.ToHexString(SHA256.HashData(trackBytes));
            if (!string.Equals(computed, payloadHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("赛道包内部 SHA-256 校验失败。");
            using var payload = JsonDocument.Parse(trackBytes);
            var payloadRoot = payload.RootElement;
            var payloadTrack = payloadRoot.GetProperty("track");
            var payloadDefinition = payloadRoot.GetProperty("definition");
            if (payloadTrack.GetProperty("id").GetGuid().ToString("D") != trackId ||
                !string.Equals(RequiredString(payloadTrack, "name"), trackName, StringComparison.Ordinal) ||
                !string.Equals(RequiredString(payloadDefinition, "mapRevision"), trackRevision, StringComparison.Ordinal))
                throw new InvalidDataException("赛道包清单与 track.json 内容不一致。");
            ValidatePitCenterLine(payloadDefinition);
            ValidateIdentity(trackId, trackName, fingerprint);
            return new HostedTrackIdentity(trackId, trackName, trackRevision, fingerprint);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("无法读取赛道包中的清单或赛道数据。", exception);
        }
    }

    private static string RequiredString(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!
            : throw new InvalidDataException($"赛道包缺少 {propertyName}。");

    private static string RequiredSha256(JsonElement value, string propertyName)
    {
        var hash = RequiredString(value, propertyName).ToUpperInvariant();
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException($"赛道包中的 {propertyName} 不是有效的 SHA-256。");
        return hash;
    }

    private static void ValidateIdentity(string trackId, string trackName, string trackPackageHash)
    {
        if (!Guid.TryParse(trackId, out _)) throw new InvalidDataException("赛道标识不是有效 UUID。");
        if (string.IsNullOrWhiteSpace(trackName) || trackName.Trim().Length > 128)
            throw new InvalidDataException("赛道名称不能为空且不能超过 128 个字符。");
        if (trackPackageHash.Length != 64 || !trackPackageHash.All(Uri.IsHexDigit))
            throw new InvalidDataException("赛道数据 SHA-256 必须是 64 位十六进制字符。");
    }

    private static void ValidatePitCenterLine(JsonElement definition)
    {
        if (!definition.TryGetProperty("pit", out var pit) || pit.ValueKind == JsonValueKind.Null)
            return;
        if (pit.ValueKind != JsonValueKind.Object ||
            !pit.TryGetProperty("centerLine", out var centerLine) ||
            centerLine.ValueKind != JsonValueKind.Array || centerLine.GetArrayLength() < 2)
            throw new InvalidDataException("赛道包中的维修区通道无效。");

        (double X, double Y, double Z)? previous = null;
        foreach (var value in centerLine.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty("x", out var xValue) || !xValue.TryGetDouble(out var x) ||
                !value.TryGetProperty("y", out var yValue) || !yValue.TryGetDouble(out var y) ||
                !value.TryGetProperty("z", out var zValue) || !zValue.TryGetDouble(out var z) ||
                !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
                throw new InvalidDataException("赛道包中的维修区通道坐标无效。");
            if (previous is { } prior)
            {
                var dx = x - prior.X;
                var dy = y - prior.Y;
                var dz = z - prior.Z;
                if (Math.Sqrt(dx * dx + dy * dy + dz * dz) > 25)
                    throw new InvalidDataException(
                        "赛道包中的维修区通道存在大段遥测缺口，请在客户端重新录入维修区通道。");
            }
            previous = (x, y, z);
        }
    }

    private static string SafeFileName(string fileName, string trackName)
    {
        var source = string.IsNullOrWhiteSpace(fileName) ? trackName + ".lfzestate" : Path.GetFileName(fileName);
        if (!source.EndsWith(".lfzestate", StringComparison.OrdinalIgnoreCase)) source += ".lfzestate";
        return new string(source.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '-' : character).ToArray());
    }

    private sealed record HostedTrackIdentity(
        string TrackId,
        string TrackName,
        string TrackRevision,
        string TrackPackageHash);
}
