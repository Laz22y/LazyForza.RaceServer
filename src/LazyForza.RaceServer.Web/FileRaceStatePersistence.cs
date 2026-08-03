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

    public void SaveImportantSnapshot(RaceSessionSnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, RaceProtocolJson.Options);
        lock (sync)
        {
            var temporary = statePath + ".tmp";
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, statePath, true);
        }
    }

    public void AppendAudit(RaceAuditEntry entry)
    {
        var json = JsonSerializer.Serialize(entry, RaceProtocolJson.Options);
        lock (sync)
            File.AppendAllText(auditPath, json + Environment.NewLine, new UTF8Encoding(false));
    }
}
