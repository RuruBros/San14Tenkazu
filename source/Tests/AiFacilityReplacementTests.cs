using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiFacilityReplacementTests
{
    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "AiFacilityReplacementVectors.json")));
    private static JsonElement[] Items(JsonElement row, string name) => row.GetProperty(name).EnumerateArray().ToArray();
    private static int[] Numbers(JsonElement array) => array.EnumerateArray().Select(x => x.GetInt32()).ToArray();

    [Fact]
    public void NativeSecondPathSelectsAReplacementWithoutSettingTheSpecialFlag()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("clockStable").GetBoolean());
        Assert.Empty(Items(root, "failures"));
        Assert.Equal(10, Items(root, "cases").Length);
        HashSet<int> selected = [];
        foreach (JsonElement row in Items(root, "cases"))
        {
            Assert.Equal(1, row.GetProperty("status").GetInt32());
            Assert.Equal(0, row.GetProperty("special").GetInt32());
            JsonElement[] frames = Items(row, "frames");
            JsonElement? phase = frames.Where(f => f.GetProperty("rva").GetUInt32() == 0x3903A)
                .Select(f => (JsonElement?)f).SingleOrDefault();
            bool allowed = frames.Any(f => f.GetProperty("rva").GetUInt32() == 0x39304
                && f.GetProperty("placementResult").GetUInt32() != 0);
            int? expected = phase is { } frame ? AiExecutorRules.SpecialSupportFacilityKind(
                Numbers(frame.GetProperty("candidates")), frame.GetProperty("pendingCategory2").GetInt32() != 0, allowed) : null;
            Assert.Equal(expected.HasValue, frames.Any(f => f.GetProperty("rva").GetUInt32() == 0x39318));
            if (expected is { } kind)
            {
                selected.Add(kind);
                Assert.Equal(new[] { kind }, Numbers(row.GetProperty("nativeKinds")));
                Assert.Contains(Items(row, "events"), e => e.GetProperty("rva").GetUInt32() == 0x279190
                    && e.GetProperty("return_address").GetUInt32() == (kind == 6 ? 0x392EE : 0x39274)
                    && e.GetProperty("result").GetUInt32() != 0);
            }
        }
        Assert.Equal(new[] { 5, 6 }, selected.Order().ToArray());
    }

    [Fact]
    public void ActualNeighborCountsMatchTheSecondPlacementGate()
    {
        using JsonDocument document = Vectors();
        JsonElement[] rows = Items(document.RootElement, "cases");
        foreach (JsonElement row in rows)
        {
            JsonElement[] counts = Items(row, "frames").Where(f => f.GetProperty("rva").GetUInt32() == 0x391C0).ToArray();
            bool eligible = counts.Any(f => AiExecutorRules.SupportFacilityNeighborhoodAllowed(
                f.GetProperty("category3Neighbors").GetInt32(), f.GetProperty("category2Neighbors").GetInt32()));
            Assert.Equal(eligible, Items(row, "frames").Any(f => f.GetProperty("rva").GetUInt32() == 0x39304));
        }
        JsonElement Find(string variant) => rows.Single(r => r.GetProperty("variant").GetString() == variant);
        JsonElement absent = Items(Find("noCategory3Neighbor"), "frames").Single(f => f.GetProperty("rva").GetUInt32() == 0x391C0);
        Assert.Equal(0, absent.GetProperty("category3Neighbors").GetInt32());
        Assert.Equal(0, absent.GetProperty("category2Neighbors").GetInt32());
        JsonElement blocked = Items(Find("withCategory2Neighbor"), "frames").Single(f => f.GetProperty("rva").GetUInt32() == 0x391C0);
        Assert.True(blocked.GetProperty("category3Neighbors").GetInt32() > 0);
        Assert.True(blocked.GetProperty("category2Neighbors").GetInt32() > 0);
    }

    [Fact]
    public void NativeMoneyAndPendingCommandControlsPreserveTheSourceFacility()
    {
        using JsonDocument document = Vectors();
        JsonElement[] rows = Items(document.RootElement, "cases");
        JsonElement Find(string variant) => rows.Single(r => r.GetProperty("variant").GetString() == variant);
        Assert.Empty(Items(Find("actual"), "copiedInputChanges"));
        Assert.Equal(new[] { 6 }, Numbers(Find("actual").GetProperty("nativeKinds")));
        Assert.Equal(new[] { 6 }, Numbers(Find("kind10").GetProperty("nativeKinds")));
        Assert.Equal(new[] { 5 }, Numbers(Find("lowFunds").GetProperty("nativeKinds")));
        Assert.True(Find("lowFunds").GetProperty("cost5").GetInt32() < Find("lowFunds").GetProperty("cost6").GetInt32());
        Assert.Empty(Items(Find("mode1"), "frames"));
        foreach (string variant in new[] { "pending8Category2", "pending9Category2" })
        {
            JsonElement row = Find(variant);
            JsonElement frame = Items(row, "frames").Single(f => f.GetProperty("rva").GetUInt32() == 0x3903A);
            Assert.Equal(1, frame.GetProperty("pendingCategory2").GetInt32());
            Assert.DoesNotContain(Items(row, "events"), e => e.GetProperty("rva").GetUInt32() == 0x279190
                && e.GetProperty("return_address").GetUInt32() is 0x39274 or 0x392EE);
        }
        Assert.Equal(new[] { 6 }, Numbers(Find("pending8Category3").GetProperty("nativeKinds")));
        Assert.Equal(new[] { 6 }, Numbers(Find("pending9Category3").GetProperty("nativeKinds")));
    }
}
