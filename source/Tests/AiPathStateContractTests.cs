using System.IO.Compression;
using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiPathStateContractTests
{
    [Fact]
    public void FrozenOriginalTableMatchesTheExplicitlyScopedConstructorContract()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("clockStable").GetBoolean());
        Assert.Equal(root.GetProperty("clockBefore").GetRawText(), root.GetProperty("clockAfter").GetRawText());
        Assert.False(root.GetProperty("gameAccess").GetBoolean());
        ulong[] pointers = root.GetProperty("tileTable").GetProperty("pointers").EnumerateArray().Select(x => x.GetUInt64()).ToArray();
        Assert.True(AiPathSearchRules.HasConstructedTileTable(pointers));
        Assert.False(AiPathSearchRules.HasConstructedTileTable(pointers.AsSpan(1)));
        // Corrupting a controller input checks the guard; these are not native vectors.
        ulong original = pointers[11];
        pointers[11] = 0;
        Assert.False(AiPathSearchRules.HasConstructedTileTable(pointers));
        pointers[11] = original + 1;
        Assert.False(AiPathSearchRules.HasConstructedTileTable(pointers));
        pointers = Enumerable.Range(0, 48_400).Select(i => unchecked(ulong.MaxValue - 31 + (ulong)i * 32)).ToArray();
        Assert.False(AiPathSearchRules.HasConstructedTileTable(pointers));
        Assert.Contains("successful original allocation", root.GetProperty("codeContracts").GetProperty("tileTable").GetProperty("scope").GetString());
        Assert.Equal(0, root.GetProperty("nativeSearchCalls").GetInt32());
    }

    [Fact]
    public void OriginalAdmissionRejectsActualFacilitiesOutsideTheTerrainExceptions()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        JsonElement admission = root.GetProperty("facilityAdmission");
        Assert.Equal(7, admission.GetProperty("selectedCategoryTerrainPairs").GetInt32());
        Assert.Equal(14, admission.GetProperty("cases").GetArrayLength());
        Assert.Empty(admission.GetProperty("helperReplacements").EnumerateArray());
        Assert.Empty(admission.GetProperty("fieldEdits").EnumerateArray());
        foreach (JsonElement row in admission.GetProperty("cases").EnumerateArray())
        {
            Assert.Contains(row.GetProperty("site").GetProperty("category").GetInt32(), new[] { 2, 3 });
            Assert.Equal(0, row.GetProperty("mask64").GetInt32());
            Assert.Equal(0, row.GetProperty("firstAdmission").GetInt32());
            int terrain = row.GetProperty("fallbackTerrain").GetInt32();
            Assert.Equal(terrain, row.GetProperty("site").GetProperty("terrainCode").GetInt32());
            Assert.False(AiPathSearchRules.HasTerrainAdmissionException(terrain));
            Assert.True(row.GetProperty("reachedFallbackReject").GetBoolean());
            Assert.Equal(0, row.GetProperty("native").GetInt32());
        }
        int[] exceptions = root.GetProperty("codeContracts").GetProperty("facility").GetProperty("exceptionTerrainCodes").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        Assert.Equal(new[] { 16, 17 }, exceptions);
        Assert.All(exceptions, value => Assert.True(AiPathSearchRules.HasTerrainAdmissionException(value)));
    }

    [Fact]
    public void ScalarCallPruningPreservesTheUnresolvedMutationBoundary()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        JsonElement graph = root.GetProperty("globalCallClosure");
        Assert.True(graph.GetProperty("directWorklistComplete").GetBoolean());
        Assert.NotEmpty(graph.GetProperty("unresolvedBoundaries").EnumerateArray());
        Assert.False(root.GetProperty("transitiveMutationClosureProved").GetBoolean());
        Assert.False(graph.GetProperty("transitiveMutationClosureProved").GetBoolean());
        Assert.False(root.GetProperty("loopTakenObserved").GetBoolean());
        JsonElement scalar = graph.GetProperty("scalarContext");
        Assert.False(scalar.GetProperty("nativeObserved").GetBoolean());
        int[] reachable = scalar.GetProperty("reachable").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        Assert.DoesNotContain(0x185D22, reachable);
        Assert.DoesNotContain(0x185EBA, reachable);
        foreach (int rva in new[] { 0x185C09, 0x185DBA })
            Assert.True(scalar.GetProperty("decisions").EnumerateArray().Single(x => x.GetProperty("rva").GetInt32() == rva).GetProperty("taken").GetBoolean());
    }

    private static JsonDocument Vectors()
    {
        using FileStream file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "AiPathStateContracts.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip);
    }
}
