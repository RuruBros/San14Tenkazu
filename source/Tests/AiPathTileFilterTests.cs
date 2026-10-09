using System.IO.Compression;
using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiPathTileFilterTests
{
    [Fact]
    public void ActualBorderUnitAndTerrainInputsExecuteBothNativeBackends()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("clockStable").GetBoolean());
        Assert.Empty(root.GetProperty("failures").EnumerateArray());
        Assert.Equal(160, root.GetProperty("cases").GetArrayLength());
        Assert.Equal(new[] { "border", "occupied", "terrain7" }, root.GetProperty("tileSites").EnumerateArray().Select(s => s.GetProperty("tag").GetString()));
        foreach ((string backend, int[] addresses) in new[]
            { ("0x183a30", new[] { 0x184023, 0x184094, 0x184140, 0x184163 }), ("0x184eb0", new[] { 0x18529D, 0x185372, 0x1853A1 }) })
            foreach (int address in addresses)
            {
                JsonElement condition = root.GetProperty("branchCoverage").GetProperty(backend).EnumerateArray().Single(b => b.GetProperty("rva").GetInt32() == address);
                Assert.True(condition.GetProperty("taken").GetBoolean());
                Assert.True(condition.GetProperty("fallthrough").GetBoolean());
            }
    }

    [Fact]
    public void SegmentTerrainPenaltyMatchesTheActualMobilityHelpersInput()
    {
        using JsonDocument document = Vectors();
        int checkedInputs = 0;
        var flags = new HashSet<int>();
        var bypassOptions = new HashSet<int>();
        foreach (JsonElement run in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            JsonElement site = run.GetProperty("tileSite");
            if (site.GetProperty("tag").GetString() != "terrain7") continue;
            long tile = site.GetProperty("tile").GetInt64();
            Assert.NotEqual(run.GetProperty("source").GetInt32(), site.GetProperty("hex").GetInt32());
            int flag = run.GetProperty("contextWords").EnumerateArray().Where(w => w[0].GetInt32() == 0x2C).Select(w => w[1].GetInt32()).DefaultIfEmpty(0).Single();
            int options = run.GetProperty("options").GetInt32();
            int? baseCost = null;
            foreach (JsonElement helper in run.GetProperty("helperCalls").EnumerateArray())
            {
                int rva = helper.GetProperty("rva").GetInt32();
                if (rva == 0x2AE6F0 && helper.GetProperty("args")[0].GetInt64() == tile)
                    baseCost = unchecked((int)helper.GetProperty("result").GetUInt32());
                if (rva != 0x1864F0 || helper.GetProperty("args")[2].GetInt64() != tile) continue;
                int originalCost = AiPathSearchRules.UsesHeuristic(options) ? Assert.IsType<int>(baseCost) : 10_000;
                int actualInput = unchecked((int)helper.GetProperty("args")[0].GetInt64());
                int expected = run.GetProperty("rva").GetInt32() == 0x183A30
                    ? AiPathSearchRules.ApplyTerrainPenalty(originalCost, flag, site.GetProperty("terrain").GetInt32(), options) : originalCost;
                Assert.Equal(expected, actualInput);
                checkedInputs++;
                if (run.GetProperty("rva").GetInt32() == 0x183A30 && flag != 0 && (options & 0xC) != 0) bypassOptions.Add(options);
                if (site.GetProperty("tag").GetString() == "terrain7") flags.Add(flag);
            }
        }
        Assert.True(checkedInputs >= 28);
        Assert.Equal(new[] { 0, 1 }, flags.Order());
        Assert.Contains(5, bypassOptions);
        Assert.Contains(9, bypassOptions);
    }

    [Fact]
    public void Option16RejectsTheActualOccupiedCandidateBeforeCostAdjustment()
    {
        using JsonDocument document = Vectors();
        int checkedInputs = 0;
        foreach (JsonElement run in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            JsonElement site = run.GetProperty("tileSite");
            if (site.GetProperty("tag").GetString() != "occupied" || (run.GetProperty("options").GetInt32() & 16) == 0) continue;
            Assert.InRange(site.GetProperty("occupant").GetInt32(), 1, 500);
            JsonElement[] calls = run.GetProperty("helperCalls").EnumerateArray().ToArray();
            Assert.Contains(calls, c => c.GetProperty("rva").GetInt32() == 0x2C6950 && c.GetProperty("args")[0].GetInt64() == site.GetProperty("hex").GetInt32() && c.GetProperty("result").GetInt32() == 1);
            Assert.DoesNotContain(calls, c => c.GetProperty("rva").GetInt32() == 0x1864F0 && c.GetProperty("args")[2].GetInt64() == site.GetProperty("tile").GetInt64());
            checkedInputs++;
        }
        Assert.Equal(12, checkedInputs);
    }

    [Fact]
    public void TileFilteredSearchesKeepTheNativeCostAndPriorityRules()
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
                bool distanceRead = row.TryGetProperty("distance", out JsonElement distance);
                int options = run.GetProperty("options").GetInt32();
                Assert.Equal(distanceRead, AiPathSearchRules.UsesHeuristic(options));
                float factor = BitConverter.Int32BitsToSingle(unchecked((int)row.GetProperty("factorBits").GetUInt32()));
                Assert.Equal(priority.GetInt32(), AiPathSearchRules.Priority(cost, distanceRead ? distance.GetInt32() : 0, factor, options));
            }
    }

    private static JsonDocument Vectors()
    {
        using FileStream file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "AiPathTileFilterVectors.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip);
    }
}
