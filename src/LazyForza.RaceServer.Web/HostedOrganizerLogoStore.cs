using System.Security.Cryptography;
using System.Text.Json;

namespace LazyForza.RaceServer.Web;

public sealed record HostedOrganizerLogoMetadata(
    string Sha256,
    string MimeType,
    long SizeBytes,
    DateTimeOffset UploadedAt,
    string FileName);

public sealed class HostedOrganizerLogoStore
{
    public const long MaximumLogoBytes = 262_144;
    private readonly SemaphoreSlim sync = new(1, 1);
    private readonly string logoPath;
    private readonly string metadataPath;
    private HostedOrganizerLogoMetadata? metadata;

    public HostedOrganizerLogoStore(LazyForza.RaceServer.Core.RaceServerOptions options)
    {
        var root = Path.GetFullPath(options.DataDirectory);
        Directory.CreateDirectory(root);
        logoPath = Path.Combine(root, "organizer-logo.bin");
        metadataPath = Path.Combine(root, "organizer-logo.json");
        metadata = LoadMetadata();
    }

    public HostedOrganizerLogoMetadata? Current =>
        metadata is not null && File.Exists(logoPath) ? metadata : null;

    public async Task<HostedOrganizerLogoMetadata> SaveAsync(
        Stream source,
        string fileName,
        string? suppliedMimeType,
        CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        var chunk = new byte[32 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > MaximumLogoBytes)
                throw new InvalidDataException("赛事 Logo 超过 256 KiB 上限。");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        if (buffer.Length == 0) throw new InvalidDataException("赛事 Logo 文件为空。");
        var bytes = buffer.ToArray();
        var mimeType = DetectMimeType(bytes, suppliedMimeType);
        var created = new HostedOrganizerLogoMetadata(
            Convert.ToHexString(SHA256.HashData(bytes)),
            mimeType,
            bytes.LongLength,
            DateTimeOffset.UtcNow,
            SafeFileName(fileName, mimeType));

        await sync.WaitAsync(cancellationToken);
        try
        {
            var logoTemporary = logoPath + ".tmp";
            var metadataTemporary = metadataPath + ".tmp";
            await File.WriteAllBytesAsync(logoTemporary, bytes, cancellationToken);
            await File.WriteAllTextAsync(metadataTemporary, JsonSerializer.Serialize(created), cancellationToken);
            File.Move(logoTemporary, logoPath, true);
            File.Move(metadataTemporary, metadataPath, true);
            metadata = created;
        }
        finally { sync.Release(); }
        return created;
    }

    public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
    {
        await sync.WaitAsync(cancellationToken);
        try { return Current is null ? null : await File.ReadAllBytesAsync(logoPath, cancellationToken); }
        finally { sync.Release(); }
    }

    public async Task DeleteAsync(CancellationToken cancellationToken)
    {
        await sync.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(logoPath)) File.Delete(logoPath);
            if (File.Exists(metadataPath)) File.Delete(metadataPath);
            metadata = null;
        }
        finally { sync.Release(); }
    }

    private HostedOrganizerLogoMetadata? LoadMetadata()
    {
        if (!File.Exists(metadataPath) || !File.Exists(logoPath)) return null;
        try
        {
            var loaded = JsonSerializer.Deserialize<HostedOrganizerLogoMetadata>(File.ReadAllText(metadataPath));
            return loaded is not null && new FileInfo(logoPath).Length == loaded.SizeBytes ? loaded : null;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    private static string DetectMimeType(byte[] bytes, string? suppliedMimeType)
    {
        var png = bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var jpeg = bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
        if (png) return "image/png";
        if (jpeg) return "image/jpeg";
        throw new InvalidDataException(
            string.Equals(suppliedMimeType, "image/png", StringComparison.OrdinalIgnoreCase)
                ? "PNG 文件签名不正确。"
                : "赛事 Logo 只支持 PNG 或 JPEG 图片。");
    }

    private static string SafeFileName(string fileName, string mimeType)
    {
        var extension = mimeType == "image/png" ? ".png" : ".jpg";
        var source = string.IsNullOrWhiteSpace(fileName) ? "organizer-logo" + extension : Path.GetFileName(fileName);
        if (!source.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) source = Path.GetFileNameWithoutExtension(source) + extension;
        return new string(source.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '-' : character).ToArray());
    }
}
