using System.IO.Compression;
using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiPathSearchTests
{
    [Fact]
    public void OriginalCompleteBackendsMatchObservedCostRelaxationAndFloatPriority()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("clockStable").GetBoolean());
        Assert.Empty(root.GetProperty("failures").EnumerateArray());
        Assert.Equal(64, root.GetProperty("cases").GetArrayLength());
        foreach (JsonElement run in root.GetProperty("cases").EnumerateArray())
        {
            int options = run.GetProperty("options").GetInt32();
            Assert.NotEmpty(run.GetProperty("relaxations").EnumerateArray());
            foreach (JsonElement row in run.GetProperty("relaxations").EnumerateArray())
            {
                int cost = row.GetProperty("newCost").GetInt32();
                byte flags = row.GetProperty("flags").GetByte();
                Assert.Equal(cost, AiPathSearchRules.Cost(row.GetProperty("parentCost").GetInt32(), row.GetProperty("stepCost").GetInt32()));
                Assert.Equal(row.GetProperty("accepted").GetBoolean(), AiPathSearchRules.CanRelax(flags, row.GetProperty("oldCost").GetInt32(), cost));
                if (!row.TryGetProperty("priority", out JsonElement priority)) continue;
                bool hasDistance = row.TryGetProperty("distance", out JsonElement d);
                Assert.Equal(hasDistance, AiPathSearchRules.UsesHeuristic(options));
                int distance = hasDistance ? d.GetInt32() : 0;
                float factor = BitConverter.Int32BitsToSingle(unchecked((int)row.GetProperty("factorBits").GetUInt32()));
                Assert.Equal(priority.GetInt32(), AiPathSearchRules.Priority(cost, distance, factor, options));
                Assert.Equal(cost, row.GetProperty("storedCost").GetInt32());
                Assert.Equal(row.GetProperty("updatedFlags").GetByte(), AiPathSearchRules.LinkedFlags(flags));
            }
        }
    }

    [Fact]
    public void NativeSearchStartsAtTheRequestedDestinationAndReturnsItsPredecessorChain()
    {
        using JsonDocument document = Vectors();
        foreach (JsonElement run in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            JsonElement[] chain = run.GetProperty("chain").EnumerateArray().ToArray();
            Assert.NotEmpty(chain);
            Assert.Equal(run.GetProperty("source").GetInt32(), chain[0].GetProperty("hex").GetInt32());
            Assert.Equal(run.GetProperty("target").GetInt32(), chain[^1].GetProperty("hex").GetInt32());
            Assert.Equal(0, chain[^1].GetProperty("cost").GetInt32());
            Assert.All(chain, row => Assert.NotEqual(0, row.GetProperty("flags").GetByte() & 4));
            Assert.Equal(run.GetProperty("target").GetInt32(), run.GetProperty("relaxations")[0].GetProperty("parentHex").GetInt32());
            Assert.Equal(0, run.GetProperty("relaxations")[0].GetProperty("parentCost").GetInt32());
        }
    }
    private static JsonDocument Vectors()
    {
        using FileStream file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "AiPathSearchVectors.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip);
    }
}
