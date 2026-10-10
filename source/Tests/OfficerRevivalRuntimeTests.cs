using System.IO.Compression;
using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class OfficerRevivalRuntimeTests
{
    private static JsonDocument Fixture(string name)
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Vectors", name));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip);
    }

    [Theory]
    [InlineData("officer_revival_native.json.gz")]
    [InlineData("officer_appearance_native.json.gz")]
    public void CurrentPreflightGeneratesExactlyTheFramesExecutedAgainstNativeMemory(string name)
    {
        using var fixture = Fixture(name);
        var input = fixture.RootElement.GetProperty("input");
        var classes = input.GetProperty("classes");
        ulong C(string name) => classes.GetProperty(name).GetUInt64();
        var types = TestGame.Classes with { Officer = C("Officer"), City = C("City"), Gate = C("Gate"), Corps = C("Corps"), Force = C("Force") };
        ulong exe = input.GetProperty("exe").GetUInt64(), raw = input.GetProperty("raw").GetUInt64(), state = input.GetProperty("state").GetUInt64();
        var identity = new GameIdentity(0, DateTime.UnixEpoch, exe, "captured-verified-code", true, types);
        var tables = new GameTables(0, 0, 0, raw + 0xE270, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var layout = new GameLayout(tables, 0, 0, 0, 0, 0, 0, 0, 0, [], [], 0, 0, state);
        byte[] Bytes(JsonElement r) => Convert.FromHexString(r.GetProperty("hex").GetString()!);
        var cities = input.GetProperty("cities").EnumerateArray().Select(r => new CityRecord(r.GetProperty("id").GetInt32(), r.GetProperty("address").GetUInt64(), Bytes(r))).ToArray();
        var officers = input.GetProperty("officers").EnumerateArray().Select(r => new OfficerRecord(r.GetProperty("slot").GetInt32(), r.GetProperty("address").GetUInt64(), Bytes(r))).ToArray();
        var forces = input.GetProperty("forces").EnumerateArray().Select(r => new ForceRecord(r.GetProperty("id").GetInt32(), r.GetProperty("address").GetUInt64(), Bytes(r))).ToArray();
        var gates = input.GetProperty("gates").EnumerateArray().Select(r => new GateRecord(r.GetProperty("id").GetInt32(), r.GetProperty("address").GetUInt64(), Bytes(r), null)).ToArray();
        var date = input.GetProperty("date");
        var data = new GameSnapshot
        {
            Cities = cities, Counties = [], Officers = officers, OfficerById = officers.ToDictionary(o => o.Id),
            CastleByCity = [], TileOwners = [], TileCounties = [], Units = [], Gates = gates,
            CorpsForce = Convert.FromHexString(input.GetProperty("corpsForce").GetString()!),
            Forces = forces.ToDictionary(f => f.Id, f => new ForceInfo(f.Id, f.Ruler, 0, "")), ForceRecords = forces.ToDictionary(f => f.Id),
            Date = (date.GetProperty("year").GetInt32(), date.GetProperty("month").GetInt32(), date.GetProperty("day").GetInt32())
        };
        var memory = new CapturedMemory(input.GetProperty("reads"));
        Assert.Equal(12, input.GetProperty("rows").GetArrayLength());
        if (name == "officer_appearance_native.json.gz")
        {
            Assert.All(input.GetProperty("rows").EnumerateArray(), row =>
                Assert.Equal(7, data.OfficerById[row.GetProperty("id").GetInt32()].Status));
            Assert.Contains(input.GetProperty("rows").EnumerateArray(), row =>
                row.GetProperty("birthYear").GetInt32() > date.GetProperty("year").GetInt32());
        }
        foreach (var row in input.GetProperty("rows").EnumerateArray())
        {
            var request = new OfficerRevival.Request(row.GetProperty("id").GetInt32(), (RevivalMode)row.GetProperty("mode").GetInt32(),
                row.GetProperty("home").GetInt32(), row.GetProperty("year").GetInt32());
            var prepared = GameMemory.PrepareRevival(memory, identity, layout, data, request);
            Assert.Equal(row.GetProperty("body").GetString(), Convert.ToHexString(prepared.Body));
            Assert.Contains(prepared.Guards, g => g.Nonzero && g.Address == exe + 0x27D1100);
        }
    }

    [Theory]
    [InlineData("officer_revival_native.json.gz", 9)]
    [InlineData("officer_appearance_native.json.gz", 7)]
    public void NativeRevivalRegistersExactlyOnceAndStaleFramesWriteNothing(string name, int originalStatus)
    {
        using var fixture = Fixture(name);
        var native = fixture.RootElement.GetProperty("native");
        Assert.True(native.GetProperty("verifiedCode").GetBoolean());
        Assert.True(native.GetProperty("clockStable").GetBoolean());
        Assert.True(native.GetProperty("officerRecordsStable").GetBoolean());
        Assert.False(native.GetProperty("gameWrites").GetBoolean());
        Assert.Empty(native.GetProperty("helperReplacements").EnumerateArray());
        int accepted = 0;
        foreach (var row in native.GetProperty("cases").EnumerateArray())
        {
            if (row.TryGetProperty("stale", out _))
            {
                Assert.Equal(0, row.GetProperty("result").GetInt32());
                Assert.True(row.GetProperty("unchanged").GetBoolean());
                continue;
            }
            ++accepted;
            Assert.Equal(1, row.GetProperty("result").GetInt32());
            Assert.Equal(row.GetProperty("mode").GetInt32() == 0 ? 4 : 5, row.GetProperty("status").GetInt32());
            Assert.Equal(row.GetProperty("home").GetInt32(), row.GetProperty("place").GetInt32());
            Assert.Equal(1, row.GetProperty("membership").GetInt32());
            Assert.Equal(0, row.GetProperty("duplicateResult").GetInt32());
            Assert.Equal(0, row.GetProperty("repeatedFrameResult").GetInt32());
            Assert.True(row.GetProperty("preservedOtherBytes").GetBoolean());
            Assert.True(row.GetProperty("poolValid").GetBoolean());
            Assert.Equal(row.GetProperty("mode").GetInt32() == 0 ? 1 : 0, row.GetProperty("nativeServingCountDelta").GetInt32());
            Assert.True(row.GetProperty("nativeForceConfirmed").GetBoolean());
            if (originalStatus == 7)
            {
                Assert.Equal(7, row.GetProperty("originalStatus").GetInt32());
                Assert.True(row.GetProperty("preservedBirthAndDebut").GetBoolean());
                var input = fixture.RootElement.GetProperty("input").GetProperty("rows").EnumerateArray()
                    .Single(r => r.GetProperty("id").GetInt32() == row.GetProperty("id").GetInt32() &&
                        r.GetProperty("mode").GetInt32() == row.GetProperty("mode").GetInt32() &&
                        r.GetProperty("home").GetInt32() == row.GetProperty("home").GetInt32());
                Assert.Equal(input.GetProperty("birthYear").GetInt32(), row.GetProperty("birthYear").GetInt32());
                Assert.Equal(input.GetProperty("debutYear").GetInt32(), row.GetProperty("debutYear").GetInt32());
            }
        }
        Assert.Equal(12, accepted);
    }

    private sealed class CapturedMemory(JsonElement reads) : IRecordMemory
    {
        private readonly (ulong Address, byte[] Bytes)[] segments = reads.EnumerateArray()
            .Select(r => (r.GetProperty("address").GetUInt64(), Convert.FromHexString(r.GetProperty("hex").GetString()!))).ToArray();
        public byte[] Read(ulong address, int length)
        {
            foreach (var segment in segments)
                if (segment.Address <= address && address + (ulong)length <= segment.Address + (ulong)segment.Bytes.Length)
                    return segment.Bytes.AsSpan((int)(address - segment.Address), length).ToArray();
            throw new InvalidOperationException($"Captured input missing: {address:X}/{length}");
        }
        public void Write(ulong address, byte[] bytes) => throw new InvalidOperationException("Read-only preflight must not write");
    }
}
