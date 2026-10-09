using System.Buffers.Binary;
using System.Text.Json;

namespace San14Tenkazu.Tests;

public class GateOwnershipTests
{
    private const int 이각군 = 6, 설지군 = 45, 동관 = 54;

    // Native CGateData::GetForce (0x22CD80), run on read-only copies of all ten live gate records.
    // Includes 동관: corps force 6, actual force 45; 호관: corps force 21, actual force 4.
    [Fact]
    public void EveryRecordedGateMatchesTheGamesNativeOwnerGetter()
    {
        using JsonDocument vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "GateOwnershipVectors.json")));
        Assert.True(vectors.RootElement.GetProperty("clock_unchanged").GetBoolean());
        JsonElement[] gates = vectors.RootElement.GetProperty("gates").EnumerateArray().ToArray();
        Assert.Equal(10, gates.Length);
        foreach (JsonElement vector in gates)
        {
            int id = vector.GetProperty("id").GetInt32(), tile = vector.GetProperty("tile").GetInt32();
            GameSnapshot made = new TestGame().Build();
            byte[] corpsForce = made.CorpsForce;
            corpsForce[vector.GetProperty("corps").GetInt32()] = (byte)vector.GetProperty("corps_force").GetInt32();
            byte[] owners = new byte[GameMemory.TileCount];
            owners[tile] = (byte)vector.GetProperty("tile_force").GetInt32();
            owners[vector.GetProperty("seat").GetInt32()] = (byte)vector.GetProperty("seat_force").GetInt32();
            ushort[] counties = new ushort[GameMemory.TileCount];
            int county = vector.GetProperty("county").GetInt32();
            counties[tile] = (ushort)county;
            var building = new BuildingRecord(id, 0x2000, Bytes("building_bytes"));
            var gate = new GateRecord(id, 0x1000, Bytes("gate_bytes"), building);
            var data = new GameSnapshot { Cities = made.Cities, Counties = [new CountyRecord(county, 0x3000, Bytes("county_bytes"))],
                Officers = made.Officers, OfficerById = made.OfficerById, CastleByCity = made.CastleByCity,
                Forces = made.Forces, CorpsForce = corpsForce, Units = made.Units, TileOwners = owners, TileCounties = counties, Gates = [gate] };

            Assert.Equal(vector.GetProperty("native_force").GetInt32(), data.ForceOfGate(gate));
            byte[] Bytes(string key) => Convert.FromHexString(vector.GetProperty(key).GetString()!);
        }
    }

    private static GameSnapshot CapturedGate()
    {
        GameSnapshot data = new TestGame().Force(이각군, 893, 6).Force(설지군, 3001, 21)
            .Officer(893, "이각", 6, 20, 1).Officer(3001, "설지", 21, 18, 1)
            .Gate(동관, 6).Build();
        data.TileOwners[data.Counties.Single().CenterTile] = 설지군;
        return data;
    }

    [Fact]
    public void CapturedGateBelongsToTheCountySeatOwnerEvenWhenCorpsBuildingAndGateLandDisagree()
    {
        GameSnapshot data = CapturedGate();
        GateRecord gate = data.Gates.Single();

        Assert.Equal(이각군, data.ForceOfCorps(gate.Corps));
        Assert.Equal(이각군, gate.Building!.Data[GameMemory.BuildingForceOffset]);
        Assert.Equal(이각군, data.TileOwners[gate.Tile]);
        Assert.Equal(설지군, data.ForceOfGate(gate));
        Assert.Equal([동관], data.GatesOf(설지군).Select(g => g.Id));
        Assert.Empty(data.GatesOf(이각군));
        Assert.Equal(L.Give.HasForce(CityNames.BaseName(동관), data.ForceName(설지군)), BaseGrants.Refusal(data, 동관, 이각군));
    }

    [Fact]
    public void ACountySeatWithoutAnOwnerLeavesTheGateNeutralDespiteItsOldCorps()
    {
        GameSnapshot data = CapturedGate();
        data.TileOwners[data.Counties.Single().CenterTile] = 0;

        Assert.Equal(0, data.ForceOfGate(data.Gates.Single()));
        Assert.Null(BaseGrants.Refusal(data, 동관, 설지군));
    }

    [Fact]
    public void AGateWithoutItsBuildingOrCountyDoesNotUseItsOldCorpsAsOwner()
    {
        GameSnapshot data = CapturedGate();
        GateRecord gate = data.Gates.Single();

        Assert.Equal(0, data.ForceOfGate(gate with { Building = null }));
        data.TileCounties[gate.Tile] = 0;
        Assert.Equal(0, data.ForceOfGate(gate));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(52)]
    public void AnInvalidCountyHasNoOwner(int homeCity)
    {
        GameSnapshot data = CapturedGate();
        data.Counties.Single().Data[CountyFields.HomeCityOffset] = (byte)homeCity;

        Assert.Equal(0, data.ForceOfGate(data.Gates.Single()));
    }

    [Fact]
    public void AnOutOfRangeCountyCentreUsesTileZeroAsTheNativeLookupDoes()
    {
        GameSnapshot data = CapturedGate();
        BinaryPrimitives.WriteUInt16LittleEndian(data.Counties.Single().Data.AsSpan(CountyFields.CenterTileOffset), ushort.MaxValue);
        data.TileOwners[0] = 설지군;

        Assert.Equal(설지군, data.ForceOfGate(data.Gates.Single()));
    }
}
