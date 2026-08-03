using LazyForza.RaceServer.Protocol;

namespace LazyForza.RaceServer.Core;

public interface IRaceStatePersistence
{
    void SaveImportantSnapshot(RaceSessionSnapshot snapshot);
    void AppendAudit(RaceAuditEntry entry);
}

public sealed record RaceAuditEntry(
    DateTimeOffset At,
    string Type,
    string Message,
    Guid? ParticipantId = null,
    object? Detail = null);

public sealed class NullRaceStatePersistence : IRaceStatePersistence
{
    public static NullRaceStatePersistence Instance { get; } = new();

    public void SaveImportantSnapshot(RaceSessionSnapshot snapshot) { }
    public void AppendAudit(RaceAuditEntry entry) { }
}
