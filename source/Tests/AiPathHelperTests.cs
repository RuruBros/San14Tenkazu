using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiPathHelperTests
{
    [Fact]
    public void NativeTerrainAdmissionAndCachedFloatCostMatchEveryRecordedCase()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("clockStable").GetBoolean());
        Assert.Empty(root.GetProperty("failures").EnumerateArray());
        Assert.Equal(896, root.GetProperty("cases").GetArrayLength());
        Assert.Equal(Enumerable.Range(1, 24), root.GetProperty("terrainKinds").EnumerateArray().Select(v => v.GetInt32()));
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        foreach (JsonElement row in root.GetProperty("cases").EnumerateArray())
        {
            int actual = row.GetProperty("native").GetInt32();
            if (row.GetProperty("kind").GetString() == "admission")
            {
                AiPathAdmission facts = row.GetProperty("facts").Deserialize<AiPathAdmission>(options)!;
                Assert.Equal(actual != 0, facts.Allowed());
            }
            else Assert.Equal(actual, AiPathStepCost.Evaluate(row.GetProperty("cost").GetInt32(), row.GetProperty("options").GetInt32(),
                row.GetProperty("hasTile").GetBoolean(), row.GetProperty("hasArmy").GetBoolean(), row.GetProperty("factor").GetSingle()));
        }
    }

    [Fact]
    public void RepeatedTerrainCostsReuseTheNativeMapValueWithoutRecalculatingMobility()
    {
        using JsonDocument document = Vectors();
        var rows = document.RootElement.GetProperty("cases").EnumerateArray()
            .Where(r => r.GetProperty("kind").GetString() == "cost" && r.TryGetProperty("definition", out _))
            .GroupBy(r => (Definition: r.GetProperty("definition").GetInt32(), Tile: r.GetProperty("tile").GetUInt64()));
        foreach (var group in rows)
        {
            JsonElement[] adjusted = group.Where(r => (r.GetProperty("options").GetInt32() & 1) != 0).ToArray();
            Assert.True(adjusted.Length > 1);
            Assert.All(adjusted, r => Assert.Equal(adjusted[0].GetProperty("factor").GetSingle(), r.GetProperty("factor").GetSingle()));
            Assert.InRange(group.Sum(r => r.GetProperty("events").GetArrayLength()), 0, 1);
        }
    }
    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "AiPathHelperVectors.json")));
}
