using System.Text;
using System.Text.Json;
using LazyForza.RaceServer.Core;
using LazyForza.RaceServer.Protocol;

namespace LazyForza.RaceServer.Web;

public sealed class FileRaceStatePersistence : IRaceStatePersistence
{
    private readonly object sync = new();
    private readonly string statePath;
    private readonly string auditPath;

    public FileRaceStatePersistence(RaceServerOptions options)
    {
        var root = Path.GetFullPath(options.DataDirectory);
        Directory.CreateDirectory(root);
        statePath = Path.Combine(root, "current-race.json");
        auditPath = Path.Combine(root, "race-audit.jsonl");
    }

    public RaceRecoveryState? LoadRecoveryState()
    {
        lock (sync)
        {
            if (!File.Exists(statePath)) return null;
            using var document = JsonDocument.Parse(File.ReadAllBytes(statePath));
            if (!document.RootElement.TryGetProperty("version", out _))
            {
                // Old snapshots contain no resume identities or deduplication ledger.
                // Read and validate them, but never invent missing authority or overwrite evidence.
                _ = document.RootElement.Deserialize<RaceSessionSnapshot>(RaceProtocolJson.Options)
                    ?? throw new InvalidDataException("旧版赛事快照无效。");
                throw new InvalidDataException(
                    "current-race.json 是旧版公开快照，缺少恢复身份和事件去重记录，不能安全续赛。请备份该文件用于核对成绩，再移走原文件以启动新赛事。");
            }
            var state = document.RootElement.Deserialize<RaceRecoveryState>(RaceProtocolJson.Options)
                ?? throw new InvalidDataException("赛事恢复文件为空。");
            if (state.Version != RaceRecoveryState.CurrentVersion || state.SavedAt == default ||
                state.State.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"不支持或无效的赛事恢复文件版本：{state.Version}。原文件已保留。");
            return state;
        }
    }

    public void SaveRecoveryState(RaceRecoveryState state)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, RaceProtocolJson.Options);
        lock (sync)
        {
            var temporary = statePath + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // Same-directory replacement: never truncate the last committed checkpoint.
            File.Move(temporary, statePath, overwrite: true);
        }
    }

    public void AppendAudit(RaceAuditEntry entry)
    {
        var json = JsonSerializer.Serialize(entry, RaceProtocolJson.Options);
        lock (sync)
            File.AppendAllText(auditPath, json + Environment.NewLine, new UTF8Encoding(false));
    }
}
