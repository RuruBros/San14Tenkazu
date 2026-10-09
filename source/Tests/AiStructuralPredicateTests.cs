using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiStructuralPredicateTests
{
    [Fact]
    public void RelationalCompositionMatchesNativeHelpers()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "AiStructuralPredicateVectors.json")));
        JsonElement cases = document.RootElement.GetProperty("cases");
        Assert.Equal(18, cases.EnumerateArray().Select(row => I(row, "predicate")).Distinct().Count());
        foreach (JsonElement row in cases.EnumerateArray())
        {
            bool actual = AiStructuralPredicates.Evaluate(I(row, "predicate"), B(row, "inputs"),
                B(row, "a"), B(row, "b"), B(row, "c"), I(row, "own"), I(row, "hostile"), I(row, "required"),
                (short)I(row, "oldLevel"), (short)I(row, "nextLevel"), I(row, "increment"));
            Assert.Equal(I(row, "native") != 0, actual);
        }
    }
    private static int I(JsonElement row, string name) => row.GetProperty(name).GetInt32();
    private static bool B(JsonElement row, string name) => row.GetProperty(name).GetBoolean();
}
