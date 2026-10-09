using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiAllScriptTests
{
    [Fact]
    public void EverySlotAndUnusedExpressionMatchesNativeIncludingDivisionTraps()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "AiAllScriptVectors.json")));
        Assert.Equal(896, document.RootElement.GetProperty("count").GetInt32());
        JsonElement slots = document.RootElement.GetProperty("slots");
        Assert.Equal(896, slots.GetArrayLength());
        Assert.Equal(13, slots.EnumerateArray().Count(slot => slot[0].GetInt32() is >= 1 and <= 13));
        JsonElement cases = document.RootElement.GetProperty("cases");
        Assert.Equal(2560, cases.GetArrayLength());
        Assert.Equal(896, cases.EnumerateArray().Select(row => row.GetProperty("script").GetInt32()).Distinct().Count());
        foreach (JsonElement row in cases.EnumerateArray())
        {
            int script = row.GetProperty("script").GetInt32();
            int[] args = row.GetProperty("args").EnumerateArray().Select(value => value.GetInt32()).ToArray();
            if (row.TryGetProperty("trap", out _))
                Assert.Throws<OverflowException>(() => AiScriptScore.Evaluate(script, args));
            else
                Assert.Equal(row.GetProperty("native").GetInt32(), AiScriptScore.Evaluate(script, args));
        }
    }
}
