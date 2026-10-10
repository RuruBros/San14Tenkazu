using System.IO.Compression;
using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class OfficerCaptivityRuntimeTests
{
    private static JsonDocument Fixture()
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Vectors", "officer_captivity_native.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip);
    }

    [Fact]
    public void CurrentPreflightMatchesExactlyTheSixArgumentFramesExecutedAgainstNativeMemory()
    {
        using var fixture = Fixture();
        var input = fixture.RootElement.GetProperty("input");
        ulong C(string name) => input.GetProperty("classes").GetProperty(name).GetUInt64();
        var types = TestGame.Classes with { Officer = C("Officer"), City = C("City"), Gate = C("Gate"), Corps = C("Corps"), Force = C("Force") };
        ulong exe = input.GetProperty("exe").GetUInt64(), raw = input.GetProperty("raw").GetUInt64(), state = input.GetProperty("state").GetUInt64();
        var identity = new GameIdentity(0, DateTime.UnixEpoch, exe, "captured-verified-code", true, types);
        var tables = new GameTables(0, 0, 0, raw + 0xE270, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var layout = new GameLayout(tables, 0, 0, 0, 0, 0, 0, 0, 0, [], [], 0, 0, state);
        byte[] Bytes(JsonElement r) => Convert.FromHexString(r.GetProperty("hex").GetString()!);
        var cities = input.GetProperty("cities").EnumerateArray().Select(r => new CityRecord(r.GetProperty("id").GetInt32(), r.GetProperty("address").GetUInt64(), Bytes(r))).ToArray();
        var officers = input.GetProperty("officers").EnumerateArray().Select(r => new OfficerRecord(r.GetProperty("slot").GetInt32(), r.GetProperty("address").GetUInt64(), Bytes(r))).ToArray();
        var forces = input.GetProperty("forces").EnumerateArray().Select(r => new ForceRecord(r.GetProperty("id").GetInt32(), r.GetProperty("address").GetUInt64(), Bytes(r))).ToArray();
        var gates = input.GetProperty("gates").EnumerateArray().Select(r => new GateRecord(r.GetProperty("id").GetInt32(), r.GetProperty("address").GetUInt64(), Bytes(r),
            r.GetProperty("building") is { ValueKind: not JsonValueKind.Null } b ? new(b.GetProperty("id").GetInt32(), b.GetProperty("address").GetUInt64(), Bytes(b)) : null)).ToArray();
        var date = input.GetProperty("date");
        var data = new GameSnapshot
        {
            Cities = cities, Counties = input.GetProperty("counties").EnumerateArray().Select(r => new CountyRecord(r.GetProperty("id").GetInt32(), r.GetProperty("address").GetUInt64(), Bytes(r))).ToArray(),
            Officers = officers, OfficerById = officers.ToDictionary(o => o.Id), CastleByCity = [], Units = [], Gates = gates,
            TileOwners = Convert.FromHexString(input.GetProperty("tileOwners").GetString()!),
            TileCounties = input.GetProperty("tileCounties").EnumerateArray().Select(r => r.GetUInt16()).ToArray(),
            CorpsForce = Convert.FromHexString(input.GetProperty("corpsForce").GetString()!),
            Forces = forces.ToDictionary(f => f.Id, f => new ForceInfo(f.Id, f.Ruler, 0, "")), ForceRecords = forces.ToDictionary(f => f.Id),
            Date = (date.GetProperty("year").GetInt32(), date.GetProperty("month").GetInt32(), date.GetProperty("day").GetInt32())
        };
        var memory = new CapturedMemory(input.GetProperty("reads"));
        Assert.Equal(9, input.GetProperty("rows").GetArrayLength());
        foreach (var row in input.GetProperty("rows").EnumerateArray())
        {
            var prepared = GameMemory.PrepareCaptivity(memory, identity, layout, data, new(row.GetProperty("id").GetInt32(), row.GetProperty("captor").GetInt32()));
            Assert.Equal(row.GetProperty("body").GetString(), Convert.ToHexString(prepared.Body));
            Assert.Equal(row.GetProperty("place").GetInt32(), prepared.Place);
            var call = Assert.IsType<TransferCall>(Assert.Single(prepared.Changes));
            Assert.Equal(4, call.Invocation.More!.Length);
            Assert.Equal(prepared.Target.Force, row.GetProperty("captor").GetInt32());
            Assert.Empty(prepared.Plan.Edits);
        }
    }

    [Fact]
    public void NativeCaptureWritesMatchForPlayerAndAiOfficersAndStaleFramesWriteNothing()
    {
        using var fixture = Fixture();
        var native = fixture.RootElement.GetProperty("native");
        Assert.True(native.GetProperty("verifiedCode").GetBoolean());
        Assert.True(native.GetProperty("clockStable").GetBoolean());
        Assert.True(native.GetProperty("officerRecordsStable").GetBoolean());
        Assert.False(native.GetProperty("gameWrites").GetBoolean());
        Assert.Empty(native.GetProperty("helperReplacements").EnumerateArray());
        var rows = native.GetProperty("cases").EnumerateArray().ToArray();
        int player = fixture.RootElement.GetProperty("input").GetProperty("PlayerForce").GetInt32();
        var accepted = rows.Where(r => !r.TryGetProperty("stale", out _)).ToArray();
        Assert.Equal(9, accepted.Length);
        Assert.Contains(accepted, r => r.GetProperty("originalForce").GetInt32() == player && r.GetProperty("captor").GetInt32() == player);
        Assert.Contains(accepted, r => r.GetProperty("originalForce").GetInt32() != player &&
            r.GetProperty("originalForce").GetInt32() == r.GetProperty("captor").GetInt32());
        Assert.Contains(accepted, r => r.GetProperty("originalForce").GetInt32() != r.GetProperty("captor").GetInt32());
        Assert.Contains(accepted, r => r.GetProperty("originalForce").GetInt32() == player);
        Assert.Contains(accepted, r => r.GetProperty("originalForce").GetInt32() != player);
        Assert.All(accepted, r =>
        {
            Assert.Equal(1, r.GetProperty("result").GetInt32()); Assert.Equal(1, r.GetProperty("nativeResult").GetInt32());
            Assert.Equal(6, r.GetProperty("status").GetInt32()); Assert.Equal(1, r.GetProperty("nativeCalls").GetInt32());
            Assert.True(r.GetProperty("exactNativeWrites").GetBoolean()); Assert.True(r.GetProperty("changedPages").GetInt32() > 0);
        });
        var rejected = rows.Where(r => r.TryGetProperty("stale", out _)).ToArray();
        Assert.Equal(36, rejected.Length);
        Assert.All(rejected, r =>
        {
            Assert.Equal(0, r.GetProperty("result").GetInt32()); Assert.Equal(0, r.GetProperty("nativeCalls").GetInt32());
            Assert.True(r.GetProperty("unchanged").GetBoolean());
        });
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
