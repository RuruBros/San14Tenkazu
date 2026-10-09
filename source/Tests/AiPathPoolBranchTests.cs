using System.IO.Compression;
using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiPathPoolBranchTests
{
    [Fact]
    public void GenuineAllocatorsReachThePoolBoundaryAndKeepTheIndexLinksConsistent()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("clockStable").GetBoolean());
        Assert.Equal(3, root.GetProperty("stages").GetArrayLength());
        foreach (JsonElement stage in root.GetProperty("stages").EnumerateArray())
        {
            Assert.True(stage.GetProperty("clockStable").GetBoolean());
            Assert.Empty(stage.GetProperty("failures").EnumerateArray());
            Assert.Equal(12, stage.GetProperty("cases").GetArrayLength());
            Assert.Equal(stage.GetProperty("liveFlagsBefore").GetRawText(), stage.GetProperty("liveFlagsAfter").GetRawText());
            Assert.Equal("00000000", stage.GetProperty("liveFlagsBefore").GetProperty("0x2141a84").GetString());
            Assert.Equal("01000000", stage.GetProperty("liveFlagsBefore").GetProperty("0x27d10b0").GetString());
            JsonElement preparation = stage.GetProperty("preparation");
            Assert.Equal(preparation.GetProperty("after").GetProperty("indices").GetProperty("active").GetInt64(), preparation.GetProperty("validatedActiveIndices").GetInt64());
            string name = stage.GetProperty("stage").GetString()!;
            if (name == "unlocked")
            {
                Assert.Equal(0, preparation.GetProperty("allocations").GetInt32());
                continue;
            }
            JsonElement before = preparation.GetProperty("before").GetProperty(name);
            JsonElement after = preparation.GetProperty("after").GetProperty(name);
            Assert.Equal(before.GetProperty("capacity").GetInt64() - before.GetProperty("active").GetInt64(), preparation.GetProperty("allocations").GetInt64());
            Assert.Equal(after.GetProperty("capacity").GetInt64(), after.GetProperty("active").GetInt64());
            Assert.Equal(0, after.GetProperty("freeHead").GetInt64());
            Assert.Equal(0, preparation.GetProperty("exhaustedReturn").GetInt64());
        }
    }

    [Fact]
    public void AllocationFailuresSkipTheGlobalHelperAndPreserveTheseNativeRoutes()
    {
        using JsonDocument document = Vectors();
        JsonElement[] stages = document.RootElement.GetProperty("stages").EnumerateArray().ToArray();
        JsonElement[] unlocked = stages.Single(s => s.GetProperty("stage").GetString() == "unlocked").GetProperty("cases").EnumerateArray().ToArray();
        int helpers = 0;
        foreach (JsonElement stage in stages)
            foreach (JsonElement run in stage.GetProperty("cases").EnumerateArray())
            {
                JsonElement[] calls = run.GetProperty("helperCalls").EnumerateArray().ToArray();
                if (stage.GetProperty("stage").GetString() == "unlocked")
                {
                    Assert.NotEmpty(calls);
                    foreach (JsonElement call in calls)
                    {
                        Assert.Equal(1, call.GetProperty("result").GetInt32());
                        helpers++;
                    }
                }
                else Assert.Empty(calls);
                JsonElement baseline = unlocked.Single(b => b.GetProperty("context").GetString() == run.GetProperty("context").GetString()
                    && b.GetProperty("options").GetInt32() == run.GetProperty("options").GetInt32()
                    && b.GetProperty("target").GetInt32() == run.GetProperty("target").GetInt32());
                Assert.Equal(baseline.GetProperty("chain").GetRawText(), run.GetProperty("chain").GetRawText());
            }
        Assert.Equal(32, helpers);
    }

    [Fact]
    public void LockSelectionAndBothAllocationBoundariesHaveObservedNativeEdges()
    {
        using JsonDocument document = Vectors();
        JsonElement[] conditions = document.RootElement.GetProperty("stages").EnumerateArray()
            .SelectMany(s => s.GetProperty("branchCoverage").GetProperty("0x183a30").EnumerateArray()).ToArray();
        foreach (int address in new[] { 0x183D80, 0x183D9D, 0x183DC7, 0x183E0C, 0x183E3D, 0x183E49, 0x183E69, 0x183E91, 0x183EE6, 0x183EF4, 0x183F27 })
        {
            JsonElement[] rows = conditions.Where(b => b.GetProperty("rva").GetInt32() == address).ToArray();
            Assert.Contains(rows, b => b.GetProperty("taken").GetBoolean());
            Assert.Contains(rows, b => b.GetProperty("fallthrough").GetBoolean());
        }
    }

    private static JsonDocument Vectors()
    {
        using FileStream file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "AiPathPoolBranchVectors.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip);
    }
}
