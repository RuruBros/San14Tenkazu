using System.IO.Compression;
using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiPathReinsertTests
{
    [Fact]
    public void CompleteSearchesUseActualAdmissibleRoutesAndReturnNativeLimits()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("clockStable").GetBoolean());
        Assert.Empty(root.GetProperty("failures").EnumerateArray());
        Assert.Equal(240, root.GetProperty("cases").GetArrayLength());
        Assert.Equal(20, root.GetProperty("routeSelection").GetProperty("routes").GetArrayLength());
        Assert.Equal(new[] { 3, 28, 51 }, root.GetProperty("routeSelection").GetProperty("profiles").EnumerateArray().Select(p => p.GetProperty("owner").GetInt32()).Order());
        int empty = 0, maximum = 0;
        foreach (JsonElement run in root.GetProperty("cases").EnumerateArray())
        {
            Assert.Empty(run.GetProperty("changes").EnumerateArray());
            JsonElement route = run.GetProperty("actualRoute"), path = route.GetProperty("path"), chain = run.GetProperty("chain");
            Assert.Equal(route.GetProperty("steps").GetInt32() + 1, path.GetArrayLength());
            Assert.Equal(run.GetProperty("source").GetInt32(), path[0].GetInt32());
            Assert.Equal(run.GetProperty("target").GetInt32(), path[path.GetArrayLength() - 1].GetInt32());
            bool bounded = run.GetProperty("rva").GetInt32() == 0x184EB0;
            if (chain.GetArrayLength() == 0) { Assert.True(bounded); empty++; continue; }
            Assert.Equal(run.GetProperty("source").GetInt32(), chain[0].GetProperty("hex").GetInt32());
            Assert.Equal(run.GetProperty("target").GetInt32(), chain[chain.GetArrayLength() - 1].GetProperty("hex").GetInt32());
            Assert.Equal(0, chain[chain.GetArrayLength() - 1].GetProperty("cost").GetInt32());
            if (bounded) Assert.All(chain.EnumerateArray(), n => Assert.True(n.GetProperty("cost").GetInt32() <= run.GetProperty("limit").GetInt32()));
            maximum = Math.Max(maximum, chain.GetArrayLength());
        }
        Assert.Equal(120, empty);
        Assert.Equal(33, maximum);
    }

    [Fact]
    public void EachQueuedImprovementCallsOriginalRemovalBeforeNativePriorityInsertion()
    {
        using JsonDocument document = Vectors();
        var totals = new Dictionary<int, int>();
        foreach (JsonElement run in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            int rva = run.GetProperty("rva").GetInt32(), returnedTo = rva == 0x183A30 ? 0x1841FD : 0x18544A;
            JsonElement[] improvements = run.GetProperty("relaxations").EnumerateArray().Where(r => r.GetProperty("accepted").GetBoolean() && (r.GetProperty("flags").GetByte() & 1) != 0).ToArray();
            JsonElement[] removals = run.GetProperty("helperCalls").EnumerateArray().Where(c => c.GetProperty("rva").GetInt32() == 0x18AE60 && c.GetProperty("return_address").GetInt32() == returnedTo).ToArray();
            Assert.Equal(improvements.Length, removals.Length);
            for (int i = 0; i < improvements.Length; i++)
            {
                JsonElement row = improvements[i];
                Assert.Equal(row.GetProperty("node").GetInt64(), removals[i].GetProperty("args")[1].GetInt64());
                Assert.True(row.GetProperty("oldCost").GetInt32() > row.GetProperty("newCost").GetInt32());
                Assert.Equal(row.GetProperty("newCost").GetInt32(), row.GetProperty("storedCost").GetInt32());
                Assert.Equal(AiPathSearchRules.LinkedFlags(row.GetProperty("flags").GetByte()), row.GetProperty("updatedFlags").GetByte());
                Assert.True(AiPathSearchRules.UsesHeuristic(run.GetProperty("options").GetInt32()));
            }
            totals[rva] = totals.GetValueOrDefault(rva) + improvements.Length;
        }
        Assert.Equal(1645, totals[0x183A30]);
        Assert.Equal(1680, totals[0x184EB0]);
    }

    [Fact]
    public void NonHeuristicNativeSearchesKeepFixedDestinationCostsAndMonotoneParents()
    {
        using JsonDocument document = Vectors();
        int searches = 0, decisions = 0;
        foreach (JsonElement run in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            int options = run.GetProperty("options").GetInt32();
            if (AiPathSearchRules.UsesHeuristic(options)) continue;
            searches++;
            int previousParentCost = -1;
            foreach (JsonElement row in run.GetProperty("relaxations").EnumerateArray())
            {
                int parent = row.GetProperty("parentCost").GetInt32();
                Assert.InRange(parent, previousParentCost, 20_000 * 48_399);
                previousParentCost = parent;
                Assert.Equal(10_000, row.GetProperty("stepCost").GetInt32());
                Assert.Equal(row.GetProperty("newCost").GetInt32(), AiPathSearchRules.Cost(parent, 10_000));
                if (row.GetProperty("accepted").GetBoolean()) Assert.Equal(0, row.GetProperty("flags").GetByte() & 3);
                if (row.TryGetProperty("priority", out JsonElement priority)) Assert.Equal(row.GetProperty("newCost").GetInt32(), priority.GetInt32());
                decisions++;
            }
        }
        Assert.Equal(120, searches);
        Assert.True(decisions > 100_000);
    }

    [Fact]
    public void EveryObservedCostDecisionAndPriorityMatchesCore()
    {
        using JsonDocument document = Vectors();
        int decisions = 0, accepted = 0;
        foreach (JsonElement run in document.RootElement.GetProperty("cases").EnumerateArray())
            foreach (JsonElement row in run.GetProperty("relaxations").EnumerateArray())
            {
                int cost = row.GetProperty("newCost").GetInt32();
                Assert.Equal(cost, AiPathSearchRules.Cost(row.GetProperty("parentCost").GetInt32(), row.GetProperty("stepCost").GetInt32()));
                bool actualAccepted = row.GetProperty("accepted").GetBoolean();
                Assert.Equal(actualAccepted, AiPathSearchRules.CanRelax(row.GetProperty("flags").GetByte(), row.GetProperty("oldCost").GetInt32(), cost));
                decisions++; if (actualAccepted) accepted++;
                if (!row.TryGetProperty("priority", out JsonElement priority)) continue;
                bool distanceRead = row.TryGetProperty("distance", out JsonElement distance);
                int options = run.GetProperty("options").GetInt32();
                Assert.Equal(distanceRead, AiPathSearchRules.UsesHeuristic(options));
                float factor = BitConverter.Int32BitsToSingle(unchecked((int)row.GetProperty("factorBits").GetUInt32()));
                Assert.Equal(priority.GetInt32(), AiPathSearchRules.Priority(cost, distanceRead ? distance.GetInt32() : 0, factor, options));
            }
        Assert.Equal(249701, decisions);
        Assert.Equal(55366, accepted);
    }

    private static JsonDocument Vectors()
    {
        using FileStream file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "AiPathReinsertVectors.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip);
    }
}
