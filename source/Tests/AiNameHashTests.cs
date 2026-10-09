using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiNameHashTests
{
    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "AiNameHashVectors.json")));
    private static JsonElement[] Items(JsonElement value, string name) => value.GetProperty(name).EnumerateArray().ToArray();

    [Fact]
    public void NativeNameHashMatchesAllByteValuesAndExistingSourceCandidates()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("clockStable").GetBoolean());
        JsonElement[] rows = Items(root, "cases");
        Assert.Equal(265, rows.Length);
        JsonElement[] bytes = rows.Where(r => r.GetProperty("role").GetString()!.StartsWith("first byte ")).ToArray();
        Assert.Equal(256, bytes.Length);
        Assert.Equal(Enumerable.Range(0, 256), bytes.Select(r => Convert.FromHexString(r.GetProperty("inputHex").GetString()!)[0]).Select(b => (int)b));
        foreach (JsonElement row in rows)
        {
            byte[] input = Convert.FromHexString(row.GetProperty("inputHex").GetString()!);
            uint hash = AiCatalog.NameHash(input);
            Assert.Equal(row.GetProperty("hash").GetUInt32(), hash);
            JsonElement call = row.GetProperty("event");
            Assert.Equal(0xB26300U, call.GetProperty("rva").GetUInt32());
            Assert.Equal(hash, (uint)call.GetProperty("args")[1].GetUInt64());
            Assert.Equal(row.GetProperty("lookupStatus").GetUInt32(), call.GetProperty("result").GetUInt32());
        }
    }

    [Fact]
    public void NativeNameHashStopsAtNulBeforeTheRemainingBytes()
    {
        using JsonDocument document = Vectors();
        JsonElement[] rows = Items(document.RootElement, "cases");
        foreach (JsonElement row in rows)
        {
            byte[] input = Convert.FromHexString(row.GetProperty("inputHex").GetString()!);
            int zero = Array.IndexOf(input, (byte)0);
            if (zero >= 0) Assert.Equal(row.GetProperty("hash").GetUInt32(), AiCatalog.NameHash(input.AsSpan(0, zero)));
        }
        Assert.Equal(AiCatalog.NameHash("A"), AiCatalog.NameHash("A\0BC"));
        JsonElement wide = rows.Single(r => r.GetProperty("role").GetString() == "UTF-16 source with early NUL");
        Assert.Equal(AiCatalog.NameHash("F"), wide.GetProperty("hash").GetUInt32());
        Assert.Equal(0U, rows.Single(r => r.GetProperty("role").GetString() == "first byte 0").GetProperty("hash").GetUInt32());
    }

    [Fact]
    public void TheOnlyAdditionalMatchRemainsAnAmbiguousHashRatherThanAnAssignedName()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.Equal(0, root.GetProperty("assignedNames").GetInt32());
        JsonElement match = Assert.Single(Items(root, "matches"));
        Assert.Equal("chs", match.GetProperty("candidate").GetString());
        Assert.Equal("unresolved parameter", match.GetProperty("kind").GetString());
        uint hash = match.GetProperty("hash").GetUInt32();
        Assert.Equal(AiCatalog.NameHash("chs"), hash);
        Assert.Equal(AiCatalog.NameHash("Dis"), hash);
        Assert.DoesNotContain(AiCatalog.Variables, entry => entry.Key == hash);
        Assert.Equal(75, root.GetProperty("blockHashes").GetInt32());
        Assert.Equal(280, root.GetProperty("unresolvedParameterHashes").GetInt32());
    }

    [Fact]
    public void SearchSourcesIncludeAllThreeTreeVersionsAndNulContainingEncodings()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.Equal(new[] { "bt.bin", "bt_ex.bin", "bt_exce.bin" }, Items(root, "treeSources").Select(t => t.GetProperty("file").GetString()).ToArray());
        Assert.All(Items(root, "treeSources"), t => Assert.Equal(4, Items(t, "treeCounts").Length));
        Assert.True(root.GetProperty("encodedCandidates").GetInt32() > 54262);
        Assert.True(root.GetProperty("candidatesWithNul").GetInt32() > 0);
    }
}
