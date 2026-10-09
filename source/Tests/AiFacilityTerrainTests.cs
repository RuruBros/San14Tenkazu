using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiFacilityTerrainTests
{
    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "AiFacilityTerrainVectors.json")));

    [Fact]
    public void BothStoredFormatsMatchTheOriginalTerrainMaskFunction()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        uint[] terrains = root.GetProperty("terrains").EnumerateArray().Select(x => x.GetUInt32()).ToArray();
        Assert.Equal(38332, root.GetProperty("nativeCalls").GetInt32());
        Assert.Equal(0xDB00U, root.GetProperty("decoder").GetUInt32());
        int calls = 0;
        foreach (JsonElement row in root.GetProperty("cases").EnumerateArray())
        {
            uint encoded = 0;
            int index = 0;
            foreach (JsonElement value in row.GetProperty("encodedBytes").EnumerateArray())
                encoded |= value.GetUInt32() << (index++ * 8);
            Assert.Equal(4, index);
            uint mask = AiFacilityPlacementRules.TerrainMask(row.GetProperty("parameterBytes").GetUInt64(),
                row.GetProperty("legacyMask").GetUInt32(), encoded, row.GetProperty("kind").GetInt32());
            Assert.Equal(row.GetProperty("observation").GetProperty("mask").GetUInt32(), mask);
            int[] native = row.GetProperty("native").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            Assert.Equal(terrains.Length, native.Length);
            for (int i = 0; i < terrains.Length; i++)
                Assert.Equal(native[i] != 0, AiFacilityPlacementRules.AllowsTerrain(mask, terrains[i]));
            calls += native.Length;
        }
        Assert.Equal(38332, calls);
    }

    [Fact]
    public void NativeFormatSelectionRoundsDownBytesAndDecodesFourLowBytes()
    {
        using JsonDocument document = Vectors();
        JsonElement[] rows = document.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(new[] { 0, 72, 75, 76, 77, 78, 79, 80 },
            rows.Select(r => r.GetProperty("parameterBytes").GetInt32()).Distinct().Order().ToArray());
        foreach (JsonElement row in rows)
        {
            int bytes = row.GetProperty("parameterBytes").GetInt32();
            bool encoded = bytes is >= 76 and <= 79;
            JsonElement observed = row.GetProperty("observation");
            Assert.Equal(encoded ? "encoded" : "legacy", observed.GetProperty("mode").GetString());
            Assert.Equal(encoded ? 4 : 0, observed.GetProperty("decoderCalls").GetInt32());
        }
        Assert.Equal(new[] { 0, 1, 30, 31, 255, 256 }, rows.Where(r => r.GetProperty("parameterBytes").GetInt32() == 76)
            .Select(r => r.GetProperty("kind").GetInt32()).Distinct().Order().ToArray());
    }

    [Fact]
    public void NativeUnsignedTerrainLimitRejectsBit31AndLargerIndices()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        uint[] terrains = root.GetProperty("terrains").EnumerateArray().Select(x => x.GetUInt32()).ToArray();
        JsonElement[] allBits = root.GetProperty("cases").EnumerateArray()
            .Where(r => r.GetProperty("observation").GetProperty("mask").GetUInt32() == uint.MaxValue).ToArray();
        Assert.NotEmpty(allBits);
        foreach (JsonElement row in allBits)
        {
            int[] native = row.GetProperty("native").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            for (int i = 0; i < terrains.Length; i++) Assert.Equal(terrains[i] <= 30 ? 1 : 0, native[i]);
        }
        Assert.Contains(31U, terrains);
        Assert.Contains(uint.MaxValue, terrains);
        Assert.Contains(0x80000000U, terrains);
    }
}
