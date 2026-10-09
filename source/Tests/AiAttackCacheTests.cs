using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiAttackCacheTests
{
    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "AiAttackCacheVectors.json")));
    private static JsonElement[] Items(JsonElement row, string name) => row.GetProperty(name).EnumerateArray().ToArray();

    [Fact]
    public void FullNativeAttackCallsMatchThePurposeDispatchAndObservedCacheAccesses()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("clockStable").GetBoolean());
        Assert.Empty(Items(root, "failures"));
        JsonElement[] rows = Items(root, "cases");
        Assert.Equal(20, rows.Length);
        Assert.Equal(new[] { 8, 9, 10, 11, 22 }, rows.Select(r => r.GetProperty("purpose").GetInt32()).Distinct().Order());
        foreach (JsonElement row in rows)
        {
            Assert.Contains(row.GetProperty("status").GetInt32(), new[] { 1, 2 });
            uint worker = AiExecutorRules.AttackDepartureWorker(row.GetProperty("purpose").GetInt32());
            JsonElement call = Items(row, "events").Single(e => e.GetProperty("rva").GetUInt32() == worker);
            Assert.Equal(1, call.GetProperty("result").GetInt32());
            Assert.Contains(Items(row, "events"), e => e.GetProperty("rva").GetUInt32() == 0x42E70);
            int input = row.GetProperty("input").GetInt32();
            Assert.Equal(input, row.GetProperty("cache").GetInt32());
            JsonElement[] accesses = Items(row, "cacheAccesses");
            Assert.Equal(2, accesses.Length);
            Assert.All(accesses, a =>
            {
                Assert.Equal("write", a.GetProperty("access").GetString());
                Assert.Equal(0x80, a.GetProperty("offset").GetInt32());
                Assert.Equal(4, a.GetProperty("size").GetInt32());
            });
            Assert.Equal(0, accesses[0].GetProperty("value").GetInt32());
            Assert.Equal(input, accesses[1].GetProperty("value").GetInt32());
        }
        foreach (var pair in rows.GroupBy(r => (r.GetProperty("force").GetInt32(), r.GetProperty("purpose").GetInt32())))
        {
            JsonElement zero = pair.Single(r => r.GetProperty("input").GetInt32() == 0);
            JsonElement one = pair.Single(r => r.GetProperty("input").GetInt32() == 1);
            Assert.Equal(zero.GetProperty("status").GetInt32(), one.GetProperty("status").GetInt32());
            Assert.Equal(Items(zero, "groups").Select(g => g.GetUInt64()), Items(one, "groups").Select(g => g.GetUInt64()));
        }
    }

    [Fact]
    public void InstructionLimitFailuresRemainSeparateFromCompletedNativeRows()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        JsonElement[] earlier = Items(root, "priorFailures");
        Assert.Equal(2, earlier.Length);
        foreach (JsonElement failure in earlier)
        {
            Assert.False(failure.TryGetProperty("status", out _));
            Assert.Contains("instruction limit", failure.GetProperty("error").GetString());
            Assert.Contains(Items(root, "cases"), row => row.GetProperty("force").GetInt32() == failure.GetProperty("force").GetInt32()
                && row.GetProperty("purpose").GetInt32() == failure.GetProperty("purpose").GetInt32()
                && row.GetProperty("input").GetInt32() == failure.GetProperty("input").GetInt32());
        }
    }
}
