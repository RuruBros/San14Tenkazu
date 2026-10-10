using System.IO.Compression;
using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class RulerTransferRuntimeTests
{
    [Fact]
    public void OriginalNativeMovePreservesTheRulerAndClaimsTheDestinationForHisMainCorps()
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Vectors", "ruler_transfer_native.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var fixture = JsonDocument.Parse(gzip);
        JsonElement root = fixture.RootElement;
        Assert.False(root.GetProperty("gameWrites").GetBoolean());
        Assert.True(root.GetProperty("clockStable").GetBoolean());
        Assert.True(root.GetProperty("verifiedCode").GetBoolean());
        Assert.Empty(root.GetProperty("helperReplacements").EnumerateArray());
        JsonElement[] cases = root.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Contains(cases, r => r.GetProperty("player").GetBoolean());
        Assert.Contains(cases, r => !r.GetProperty("player").GetBoolean());
        Assert.Contains(cases, r => r.GetProperty("destinationCorps").GetInt32() != r.GetProperty("oldCorps").GetInt32());
        Assert.Contains(cases, r => r.GetProperty("units").GetArrayLength() > 0);
        foreach (JsonElement row in cases)
        {
            Assert.Equal(1, row.GetProperty("result").GetInt32());
            Assert.Equal(1, row.GetProperty("status").GetInt32());
            Assert.Equal(row.GetProperty("id").GetInt32(), row.GetProperty("ruler").GetInt32());
            Assert.Equal(row.GetProperty("id").GetInt32(), row.GetProperty("leader").GetInt32());
            Assert.Equal(row.GetProperty("oldCorps").GetInt32(), row.GetProperty("corps").GetInt32());
            Assert.Equal(row.GetProperty("oldCorps").GetInt32(), row.GetProperty("cityCorps").GetInt32());
            Assert.Equal(row.GetProperty("home").GetInt32(), row.GetProperty("base").GetInt32());
            Assert.Equal(row.GetProperty("oldTravelFlags").GetInt32(), row.GetProperty("travelFlags").GetInt32());
            foreach (JsonElement unit in row.GetProperty("units").EnumerateArray())
            {
                Assert.Equal(61 + unit.GetProperty("slot").GetInt32(), row.GetProperty("place").GetInt32());
                Assert.Equal(11, unit.GetProperty("order").GetInt32());
                Assert.InRange(unit.GetProperty("next").GetInt32(), 0, GameMemory.TileCount - 1);
                Assert.Equal(0, unit.GetProperty("group").GetInt32());
                Assert.Equal(0, unit.GetProperty("flags").GetInt32() & 2);
            }
        }
        Assert.Equal(cases.Length * 3, root.GetProperty("stale").GetArrayLength());
        foreach (JsonElement row in root.GetProperty("stale").EnumerateArray())
        {
            Assert.Equal(0, row.GetProperty("result").GetInt32());
            Assert.True(row.GetProperty("unchanged").GetBoolean());
        }
        foreach (JsonElement row in root.GetProperty("input").GetProperty("rows").EnumerateArray().Where(r => r.TryGetProperty("body", out _)))
        {
            var guards = row.GetProperty("guards").EnumerateArray().Select(g =>
                new TransferGuard(g.GetProperty("address").GetUInt64(), Convert.FromHexString(g.GetProperty("hex").GetString()!))).ToArray();
            TransferCall Call(JsonElement c, bool check) => new(new(c.GetProperty("function").GetUInt64(),
                c.GetProperty("rcx").GetUInt64(), c.GetProperty("rdx").GetUInt64(),
                c.GetProperty("more").ValueKind == JsonValueKind.Null ? [] : c.GetProperty("more").EnumerateArray().Select(a => a.GetUInt64()).ToArray()), check,
                c.GetProperty("corpsAt").GetUInt64(), c.TryGetProperty("rdxFromReturn", out var returned) && returned.GetBoolean());
            var checks = row.GetProperty("checks").EnumerateArray().Select(c => (TransferStep)Call(c, true)).ToArray();
            var changes = row.GetProperty("changes").EnumerateArray().Select(c => c.TryGetProperty("hex", out JsonElement value)
                ? (TransferStep)new TransferStore(c.GetProperty("address").GetUInt64(), Convert.FromHexString(value.GetString()!))
                : Call(c, false)).ToArray();
            Assert.Equal(row.GetProperty("body").GetString(), Convert.ToHexString(TransferFrame.Build(guards, checks, changes)));
        }
    }
}
