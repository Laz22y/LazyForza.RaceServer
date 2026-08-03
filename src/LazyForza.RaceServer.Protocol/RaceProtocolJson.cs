using System.Text.Json;
using System.Text.Json.Serialization;

namespace LazyForza.RaceServer.Protocol;

public static class RaceProtocolJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize<T>(string type, long sequence, T payload)
    {
        var envelope = new
        {
            protocolVersion = RaceProtocol.CurrentVersion,
            type,
            sequence,
            payload
        };
        return JsonSerializer.Serialize(envelope, Options);
    }

    public static RaceEnvelope DeserializeEnvelope(ReadOnlySpan<byte> utf8Json)
    {
        var envelope = JsonSerializer.Deserialize<RaceEnvelope>(utf8Json, Options);
        return envelope ?? throw new JsonException("Race message envelope is empty.");
    }

    public static T DeserializePayload<T>(RaceEnvelope envelope) =>
        envelope.Payload.Deserialize<T>(Options) ??
        throw new JsonException($"Message '{envelope.Type}' has an empty payload.");

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
