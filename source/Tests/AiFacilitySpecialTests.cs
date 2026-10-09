using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiFacilitySpecialTests
{
    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "AiFacilitySpecialVectors.json")));

    private static JsonElement[] Items(JsonElement row, string name) => row.GetProperty(name).EnumerateArray().ToArray();
    private static int[] Numbers(JsonElement array) => array.EnumerateArray().Select(x => x.GetInt32()).ToArray();

    [Fact]
    public void StableNativeWorldMatchesTheOuterHomeModeAndGateChecks()
    {
        using JsonDocument document = Vectors();
        foreach (JsonElement run in document.RootElement.GetProperty("runs").EnumerateArray())
        {
            Assert.True(run.GetProperty("clockStable").GetBoolean());
            Assert.True(run.GetProperty("worldStable").GetBoolean());
            Assert.Empty(Items(run, "failures"));
            foreach (JsonElement row in Items(run, "cases"))
            {
                Assert.Equal(1, row.GetProperty("status").GetInt32());
                ulong area = row.GetProperty("area").GetUInt64(), gate = row.GetProperty("gate").GetUInt64();
                bool adjacent = Items(row, "events").Any(e => e.GetProperty("rva").GetUInt32() == 0x22F060
                    && e.GetProperty("list").EnumerateArray().Any(x => x.GetUInt64() == gate));
                bool expected = AiParameterRules.FacilityBorderMode(area == row.GetProperty("home").GetUInt64(),
                    area == gate, adjacent, row.GetProperty("outputMode").GetInt32());
                Assert.Equal(expected, Items(row, "frames").Any(f => f.GetProperty("rva").GetUInt32() == 0x38926));
            }
        }
    }

    [Fact]
    public void NativeFortSelectionMatchesTheObservedPlacementCalls()
    {
        using JsonDocument document = Vectors();
        int positive = 0, negative = 0;
        HashSet<int> selected = [];
        foreach (JsonElement run in document.RootElement.GetProperty("runs").EnumerateArray())
        foreach (JsonElement row in Items(run, "cases"))
        {
            JsonElement[] frames = Items(row, "frames");
            JsonElement? fort = frames.Where(f => f.GetProperty("rva").GetUInt32() == 0x38CA4)
                .Select(f => (JsonElement?)f).SingleOrDefault();
            bool allowed = Items(row, "events").Any(e => e.GetProperty("rva").GetUInt32() == 0x279190
                && e.GetProperty("return_address").GetUInt32() == 0x38E8A && e.GetProperty("result").GetUInt32() != 0);
            int? expected = fort is { } frame
                ? AiExecutorRules.SpecialFortFacilityKind(Numbers(frame.GetProperty("candidates")),
                    frame.GetProperty("pendingCategory3").GetInt32() != 0, allowed)
                : null;
            bool special = row.GetProperty("special").GetInt32() != 0;
            Assert.Equal(expected.HasValue, special);
            Assert.Equal(special, frames.Any(f => f.GetProperty("rva").GetUInt32() == 0x38FE9));
            if (expected is { } kind)
            {
                positive++;
                selected.Add(kind);
                Assert.Equal(new[] { kind }, Numbers(row.GetProperty("nativeKinds")));
                Assert.NotEmpty(Items(row, "copiedInputChanges"));
            }
            else negative++;
        }
        Assert.True(positive > 0 && negative > 0);
        Assert.Equal(new[] { 9, 10 }, selected.Order().ToArray());
    }

    [Fact]
    public void NativeCopiedInputsExerciseBudgetNeighborAndPendingCommandControls()
    {
        using JsonDocument document = Vectors();
        JsonElement detail = document.RootElement.GetProperty("runs").EnumerateArray().Single(r =>
            Items(r, "cases").Any(c => c.GetProperty("variant").GetString() == "copiedFortLowFunds"));
        JsonElement[] rows = Items(detail, "cases");
        JsonElement Find(string variant, int mode = 0) => rows.Single(c =>
            c.GetProperty("variant").GetString() == variant && c.GetProperty("inputMode").GetInt32() == mode);
        Assert.Equal(0, Find("actual").GetProperty("special").GetInt32());
        Assert.Equal(new[] { 10 }, Numbers(Find("copiedFort").GetProperty("nativeKinds")));
        Assert.Equal(new[] { 10 }, Numbers(Find("copiedFortKind6").GetProperty("nativeKinds")));
        Assert.Equal(0, Find("copiedFort", 1).GetProperty("special").GetInt32());
        Assert.Equal(new[] { 9 }, Numbers(Find("copiedFortLowFunds").GetProperty("nativeKinds")));
        JsonElement neighbor = Find("copiedFortWithCategory3Neighbor");
        Assert.Equal(0, neighbor.GetProperty("special").GetInt32());
        Assert.DoesNotContain(Items(neighbor, "events"), e => e.GetProperty("rva").GetUInt32() == 0x279190
            && e.GetProperty("return_address").GetUInt32() == 0x38E8A);
        foreach (string variant in new[] { "copiedFortWithPending8Category3", "copiedFortWithPending9Category3" })
        {
            JsonElement pending = Find(variant);
            JsonElement frame = Items(pending, "frames").Single(f => f.GetProperty("rva").GetUInt32() == 0x38CA4);
            Assert.Equal(1, frame.GetProperty("pendingCategory3").GetInt32());
            Assert.Equal(0, pending.GetProperty("special").GetInt32());
            Assert.Null(AiExecutorRules.SpecialFortFacilityKind(Numbers(frame.GetProperty("candidates")), true, true));
        }
        Assert.Equal(1, Find("copiedFortWithPending8Category2").GetProperty("special").GetInt32());
        Assert.Equal(1, Find("copiedFortWithPending9Category2").GetProperty("special").GetInt32());
    }
}
