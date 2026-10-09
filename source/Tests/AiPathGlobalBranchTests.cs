using System.IO.Compression;
using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiPathGlobalBranchTests
{
    [Fact]
    public void GatedSearchesUseTheCompleteStableGlobalObjectAndLeaveTheLiveFlagUnchanged()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("clockStable").GetBoolean());
        Assert.Empty(root.GetProperty("failures").EnumerateArray());
        Assert.Equal("00000000", root.GetProperty("liveFlagBefore").GetString());
        Assert.Equal(root.GetProperty("liveFlagBefore").GetString(), root.GetProperty("liveFlagAfter").GetString());
        JsonElement snapshot = root.GetProperty("globalContextSnapshot");
        Assert.True(snapshot.GetProperty("twoReadsEqual").GetBoolean());
        Assert.Equal(0x31330, snapshot.GetProperty("allocationSize").GetInt32());
        long pointer = snapshot.GetProperty("pointer").GetInt64();
        long pageStart = snapshot.GetProperty("pageStart").GetInt64();
        Assert.Equal(0, pageStart & 0xFFF);
        Assert.InRange(pointer, pageStart, pageStart + 0xFFF);
        Assert.True(pointer + 0x31330 <= pageStart + snapshot.GetProperty("pageLength").GetInt32());
        Assert.Equal(64, snapshot.GetProperty("sha256").GetString()!.Length);
        JsonElement[] runs = root.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(24, runs.Length);
        int calls = 0;
        foreach (JsonElement run in runs)
        {
            JsonElement change = Assert.Single(run.GetProperty("changes").EnumerateArray());
            Assert.Equal(0x2141A84, change.GetProperty("rva").GetInt32());
            Assert.Equal("00000000", change.GetProperty("before").GetString());
            bool enabled = run.GetProperty("imageFields")[0][2].GetInt32() == 1;
            JsonElement[] helpers = run.GetProperty("helperCalls").EnumerateArray().ToArray();
            if (enabled) Assert.NotEmpty(helpers);
            else Assert.Empty(helpers);
            foreach (JsonElement helper in helpers)
            {
                Assert.Equal(0x185B30, helper.GetProperty("rva").GetInt32());
                Assert.Equal(pointer, helper.GetProperty("args")[0].GetInt64());
                Assert.Equal(44, helper.GetProperty("args")[2].GetInt32());
                Assert.Equal(0, helper.GetProperty("args")[3].GetInt32());
                Assert.Equal(1, helper.GetProperty("result").GetInt32());
                calls++;
            }
        }
        Assert.Equal(32, calls);
        JsonElement branch = root.GetProperty("branchCoverage").GetProperty("0x183a30").EnumerateArray()
            .Single(b => b.GetProperty("rva").GetInt32() == 0x183D6F);
        Assert.True(branch.GetProperty("taken").GetBoolean());
        Assert.True(branch.GetProperty("fallthrough").GetBoolean());
    }

    [Fact]
    public void EnablingTheGlobalFlagPreservesTheseTwelveNativeRouteResults()
    {
        using JsonDocument document = Vectors();
        JsonElement[] runs = document.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        foreach (JsonElement disabled in runs.Where(r => r.GetProperty("imageFields")[0][2].GetInt32() == 0))
        {
            JsonElement enabled = runs.Single(r => r.GetProperty("imageFields")[0][2].GetInt32() == 1
                && r.GetProperty("context").GetString() == disabled.GetProperty("context").GetString()
                && r.GetProperty("options").GetInt32() == disabled.GetProperty("options").GetInt32()
                && r.GetProperty("target").GetInt32() == disabled.GetProperty("target").GetInt32());
            Assert.Equal(disabled.GetProperty("chain").GetRawText(), enabled.GetProperty("chain").GetRawText());
        }
    }

    [Fact]
    public void CostsAndPrioritiesStillMatchTheCoreRulesInsideTheGatedSearch()
    {
        using JsonDocument document = Vectors();
        foreach (JsonElement run in document.RootElement.GetProperty("cases").EnumerateArray())
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

    private static JsonDocument Vectors()
    {
        using FileStream file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "AiPathGlobalBranchVectors.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip);
    }
}
