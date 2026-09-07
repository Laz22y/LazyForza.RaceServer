using System.Text.Json;
using LazyForza.RaceServer.Core;
using LazyForza.RaceServer.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LazyForza.RaceServer.Tests;

[TestClass]
public sealed class LapCompletionValidatorTests
{
    public static IEnumerable<object[]> Cases() => JsonSerializer.Deserialize<JsonElement[]>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "lap-validation-cases.json")))!
        .Select(item => new object[] { item.GetProperty("name").GetString()!, item.GetRawText() });

    [TestMethod]
    [DynamicData(nameof(Cases))]
    public void SharedContract(string name, string json)
    {
        var item = JsonSerializer.Deserialize<JsonElement>(json);
        var stage = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var lap = new RaceLapCompleted(Guid.NewGuid(), Int("number", 1), Number("seconds", 60),
            item.TryGetProperty("sectors", out var sectors) ? sectors.Deserialize<double[]>()! : [20, 20, 20],
            Bool("valid", true), null, 60_000, StageId: Bool("legacy") ? null : Bool("wrongStage") ? Guid.NewGuid() : stage);
        var kind = item.TryGetProperty("samples", out var mode) ? mode.GetString() : "none";
        var samples = kind == "none" ? [] : Enumerable.Range(0, 61)
            .Where(i => kind != "gap" || i < 20 || i > 30)
            .Select(i => new LapValidationSample(i * 1000, kind == "stationary" ? .2 : i == 60 ? 0 : i / 60d,
                kind != "paused" || i != 30, kind == "pit" && i is >= 20 and <= 40)).ToArray();
        var result = LapCompletionValidator.Validate(lap, stage, 3,
            item.TryGetProperty("lastNumber", out var last) ? last.GetInt32() : null, Bool("lastValid"), samples);
        Assert.AreEqual(Bool("canAccept"), result.CanAccept, name);
        Assert.AreEqual(Enum.Parse<RaceLapValidationStatus>(item.GetProperty("status").GetString()!, true), result.Status, name);

        bool Bool(string key, bool fallback = false) => item.TryGetProperty(key, out var value) ? value.GetBoolean() : fallback;
        int Int(string key, int fallback) => item.TryGetProperty(key, out var value) ? value.GetInt32() : fallback;
        double Number(string key, double fallback) => item.TryGetProperty(key, out var value) ? value.GetDouble() : fallback;
    }

    [TestMethod]
    public void NonFiniteAndUnsafeNumbersAreRejected()
    {
        var lap = new RaceLapCompleted(Guid.NewGuid(), 1, 60, [20,20,20], true, null, 60000);
        foreach (var bad in new[] { lap with { LapSeconds = double.NaN }, lap with { LapSeconds = double.PositiveInfinity },
            lap with { SectorSeconds = [20, double.NaN, 20] }, lap with { ClientMonotonicMilliseconds = long.MaxValue },
            lap with { EventId = Guid.Empty } })
            Assert.IsFalse(LapCompletionValidator.Validate(bad, null, 3, null, false, []).CanAccept);
    }
}
