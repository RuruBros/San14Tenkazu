using System.IO.Compression;
using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiPathSmallTrialTests
{
    [Fact]
    public void PlannedImprovementsEmptyTheOriginalQueueAndTakePreviouslyMissingEdges()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("clockStable").GetBoolean());
        Assert.Equal(root.GetProperty("clockBefore").GetRawText(), root.GetProperty("clockAfter").GetRawText());
        Assert.Empty(root.GetProperty("failures").EnumerateArray());
        Assert.Equal(3, root.GetProperty("cases").GetArrayLength());
        Assert.Equal(root.GetProperty("liveFlagBefore").GetString(), root.GetProperty("liveFlagAfter").GetString());
        JsonElement[] emptyRemovals = root.GetProperty("preparation").GetProperty("queueRemovals").EnumerateArray()
            .Where(r => r.GetProperty("headAfter").GetInt64() == 0).ToArray();
        Assert.Equal(2, emptyRemovals.Length);
        for (int i = 0; i < 2; i++)
        {
            JsonElement run = root.GetProperty("cases")[i], removal = emptyRemovals[i];
            bool bounded = run.GetProperty("rva").GetInt32() == 0x184EB0;
            Assert.Equal(bounded ? 0x18544A : 0x1841FD, removal.GetProperty("returnAddress").GetInt32());
            Assert.Equal(removal.GetProperty("node").GetInt64(), removal.GetProperty("headBefore").GetInt64());
            JsonElement prediction = run.GetProperty("plannedPredictions")[0];
            JsonElement actual = run.GetProperty("relaxations").EnumerateArray().Single(r =>
                r.GetProperty("node").GetInt64() == removal.GetProperty("node").GetInt64() &&
                r.GetProperty("accepted").GetBoolean() && (r.GetProperty("flags").GetByte() & 1) != 0);
            foreach (string key in new[] { "hex", "parentHex", "oldCost", "newCost" })
                Assert.Equal(prediction.GetProperty(key).GetInt32(), actual.GetProperty(key).GetInt32());
            JsonElement edge = root.GetProperty("branchCoverage").GetProperty(bounded ? "0x184eb0" : "0x183a30")
                .EnumerateArray().Single(r => r.GetProperty("rva").GetInt32() == run.GetProperty("plannedTarget").GetInt32());
            Assert.True(edge.GetProperty("fallthrough").GetBoolean());
            Assert.Empty(run.GetProperty("changes").EnumerateArray());
            Assert.Empty(run.GetProperty("chain").EnumerateArray());
        }
        Assert.Equal(16, root.GetProperty("cases")[0].GetProperty("nativeExclusionList").GetProperty("entries").GetArrayLength());
        Assert.False(root.GetProperty("cases")[1].TryGetProperty("excludedHexes", out _));
        Assert.Equal(139, root.GetProperty("cases")[1].GetProperty("limit").GetInt32());
    }

    [Fact]
    public void SmallNativeSearchDecisionsStillMatchCoreCostAndPriorityRules()
    {
        using JsonDocument document = Vectors();
        int decisions = 0;
        foreach (JsonElement run in document.RootElement.GetProperty("cases").EnumerateArray())
            foreach (JsonElement row in run.GetProperty("relaxations").EnumerateArray())
            {
                int cost = row.GetProperty("newCost").GetInt32(), options = run.GetProperty("options").GetInt32();
                Assert.Equal(cost, AiPathSearchRules.Cost(row.GetProperty("parentCost").GetInt32(), row.GetProperty("stepCost").GetInt32()));
                Assert.Equal(row.GetProperty("accepted").GetBoolean(), AiPathSearchRules.CanRelax(row.GetProperty("flags").GetByte(), row.GetProperty("oldCost").GetInt32(), cost));
                if (row.TryGetProperty("priority", out JsonElement priority))
                {
                    float factor = BitConverter.Int32BitsToSingle(unchecked((int)row.GetProperty("factorBits").GetUInt32()));
                    Assert.Equal(priority.GetInt32(), AiPathSearchRules.Priority(cost, row.GetProperty("distance").GetInt32(), factor, options));
                    Assert.Equal(cost, row.GetProperty("storedCost").GetInt32());
                }
                decisions++;
            }
        Assert.Equal(955, decisions);
    }

    [Fact]
    public void GlobalCallLeavesThisFreshSingleNodeListUnchangedWithoutClaimingLoopCoverage()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement, run = root.GetProperty("cases")[2];
        Assert.Equal(run.GetProperty("source").GetInt32(), run.GetProperty("goals")[0].GetInt32());
        Assert.Equal(1, run.GetProperty("chain").GetArrayLength());
        JsonElement call = Assert.Single(root.GetProperty("preparation").GetProperty("globalListCalls").EnumerateArray());
        JsonElement before = call.GetProperty("before"), after = call.GetProperty("after");
        Assert.Equal(before.GetRawText(), after.GetRawText());
        Assert.Equal(1, before.GetProperty("count").GetInt64());
        Assert.Equal(0, before.GetProperty("next").GetInt64());
        Assert.Equal(0, before.GetProperty("previous").GetInt64());
        Assert.Equal(before.GetProperty("node").GetInt64(), before.GetProperty("head").GetInt64());
        Assert.Equal(before.GetProperty("head").GetInt64(), before.GetProperty("tail").GetInt64());
        JsonElement helper = Assert.Single(run.GetProperty("helperCalls").EnumerateArray());
        Assert.Equal(0x185B30, helper.GetProperty("rva").GetInt32());
        Assert.Equal(before.GetProperty("payload").GetInt64(), helper.GetProperty("args")[1].GetInt64());
        JsonElement edge = root.GetProperty("branchCoverage").GetProperty("0x183a30").EnumerateArray()
            .Single(r => r.GetProperty("rva").GetInt32() == 0x183ECB);
        Assert.False(edge.GetProperty("taken").GetBoolean());
        Assert.True(edge.GetProperty("fallthrough").GetBoolean());
    }

    private static JsonDocument Vectors()
    {
        using FileStream file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "AiPathSmallTrialVectors.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip);
    }
}
