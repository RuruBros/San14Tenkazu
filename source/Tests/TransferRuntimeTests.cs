using System.IO.Compression;
using System.Text.Json;

namespace San14Tenkazu.Tests;

// Native functions executed in copied pages. The fixture retains all route-table inputs and exact
// C# transaction bytes; no live game write or native-result substitution generated these answers.
public sealed class TransferRuntimeTests
{
    private static JsonDocument Fixture()
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Vectors", "officer_transfer_native.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip);
    }

    [Fact]
    public void TransportAndArmyReachabilityMatchesOriginalSendHome()
    {
        using JsonDocument fixture = Fixture();
        JsonElement root = fixture.RootElement, input = root.GetProperty("input"), native = root.GetProperty("routes");
        Assert.True(native.GetProperty("verifiedCode").GetBoolean());
        Assert.True(native.GetProperty("clockStable").GetBoolean());
        Assert.False(native.GetProperty("gameWrites").GetBoolean());
        var memory = new CapturedMemory(input.GetProperty("reads"));
        var world = TransferRouting.Read(memory, input.GetProperty("raw").GetUInt64());
        Assert.Equal(12, native.GetProperty("routeCases").GetArrayLength());
        foreach (JsonElement row in native.GetProperty("routeCases").EnumerateArray())
        {
            Assert.False(row.TryGetProperty("error", out _));
            JsonElement source = input.GetProperty("routes").EnumerateArray().Single(r => r.GetProperty("slot").GetInt32() == row.GetProperty("slot").GetInt32() && r.GetProperty("home").GetInt32() == row.GetProperty("home").GetInt32());
            var unit = new UnitRecord(source.GetProperty("slot").GetInt32(), source.GetProperty("address").GetUInt64(), Convert.FromHexString(source.GetProperty("unit").GetString()!), 0);
            bool actual = TransferRouting.Find(world, unit, source.GetProperty("force").GetInt32(), source.GetProperty("home").GetInt32()).Count > 0;
            Assert.Equal(row.GetProperty("actual").GetBoolean(), actual);
        }
    }

    [Fact]
    public void GeneratedGuardedTransactionMatchesNativeExecutedBytes()
    {
        using JsonDocument fixture = Fixture();
        JsonElement root = fixture.RootElement;
        foreach (JsonElement row in root.GetProperty("input").GetProperty("rows").EnumerateArray().Where(r => r.TryGetProperty("body", out _)))
        {
            var guards = row.GetProperty("guards").EnumerateArray().Select(g => new TransferGuard(g.GetProperty("address").GetUInt64(), Convert.FromHexString(g.GetProperty("hex").GetString()!))).ToArray();
            TransferCall Call(JsonElement c, bool check) => new(new(c.GetProperty("function").GetUInt64(), c.GetProperty("rcx").GetUInt64(), c.GetProperty("rdx").GetUInt64(), c.GetProperty("more").ValueKind == JsonValueKind.Null ? [] : c.GetProperty("more").EnumerateArray().Select(a => a.GetUInt64()).ToArray()), check, c.GetProperty("corpsAt").GetUInt64());
            var checks = row.GetProperty("checks").EnumerateArray().Select(c => (TransferStep)Call(c, true)).ToArray();
            var changes = row.GetProperty("changes").EnumerateArray().Select(c => c.TryGetProperty("hex", out JsonElement value) ? (TransferStep)new TransferStore(c.GetProperty("address").GetUInt64(), Convert.FromHexString(value.GetString()!)) : Call(c, false)).ToArray();
            Assert.Equal(row.GetProperty("body").GetString(), Convert.ToHexString(TransferFrame.Build(guards, checks, changes)));
        }
        JsonElement native = root.GetProperty("frames");
        Assert.False(native.GetProperty("gameWrites").GetBoolean());
        Assert.Empty(native.GetProperty("helperReplacements").EnumerateArray());
        foreach (JsonElement row in native.GetProperty("frameCases").EnumerateArray())
        {
            Assert.False(row.TryGetProperty("error", out _));
            if (row.TryGetProperty("stale", out _))
            {
                Assert.Equal(0, row.GetProperty("result").GetInt32());
                Assert.True(row.GetProperty("unchanged").GetBoolean());
                continue;
            }
            Assert.Equal(1, row.GetProperty("result").GetInt32());
            Assert.Equal(4, row.GetProperty("status").GetInt32());
            Assert.Equal(row.GetProperty("home").GetInt32(), row.GetProperty("base").GetInt32());
            Assert.NotEqual(row.GetProperty("id").GetInt32(), row.GetProperty("oldLeader").GetInt32());
            Assert.All(row.GetProperty("units").EnumerateArray(), u => Assert.Equal(0, u.GetProperty("group").GetInt32()));
        }
    }

    [Fact]
    public void OriginalMovementAdvancesAndArrivalDepositsTransportTroops()
    {
        using JsonDocument fixture = Fixture();
        JsonElement[] rows = fixture.RootElement.GetProperty("frames").GetProperty("frameCases").EnumerateArray().Where(r => !r.TryGetProperty("stale", out _)).ToArray();
        Assert.Contains(rows, r => r.GetProperty("groupCommanders").EnumerateArray().Any(c => c.GetInt32() == 2071));
        foreach (int kind in new[] { UnitFields.ArmyKind, UnitFields.TransportKind })
        {
            JsonElement row = rows.First(r => r.GetProperty("inputChanges").GetArrayLength() == 0 && r.GetProperty("units").EnumerateArray().Any(u => u.GetProperty("kind").GetInt32() == kind));
            JsonElement motion = Assert.Single(row.GetProperty("movement").EnumerateArray());
            Assert.True(motion.GetProperty("beforeTile").GetInt32() != motion.GetProperty("afterTile").GetInt32() || motion.GetProperty("afterProgress").GetInt32() > motion.GetProperty("beforeProgress").GetInt32());
        }
        JsonElement arrival = Assert.Single(rows, r => r.GetProperty("baseTroopDelta").GetInt32() > 0);
        Assert.Equal(1000, arrival.GetProperty("baseTroopDelta").GetInt32());
        Assert.Equal(1, Assert.Single(arrival.GetProperty("pending").EnumerateArray()).GetInt32());
        Assert.Equal(arrival.GetProperty("home").GetInt32(), arrival.GetProperty("place").GetInt32());
    }

    private sealed class CapturedMemory(JsonElement reads) : IRecordMemory
    {
        private readonly (ulong Address, byte[] Bytes)[] segments = reads.EnumerateArray().Select(r => (r.GetProperty("address").GetUInt64(), Convert.FromHexString(r.GetProperty("hex").GetString()!))).ToArray();
        public byte[] Read(ulong address, int length)
        {
            foreach (var s in segments)
                if (s.Address <= address && address + (ulong)length <= s.Address + (ulong)s.Bytes.Length)
                    return s.Bytes.AsSpan((int)(address - s.Address), length).ToArray();
            throw new InvalidDataException($"Uncaptured read {address:X}, {length}");
        }
        public void Write(ulong address, byte[] bytes) => throw new InvalidOperationException("Read-only preflight must not write.");
    }
}
