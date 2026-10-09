using System.IO.Compression;
using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiPathFacilityBranchTests
{
    [Fact]
    public void ActualLinkedFacilitiesAndForce51ExecuteWholeBackends()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("clockStable").GetBoolean());
        Assert.Equal(root.GetProperty("clockBefore").GetRawText(), root.GetProperty("clockAfter").GetRawText());
        Assert.Empty(root.GetProperty("failures").EnumerateArray());
        Assert.Equal(162, root.GetProperty("cases").GetArrayLength());
        JsonElement selection = root.GetProperty("facilitySelection");
        Assert.Equal(43, selection.GetProperty("inventory").GetArrayLength());
        Assert.Equal(new[] { 2, 3, 4 }, selection.GetProperty("sites").EnumerateArray().Select(s => s.GetProperty("category").GetInt32()).Order());
        Assert.Contains(selection.GetProperty("boundaryProfiles").EnumerateArray(), p => p.GetProperty("force").GetInt32() == 51);
        foreach (JsonElement run in root.GetProperty("cases").EnumerateArray())
        {
            JsonElement site = run.GetProperty("facilitySite");
            Assert.NotEqual(run.GetProperty("source").GetInt32(), site.GetProperty("hex").GetInt32());
            Assert.NotEqual(run.GetProperty("target").GetInt32(), site.GetProperty("hex").GetInt32());
            Assert.Equal(0, site.GetProperty("city").GetInt32());
            Assert.Equal(0, site.GetProperty("gate").GetInt32());
            Assert.Empty(run.GetProperty("changes").EnumerateArray());
            Assert.Equal(run.GetProperty("source").GetInt32(), run.GetProperty("chain")[0].GetProperty("hex").GetInt32());
            Assert.Equal(run.GetProperty("target").GetInt32(), run.GetProperty("chain")[run.GetProperty("chain").GetArrayLength() - 1].GetProperty("hex").GetInt32());
        }
    }

    [Fact]
    public void FacilityFilterMatchesNativeCostEntryAfterActualAdmission()
    {
        using JsonDocument document = Vectors();
        int checkedInputs = 0;
        var categories = new HashSet<int>();
        var decisions = new HashSet<(bool Bounded, bool Mask64, bool EqualOwner, bool Allowed)>();
        foreach (JsonElement run in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            int options = run.GetProperty("options").GetInt32();
            if ((options & 0xC) != 0) continue; // A separate native helper handles these options.
            JsonElement site = run.GetProperty("facilitySite");
            long tile = site.GetProperty("tile").GetInt64(), building = site.GetProperty("building").GetInt64();
            bool bounded = run.GetProperty("rva").GetInt32() == 0x184EB0;
            JsonElement[] calls = run.GetProperty("helperCalls").EnumerateArray().ToArray();
            bool mask64 = (options & 64) != 0;
            bool entered = mask64
                ? bounded ? calls.Any(c => Call(c, 0x22FB10, 0x1852AB, 0, tile))
                    : calls.Any(c => Call(c, 0x18A080, 0x184006, 1, tile) && c.GetProperty("result").GetInt32() == 1)
                : calls.Any(c => Call(c, 0xEE90, bounded ? 0x1852DE : 0x1840D8, 0, building));
            if (!entered) continue;
            int owner = site.GetProperty("owner").GetInt32(), searchOwner = run.GetProperty("resolvedSearchOwner").GetInt32();
            int category = site.GetProperty("category").GetInt32();
            bool actual = calls.Any(c => Call(c, 0x1864F0, bounded ? 0x1853F1 : 0x1841B4, 2, tile));
            Assert.Equal(actual, AiPathSearchRules.PassesFacilityFilter(bounded, category, owner, searchOwner, options));
            categories.Add(category); decisions.Add((bounded, mask64, owner == searchOwner, actual)); checkedInputs++;
        }
        Assert.True(checkedInputs >= 70);
        Assert.Equal(new[] { 2, 3, 4 }, categories.Order());
        Assert.Contains((true, true, true, false), decisions);
        Assert.Contains((true, true, false, true), decisions);
        Assert.Contains((false, true, true, true), decisions);
        Assert.Contains((false, true, false, true), decisions);
        Assert.Contains((true, false, true, true), decisions);
    }

    [Fact]
    public void EarlierNativeAdmissionRejectsSelectedCategory2And3WithoutMask64()
    {
        using JsonDocument document = Vectors();
        var observed = new HashSet<(int Category, bool Bounded)>();
        foreach (JsonElement run in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            int options = run.GetProperty("options").GetInt32();
            JsonElement site = run.GetProperty("facilitySite");
            int category = site.GetProperty("category").GetInt32();
            if ((options & 0x4C) != 0 || category is not (2 or 3)) continue;
            long tile = site.GetProperty("tile").GetInt64();
            bool bounded = run.GetProperty("rva").GetInt32() == 0x184EB0;
            JsonElement[] calls = run.GetProperty("helperCalls").EnumerateArray().ToArray();
            if (!calls.Any(c => Call(c, 0x18A080, bounded ? 0x185287 : 0x184006, 1, tile))) continue;
            Assert.All(calls.Where(c => Call(c, 0x18A080, bounded ? 0x185287 : 0x184006, 1, tile)), c => Assert.Equal(0, c.GetProperty("result").GetInt32()));
            Assert.DoesNotContain(calls, c => Call(c, 0x1864F0, bounded ? 0x1853F1 : 0x1841B4, 2, tile));
            observed.Add((category, bounded));
        }
        Assert.Equal(4, observed.Count);
    }

    [Fact]
    public void FacilitySearchCostsAndPrioritiesMatchTheCoreRules()
    {
        using JsonDocument document = Vectors();
        int checkedDecisions = 0;
        foreach (JsonElement run in document.RootElement.GetProperty("cases").EnumerateArray())
            foreach (JsonElement row in run.GetProperty("relaxations").EnumerateArray())
            {
                int cost = row.GetProperty("newCost").GetInt32();
                Assert.Equal(cost, AiPathSearchRules.Cost(row.GetProperty("parentCost").GetInt32(), row.GetProperty("stepCost").GetInt32()));
                Assert.Equal(row.GetProperty("accepted").GetBoolean(), AiPathSearchRules.CanRelax(row.GetProperty("flags").GetByte(), row.GetProperty("oldCost").GetInt32(), cost));
                checkedDecisions++;
                if (!row.TryGetProperty("priority", out JsonElement priority)) continue;
                bool distanceRead = row.TryGetProperty("distance", out JsonElement distance);
                int options = run.GetProperty("options").GetInt32();
                Assert.Equal(distanceRead, AiPathSearchRules.UsesHeuristic(options));
                float factor = BitConverter.Int32BitsToSingle(unchecked((int)row.GetProperty("factorBits").GetUInt32()));
                Assert.Equal(priority.GetInt32(), AiPathSearchRules.Priority(cost, distanceRead ? distance.GetInt32() : 0, factor, options));
            }
        Assert.Equal(1588, checkedDecisions);
    }

    private static bool Call(JsonElement call, int rva, int returnAddress, int argument, long value)
        => call.GetProperty("rva").GetInt32() == rva && call.GetProperty("return_address").GetInt32() == returnAddress && call.GetProperty("args")[argument].GetInt64() == value;

    private static JsonDocument Vectors()
    {
        using FileStream file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "AiPathFacilityBranchVectors.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip);
    }
}
