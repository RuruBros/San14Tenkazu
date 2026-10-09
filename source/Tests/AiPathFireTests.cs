using System.IO.Compression;
using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiPathFireTests
{
    [Fact]
    public void ActualSavedFireExecutesTheCompleteNativeSearchWithoutIgniteOrTimerPokes()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("completed").GetBoolean());
        Assert.True(root.GetProperty("clockStable").GetBoolean());
        Assert.Equal(root.GetProperty("clockBefore").GetRawText(), root.GetProperty("clockAfter").GetRawText());
        Assert.False(root.GetProperty("gameAccess").GetBoolean());
        Assert.Empty(root.GetProperty("failures").EnumerateArray());
        Assert.Empty(root.GetProperty("helperReplacements").EnumerateArray());
        Assert.Empty(root.GetProperty("timerPokes").EnumerateArray());
        Assert.Empty(root.GetProperty("codeSource").GetProperty("addedTextPageRvas").EnumerateArray());
        Assert.True(root.GetProperty("listPoolSnapshot").GetProperty("valid").GetBoolean());
        JsonElement preparation = root.GetProperty("preparation");
        JsonElement attempt = Assert.Single(preparation.GetProperty("attempts").EnumerateArray());
        Assert.Equal("saved frozen tile", attempt.GetProperty("origin").GetString());
        Assert.False(attempt.GetProperty("igniteStarted").GetBoolean());
        Assert.Equal(attempt.GetProperty("before").GetString(), attempt.GetProperty("after").GetString());
        Assert.Equal(40, attempt.GetProperty("writtenTimer").GetInt32());
        Assert.Equal(2, root.GetProperty("cases").GetArrayLength());
        foreach (JsonElement run in root.GetProperty("cases").EnumerateArray())
        {
            Assert.Equal(0x183A30, run.GetProperty("rva").GetInt32());
            Assert.Equal(1, run.GetProperty("options").GetInt32());
            Assert.Empty(run.GetProperty("changes").EnumerateArray());
            Assert.Equal(3, run.GetProperty("chain").GetArrayLength());
        }
        JsonElement edge = root.GetProperty("branchCoverage").GetProperty("0x183a30")
            .EnumerateArray().Single(row => row.GetProperty("rva").GetInt32() == 0x184084);
        Assert.True(edge.GetProperty("taken").GetBoolean());
        Assert.True(edge.GetProperty("fallthrough").GetBoolean());
    }

    [Fact]
    public void CoreFireRuleMatchesOriginalFilterInputsAndNativeRejection()
    {
        using JsonDocument document = Vectors();
        JsonElement[] rows = document.RootElement.GetProperty("preparation").GetProperty("fireFilterInputs").EnumerateArray().ToArray();
        Assert.Equal(106, rows.Length);
        foreach (JsonElement row in rows)
            Assert.Equal(row.GetProperty("nativeRejected").GetBoolean(),
                AiPathSearchRules.RejectsFire(row.GetProperty("contextWord").GetInt32(), row.GetProperty("timer").GetUInt16()));
        Assert.Equal(12, rows.Count(row => row.GetProperty("nativeRejected").GetBoolean()));
        Assert.Contains(rows, row => row.GetProperty("contextWord").GetInt32() == 0 && row.GetProperty("timer").GetInt32() == 40 && !row.GetProperty("nativeRejected").GetBoolean());
        Assert.Contains(rows, row => row.GetProperty("contextWord").GetInt32() == 1 && row.GetProperty("timer").GetInt32() == 0 && !row.GetProperty("nativeRejected").GetBoolean());
    }

    [Fact]
    public void FireFilteredSearchesRetainTheOriginalCostAndPriorityRules()
    {
        using JsonDocument document = Vectors();
        int decisions = 0;
        foreach (JsonElement run in document.RootElement.GetProperty("cases").EnumerateArray())
            foreach (JsonElement row in run.GetProperty("relaxations").EnumerateArray())
            {
                int cost = row.GetProperty("newCost").GetInt32();
                Assert.Equal(cost, AiPathSearchRules.Cost(row.GetProperty("parentCost").GetInt32(), row.GetProperty("stepCost").GetInt32()));
                Assert.Equal(row.GetProperty("accepted").GetBoolean(), AiPathSearchRules.CanRelax(row.GetProperty("flags").GetByte(), row.GetProperty("oldCost").GetInt32(), cost));
                if (row.TryGetProperty("priority", out JsonElement priority))
                {
                    float factor = BitConverter.Int32BitsToSingle(unchecked((int)row.GetProperty("factorBits").GetUInt32()));
                    Assert.Equal(priority.GetInt32(), AiPathSearchRules.Priority(cost, row.GetProperty("distance").GetInt32(), factor, run.GetProperty("options").GetInt32()));
                }
                decisions++;
            }
        Assert.Equal(96, decisions);
    }

    private static JsonDocument Vectors()
    {
        using FileStream file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "AiPathFireVectors.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip);
    }
}
