using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiFacilitySpacingTests
{
    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "AiFacilitySpacingVectors.json")));
    private static JsonElement[] Items(JsonElement value, string name) => value.GetProperty(name).EnumerateArray().ToArray();
    private static int Mode(JsonElement row) => unchecked((int)row.GetProperty("mode").GetUInt32());
    private static byte Spacing(JsonElement row) => AiFacilityPlacementRules.SpacingParameter(
        row.GetProperty("parameterBytes").GetInt64(), row.GetProperty("legacySpacing").GetByte(),
        row.GetProperty("encodedByte").GetByte(), 5);

    [Fact]
    public void FullNativeSpacingCallsMatchTheCategoryAndDistanceRules()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("clockStable").GetBoolean());
        Assert.True(root.GetProperty("completeMatrix").GetBoolean());
        Assert.Empty(Items(root, "failures"));
        Assert.Equal(492, root.GetProperty("plannedCases").GetInt32());
        JsonElement[] rows = Items(root, "cases");
        Assert.Equal(492, rows.Length);
        foreach (JsonElement row in rows)
        {
            bool blocked = AiFacilityPlacementRules.BlocksSpacing(row.GetProperty("distance").GetInt32(),
                row.GetProperty("targetValidBuilding").GetBoolean(), row.GetProperty("targetValidDefinition").GetBoolean(),
                row.GetProperty("targetCategory").GetByte(), Mode(row), Spacing(row));
            Assert.Equal(blocked ? 0 : 1, row.GetProperty("native").GetInt32());
        }
    }

    [Fact]
    public void NativeByteDecoderAndLegacySpacingAgreeAtFormatBoundaries()
    {
        using JsonDocument document = Vectors();
        JsonElement[] rows = Items(document.RootElement, "cases");
        Assert.Equal(new[] { 0, 72, 75, 76, 77, 78, 79, 80 },
            rows.Select(r => r.GetProperty("parameterBytes").GetInt32()).Distinct().Order().ToArray());
        foreach (JsonElement row in rows)
        {
            Assert.Equal(row.GetProperty("spacing").GetByte(), Spacing(row));
            JsonElement[] reads = Items(row, "frames").Where(f => f.GetProperty("rva").GetUInt32() == 0x27BED8).ToArray();
            foreach (JsonElement frame in reads) Assert.Equal(Spacing(row), frame.GetProperty("spacing").GetByte());
            JsonElement[] decodes = Items(row, "events").Where(e => e.GetProperty("rva").GetUInt32() == 0xDB00).ToArray();
            bool encoded = row.GetProperty("parameterBytes").GetInt32() is >= 76 and <= 79;
            Assert.Equal(encoded ? reads.Length : 0, decodes.Length);
            foreach (JsonElement call in decodes)
            {
                Assert.Equal(3U, (uint)(call.GetProperty("args")[2].GetUInt64() & 255));
                Assert.Equal(5U, call.GetProperty("args")[3].GetUInt32());
                Assert.Equal(Spacing(row), unchecked((byte)call.GetProperty("result").GetUInt32()));
            }
        }
    }

    [Fact]
    public void NativeQueriesScanExactRingsOneThroughFourAndStopAtTheFirstBlocker()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Dictionary<int, ulong[]> rings = Items(root, "rings").ToDictionary(r => r.GetProperty("distance").GetInt32(), r =>
            Items(r, "items").Select(h => h.GetProperty("pointer").GetUInt64()).ToArray());
        foreach (JsonElement ring in Items(root, "rings"))
        {
            int distance = ring.GetProperty("distance").GetInt32();
            Assert.Equal(distance * 6, Items(ring, "items").Length);
            Assert.All(Items(ring, "items"), h => Assert.Equal(distance, h.GetProperty("distance").GetInt32()));
        }
        foreach (JsonElement row in Items(root, "cases"))
        {
            JsonElement[] queries = Items(row, "events").Where(e => e.GetProperty("rva").GetUInt32() == 0x2B4750).ToArray();
            int last = row.GetProperty("native").GetInt32() == 0 ? row.GetProperty("distance").GetInt32() : 4;
            Assert.Equal(Enumerable.Range(1, last), queries.Select(q => q.GetProperty("args")[1].GetInt32()));
            foreach (JsonElement query in queries)
            {
                int distance = query.GetProperty("args")[1].GetInt32();
                Assert.Equal(root.GetProperty("centerHex").GetInt32(), query.GetProperty("args")[0].GetInt32());
                Assert.Equal(rings[distance], query.GetProperty("list").EnumerateArray().Select(h => h.GetUInt64()).ToArray());
                Assert.Equal(distance * 6, query.GetProperty("result").GetInt32());
            }
        }
    }

    [Fact]
    public void FilteredCategoriesAndMissingDefinitionsNeverReadTheSpacingByte()
    {
        using JsonDocument document = Vectors();
        JsonElement[] rows = Items(document.RootElement, "cases");
        foreach (JsonElement row in rows)
        {
            bool eligible = AiFacilityPlacementRules.BlocksSpacing(row.GetProperty("distance").GetInt32(),
                row.GetProperty("targetValidBuilding").GetBoolean(), row.GetProperty("targetValidDefinition").GetBoolean(),
                row.GetProperty("targetCategory").GetByte(), Mode(row), 255);
            Assert.Equal(eligible, Items(row, "frames").Any(f => f.GetProperty("rva").GetUInt32() == 0x27BED8));
        }
        foreach (string variant in new[] { "emptyTarget", "invalidKind31", "invalidKind255", "missingDefinition", "centerOnly", "beyondDistance4" })
        {
            JsonElement[] controls = rows.Where(r => r.GetProperty("variant").GetString() == variant).ToArray();
            Assert.Equal(2, controls.Length);
            Assert.All(controls, r => Assert.Equal(1, r.GetProperty("native").GetInt32()));
        }
        Assert.Equal(new[] { 0, 1, 52, 255 }, rows.Where(r => r.GetProperty("variant").GetString() == "ownerByte")
            .Select(r => r.GetProperty("ownerInput").GetInt32()).ToArray());
        Assert.All(rows.Where(r => r.GetProperty("variant").GetString() == "ownerByte"),
            r => Assert.Equal(0, r.GetProperty("native").GetInt32()));
    }
}
