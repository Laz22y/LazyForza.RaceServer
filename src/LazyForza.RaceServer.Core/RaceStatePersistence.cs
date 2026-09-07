using System.Text.Json;

namespace LazyForza.RaceServer.Core;

// Internal disk contract, independently versioned from the public wire protocol.
public sealed record RaceRecoveryState(int Version, DateTimeOffset SavedAt, JsonElement State)
{
    public const int CurrentVersion = 1;
}

public interface IRaceStatePersistence
{
    RaceRecoveryState? LoadRecoveryState();
    void SaveRecoveryState(RaceRecoveryState state);
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
    public RaceRecoveryState? LoadRecoveryState() => null;
    public void SaveRecoveryState(RaceRecoveryState state) { }
    public void AppendAudit(RaceAuditEntry entry) { }
}
