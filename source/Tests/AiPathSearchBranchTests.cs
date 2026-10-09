using System.IO.Compression;
using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiPathSearchBranchTests
{
    [Fact]
    public void ExtendedOriginalSearchesMatchSignedRelaxationAndOptionSelectedPriority()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("clockStable").GetBoolean());
        Assert.Empty(root.GetProperty("failures").EnumerateArray());
        Assert.True(root.GetProperty("cases").GetArrayLength() >= 583);
        foreach (JsonElement run in root.GetProperty("cases").EnumerateArray())
            foreach (JsonElement row in run.GetProperty("relaxations").EnumerateArray())
            {
                int cost = row.GetProperty("newCost").GetInt32();
                byte flags = row.GetProperty("flags").GetByte();
                Assert.Equal(cost, AiPathSearchRules.Cost(row.GetProperty("parentCost").GetInt32(), row.GetProperty("stepCost").GetInt32()));
                Assert.Equal(row.GetProperty("accepted").GetBoolean(), AiPathSearchRules.CanRelax(flags, row.GetProperty("oldCost").GetInt32(), cost));
                if (!row.TryGetProperty("priority", out JsonElement priority)) continue;
                int options = run.GetProperty("options").GetInt32();
                bool distanceRead = row.TryGetProperty("distance", out JsonElement distance);
                Assert.Equal(distanceRead, AiPathSearchRules.UsesHeuristic(options));
                float factor = BitConverter.Int32BitsToSingle(unchecked((int)row.GetProperty("factorBits").GetUInt32()));
                Assert.Equal(priority.GetInt32(), AiPathSearchRules.Priority(cost, distanceRead ? distance.GetInt32() : 0, factor, options));
                Assert.Equal(cost, row.GetProperty("storedCost").GetInt32());
                Assert.Equal(row.GetProperty("updatedFlags").GetByte(), AiPathSearchRules.LinkedFlags(flags));
            }
    }

    [Fact]
    public void EmptyGoalsIdentityGoalsAndExactNativeCostLimitsStayDistinct()
    {
        using JsonDocument document = Vectors();
        JsonElement[] runs = document.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        foreach (JsonElement run in runs)
        {
            int source = run.GetProperty("source").GetInt32();
            int[] goals = run.GetProperty("goals").EnumerateArray().Select(g => g.GetInt32()).ToArray();
            JsonElement[] chain = run.GetProperty("chain").EnumerateArray().ToArray();
            if (goals.Length == 0) Assert.Empty(chain);
            else if (goals.Contains(source)) Assert.Single(chain);
            if (chain.Length == 0) continue;
            Assert.Equal(source, chain[0].GetProperty("hex").GetInt32());
            Assert.Contains(chain[^1].GetProperty("hex").GetInt32(), goals);
            Assert.Equal(0, chain[^1].GetProperty("cost").GetInt32());
        }
        foreach ((string context, int options, int below, int exact) in new[]
            { ("neutral", 0, 9999, 10000), ("neutral", 1, 299, 300), ("army", 1, 14, 15) })
        {
            JsonElement Selected(int limit) => runs.Single(r => r.GetProperty("rva").GetInt32() == 0x184EB0
                && r.GetProperty("context").GetString() == context && r.GetProperty("options").GetInt32() == options
                && r.GetProperty("limit").GetInt32() == limit && !r.TryGetProperty("terrain", out _));
            Assert.Empty(Selected(below).GetProperty("chain").EnumerateArray());
            Assert.NotEmpty(Selected(exact).GetProperty("chain").EnumerateArray());
        }
    }

    [Fact]
    public void ExtendedInputsCoverMoreConditionsOfBothWholeBackends()
    {
        using JsonDocument document = Vectors();
        foreach (JsonProperty backend in document.RootElement.GetProperty("branchCoverage").EnumerateObject())
            Assert.True(backend.Value.EnumerateArray().Count(b => b.GetProperty("taken").GetBoolean() && b.GetProperty("fallthrough").GetBoolean()) >= 38);
    }

    private static JsonDocument Vectors()
    {
        using FileStream file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "AiPathSearchBranchVectors.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip);
    }
}
