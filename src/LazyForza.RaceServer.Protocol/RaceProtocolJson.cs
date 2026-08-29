using System.Text.Json;
using System.Text.Json.Serialization;

namespace LazyForza.RaceServer.Protocol;

public sealed record RaceEnvelope(
    int ProtocolVersion,
    string Type,
    long Sequence,
    JsonElement Payload);

public static class RaceProtocolJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize<T>(string type, long sequence, T payload)
    {
        return JsonSerializer.Serialize(CreateEnvelope(type, sequence, payload), Options);
    }

    public static byte[] SerializeToUtf8Bytes<T>(string type, long sequence, T payload) =>
        JsonSerializer.SerializeToUtf8Bytes(CreateEnvelope(type, sequence, payload), Options);

    public static RaceEnvelope DeserializeEnvelope(ReadOnlySpan<byte> utf8Json)
    {
        var envelope = JsonSerializer.Deserialize<RaceEnvelope>(utf8Json, Options);
        return envelope ?? throw new JsonException("Race message envelope is empty.");
    }

    public static T DeserializePayload<T>(RaceEnvelope envelope) =>
        envelope.Payload.Deserialize<T>(Options) ??
        throw new JsonException($"Message '{envelope.Type}' has an empty payload.");

    private static WireEnvelope<T> CreateEnvelope<T>(string type, long sequence, T payload) =>
        new(RaceProtocol.CurrentVersion, type, sequence, payload);

    private sealed record WireEnvelope<T>(
        int ProtocolVersion,
        string Type,
        long Sequence,
        T Payload);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
