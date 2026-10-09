using System.IO.Compression;
using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiPathCreationContractTests
{
    [Fact]
    public void OriginalAssignmentClampIsScopedAndCannotProduceAnOutOfTableCommander()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        JsonElement assignment = root.GetProperty("codeContracts").GetProperty("commanderAssignment");
        Assert.True(assignment.GetProperty("dominates").GetBoolean());
        Assert.False(assignment.GetProperty("nativeObserved").GetBoolean());
        Assert.False(root.GetProperty("globalCreationInvariantProved").GetBoolean());
        Assert.Equal(0x2D2AAA, Assert.Single(assignment.GetProperty("directStores").EnumerateArray()).GetInt32());
        Assert.Contains(assignment.GetProperty("anchors").EnumerateArray(), row =>
            row.GetProperty("rva").GetInt32() == 0x2D2AA7 && row.GetProperty("text").GetString() == "cmovg eax, ecx");
        foreach (int request in new[] { int.MinValue, -1, 0, 1, 5999, 6000, 6001, 65535, int.MaxValue })
        {
            int result = AiPathSearchRules.CreatedCommanderId(request);
            Assert.InRange(result, assignment.GetProperty("minimum").GetInt32(), assignment.GetProperty("maximum").GetInt32());
            if (request is >= 0 and <= 6000) Assert.Equal(request, result);
        }
    }

    [Fact]
    public void AllActualCategoryTwoAndThreeDefinitionMasksRejectBothExceptionTerrains()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("clockStable").GetBoolean());
        Assert.Equal(root.GetProperty("clockBefore").GetRawText(), root.GetProperty("clockAfter").GetRawText());
        Assert.False(root.GetProperty("gameAccess").GetBoolean());
        Assert.Equal(0, root.GetProperty("gameWrites").GetInt32());
        Assert.Empty(root.GetProperty("sourceFieldEdits").EnumerateArray());
        Assert.Empty(root.GetProperty("helperReplacements").EnumerateArray());
        Assert.Empty(root.GetProperty("codeSource").GetProperty("addedTextPageRvas").EnumerateArray());
        Assert.Equal(0, root.GetProperty("codeSource").GetProperty("mutablePagesAdded").GetInt32());
        JsonElement[] definitions = root.GetProperty("definitions").EnumerateArray().ToArray();
        Assert.Equal(Enumerable.Range(0, 31), definitions.Select(row => row.GetProperty("index").GetInt32()));
        JsonElement[] facilities = definitions.Where(row => row.TryGetProperty("category", out JsonElement category) && category.GetInt32() is 2 or 3).ToArray();
        Assert.Equal(10, facilities.Length);
        foreach (JsonElement row in facilities)
        {
            uint encoded = 0; int index = 0;
            foreach (JsonElement value in row.GetProperty("encodedBytes").EnumerateArray()) encoded |= value.GetUInt32() << (8 * index++);
            uint mask = AiFacilityPlacementRules.TerrainMask(row.GetProperty("parameterBytes").GetUInt64(),
                row.GetProperty("legacyMask").GetUInt32(), encoded, row.GetProperty("index").GetInt32());
            Assert.Equal(row.GetProperty("decodedMask").GetUInt32(), mask);
            int[] terrains = row.GetProperty("terrains").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            int[] native = row.GetProperty("native").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            Assert.Equal(new[] { 16, 17 }, terrains);
            Assert.Equal(new[] { 0, 0 }, native);
            for (int i = 0; i < terrains.Length; i++) Assert.Equal(native[i] != 0, AiFacilityPlacementRules.AllowsTerrain(mask, (uint)terrains[i]));
        }
        Assert.Equal(20, root.GetProperty("terrainMaskNativeCalls").GetInt32());
        Assert.Equal(0, root.GetProperty("nativeSearchCalls").GetInt32());
        Assert.False(root.GetProperty("loopTakenObserved").GetBoolean());
    }

    [Fact]
    public void IntactCurrentCityAndGateTilesNeverReachTheNonBaseFacilityFallback()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("buildingTable").GetProperty("verified").GetBoolean());
        Assert.Equal(0, root.GetProperty("dummyBaseValidity").GetProperty("city").GetInt32());
        Assert.Equal(0, root.GetProperty("dummyBaseValidity").GetProperty("gate").GetInt32());
        Assert.Equal(0x1A, root.GetProperty("codeContracts").GetProperty("sharedBuildingId").GetProperty("tileOffset").GetInt32());
        JsonElement[] sites = root.GetProperty("exceptionTerrainSites").EnumerateArray().ToArray();
        Assert.Equal(399, sites.Length);
        foreach (JsonElement site in sites)
        {
            int terrain = site.GetProperty("terrain").GetInt32();
            int building = site.GetProperty("buildingId").GetInt32();
            Assert.InRange(building, terrain == 16 ? 1 : 52, terrain == 16 ? 51 : 61);
            Assert.False(AiPathSearchRules.CanEnterNonBaseFacilityFallback(true, terrain));
        }
        Assert.False(AiPathSearchRules.CanEnterNonBaseFacilityFallback(false, 17));
        // A necessary neutral terrain16 candidate is not proof of an admitted facility or a natural game state.
        Assert.True(AiPathSearchRules.CanEnterNonBaseFacilityFallback(false, 16));
    }

    private static JsonDocument Vectors()
    {
        using FileStream file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "AiPathCreationContracts.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip);
    }
}
