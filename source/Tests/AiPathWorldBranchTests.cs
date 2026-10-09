using System.IO.Compression;
using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiPathWorldBranchTests
{
    [Fact]
    public void RealRoutesArmiesAndFacilitiesExerciseBothSearchBackendsWithoutNativeFaults()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("clockStable").GetBoolean());
        Assert.Empty(root.GetProperty("failures").EnumerateArray());
        JsonElement[] runs = root.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(298, runs.Length);
        Assert.Equal(8, root.GetProperty("worldSelection").GetProperty("armyProfiles").GetArrayLength());
        Assert.Contains(root.GetProperty("worldSelection").GetProperty("armyProfiles").EnumerateArray(), a => a.GetProperty("skill102").GetInt32() == 1);
        Assert.Equal(2, root.GetProperty("worldSelection").GetProperty("facilitySites").GetArrayLength());
        Assert.True(runs.Max(r => r.GetProperty("chain").GetArrayLength()) >= 17);
        foreach (JsonElement run in runs)
        {
            JsonElement[] chain = run.GetProperty("chain").EnumerateArray().ToArray();
            if (chain.Length == 0) continue;
            Assert.Equal(run.GetProperty("source").GetInt32(), chain[0].GetProperty("hex").GetInt32());
            Assert.Contains(chain[^1].GetProperty("hex").GetInt32(), run.GetProperty("goals").EnumerateArray().Select(g => g.GetInt32()));
            Assert.Equal(0, chain[^1].GetProperty("cost").GetInt32());
        }
        foreach ((string backend, int address) in new[] { ("0x183a30", 0x183B9E), ("0x184eb0", 0x185008) })
        {
            JsonElement condition = root.GetProperty("branchCoverage").GetProperty(backend).EnumerateArray().Single(b => b.GetProperty("rva").GetInt32() == address);
            Assert.True(condition.GetProperty("taken").GetBoolean());
            Assert.True(condition.GetProperty("fallthrough").GetBoolean());
        }
    }

    [Fact]
    public void NativeExclusionListsBlockIntermediateTilesAndKeepTheSourceExceptionDistinct()
    {
        using JsonDocument document = Vectors();
        JsonElement[] runs = document.RootElement.GetProperty("cases").EnumerateArray().Where(r => r.TryGetProperty("excludedHexes", out _)).ToArray();
        Assert.Equal(48, runs.Length);
        foreach (JsonElement run in runs)
        {
            int[] excluded = run.GetProperty("excludedHexes").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            JsonElement list = run.GetProperty("nativeExclusionList");
            Assert.InRange(list.GetProperty("index").GetInt32(), 0, 0x13FFF);
            Assert.Equal(excluded, list.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("hex").GetInt32()));
            Assert.Equal(list.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("tile").GetInt64()), list.GetProperty("nativeTiles").EnumerateArray().Select(e => e.GetInt64()));
            if (run.GetProperty("rva").GetInt32() != 0x183A30) continue;
            int source = run.GetProperty("source").GetInt32();
            int[] goals = run.GetProperty("goals").EnumerateArray().Select(g => g.GetInt32()).ToArray();
            foreach (JsonElement node in run.GetProperty("chain").EnumerateArray())
            {
                int hex = node.GetProperty("hex").GetInt32();
                if (hex != source && !goals.Contains(hex)) Assert.DoesNotContain(hex, excluded);
            }
        }
        foreach (IGrouping<(int Rva, int Options, int Target), JsonElement> group in runs.GroupBy(r =>
            (r.GetProperty("rva").GetInt32(), r.GetProperty("options").GetInt32(), r.GetProperty("target").GetInt32())))
        {
            JsonElement empty = group.Single(r => r.GetProperty("excludedHexes").GetArrayLength() == 0);
            JsonElement sourceOnly = group.Single(r => r.GetProperty("excludedHexes").GetArrayLength() == 1 && r.GetProperty("excludedHexes")[0].GetInt32() == r.GetProperty("source").GetInt32());
            Assert.Equal(empty.GetProperty("chain").GetRawText(), sourceOnly.GetProperty("chain").GetRawText());
            if (group.Key.Rva == 0x184EB0)
                foreach (JsonElement run in group) Assert.Equal(empty.GetProperty("chain").GetRawText(), run.GetProperty("chain").GetRawText());
            if (group.Key == (0x183A30, 1, 12558))
            {
                Assert.Equal(600, empty.GetProperty("chain")[0].GetProperty("cost").GetInt32());
                JsonElement blocked = group.Single(r => r.GetProperty("excludedHexes").GetArrayLength() == 1 && r.GetProperty("excludedHexes")[0].GetInt32() == 12779);
                Assert.Equal(4, blocked.GetProperty("chain").GetArrayLength());
                Assert.Equal(900, blocked.GetProperty("chain")[0].GetProperty("cost").GetInt32());
            }
        }
    }

    [Fact]
    public void ExtendedWorldCostsAndPrioritiesMatchTheVerifiedCoreRules()
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
                Assert.Equal(cost, row.GetProperty("storedCost").GetInt32());
                Assert.Equal(row.GetProperty("updatedFlags").GetByte(), AiPathSearchRules.LinkedFlags(flags));
            }
    }

    private static JsonDocument Vectors()
    {
        using FileStream file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "AiPathWorldBranchVectors.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip);
    }
}
