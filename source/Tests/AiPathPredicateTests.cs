using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiPathPredicateTests
{
    [Fact]
    public void NativePathResultsAndCachedCostMatchWrapperRules()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "AiPathPredicateVectors.json")));
        JsonElement cases = document.RootElement.GetProperty("cases");
        Assert.Equal(7, cases.EnumerateArray().Select(row => I(row, "predicate")).Distinct().Count());
        foreach (JsonElement row in cases.EnumerateArray())
        {
            bool actual = AiPathPredicates.Evaluate(I(row, "predicate"), B(row, "valid"), B(row, "cacheEligible"),
                I(row, "cached"), B(row, "pathSuccess"), I(row, "count"), I(row, "cost"), I(row, "limit"),
                row.GetProperty("mobility").GetSingle(), B(row, "hostile"));
            Assert.Equal(I(row, "native") != 0, actual);
        }
    }

    [Fact]
    public void OrderedCacheAndNativeTruncationRemainDistinct()
    {
        Assert.NotEqual(AiPathPredicates.CacheOffset(8, 9), AiPathPredicates.CacheOffset(9, 8));
        Assert.Equal(120, AiPathPredicates.MobilityLimit(1.99f));
        Assert.Equal(-120, AiPathPredicates.MobilityLimit(-1.99f));
        Assert.Equal(0, AiPathPredicates.MobilityLimit(float.NaN));
    }
    private static int I(JsonElement row, string name) => row.GetProperty(name).GetInt32();
    private static bool B(JsonElement row, string name) => row.GetProperty(name).GetBoolean();
}
