using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiOptionTests
{
    [Fact]
    public void StorageSelectionAndComparisonsMatchBothNativePredicates()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "AiOptionVectors.json")));
        JsonElement cases = document.RootElement.GetProperty("cases");
        Assert.Equal(128, cases.GetArrayLength());
        foreach (JsonElement row in cases.EnumerateArray())
            Assert.Equal(row[5].GetInt32() != 0, AiOptionPredicates.Evaluate(
                row[0].GetInt32(), row[1].GetInt32(), row[2].GetInt32(), row[3].GetInt32()));
    }
}
