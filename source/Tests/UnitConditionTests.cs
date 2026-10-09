using System.Buffers.Binary;

namespace San14Tenkazu.Tests;

public class UnitConditionTests
{
    private static GameSnapshot Game(int officer = 3002) => new TestGame()
        .Officer(officer, "Officer", 1, 1, 4).Leading(officer)
        .Personality(77, "Detox", "", (52, 0))
        .Personality(76, "Tribe", "", (93, 0))
        .Personality(53, "Fire", "", (25, 0))
        .Personality(258, "Custom", "", (52, 0)).Build();

    private static UnitRecord On(GameSnapshot data, int kind, int fire = 0) => data.Units.Single() with
        { Terrain = new(data.Units.Single().Tile, kind, fire) };

    private static void Trait(GameSnapshot data, int id, int slot = 0)
    {
        OfficerRecord officer = data.Officers.Single();
        OfficerFields.Personalities[slot].Encode(id).CopyTo(officer.Data, OfficerFields.Personalities[slot].Offset);
        data.Personalities.Single(p => p.Index == id).Data[0xBF] = 16;
    }

    [Fact]
    public void RecordedPoisonLossHasNoEditableAilmentAndSupplyRemainsConnected()
    {
        GameSnapshot data = Game();
        UnitRecord unit = On(data, 7);
        unit.Data[0x26] = 74; unit.Data[0x21] = 150; unit.Data[0x22] = 200;
        Trait(data, 76);

        IReadOnlyList<UnitCondition> conditions = UnitConditions.Read(data, unit);
        Assert.Equal(L.Unit.PoisonDamage, conditions[0].Value);
        Assert.True(conditions[0].Warning);
        Assert.Equal(L.Terms.None, conditions[1].Value);
        Assert.Equal(L.Unit.SupplyConnected, conditions[2].Value);
        Assert.All(UnitFields.Ailments, a => Assert.Equal(0, unit[a]));
    }

    [Fact]
    public void DetoxNeedsItsActualIdInAnEnabledFirstFiveSlot()
    {
        GameSnapshot data = Game();
        UnitRecord unit = On(data, 7);
        Trait(data, 258);
        Assert.Equal(L.Unit.PoisonDamage, UnitConditions.Read(data, unit)[0].Value);
        Trait(data, 77, 5);
        Assert.Equal(L.Unit.PoisonDamage, UnitConditions.Read(data, unit)[0].Value);
        Trait(data, 77, 4);
        Assert.Equal(L.Unit.PoisonImmune, UnitConditions.Read(data, unit)[0].Value);
        data.Personalities.Single(p => p.Index == 77).Data[0xBF] = 0;
        Assert.Equal(L.Unit.PoisonDamage, UnitConditions.Read(data, unit)[0].Value);
    }

    [Fact]
    public void RegisteredOfficerBypassesFlagButStillRequiresDetoxId()
    {
        GameSnapshot data = Game(5101);
        Trait(data, 77);
        data.Personalities.Single(p => p.Index == 77).Data[0xBF] = 0;
        Assert.Equal(L.Unit.PoisonImmune, UnitConditions.Read(data, On(data, 7))[0].Value);
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(7, true)]
    [InlineData(8, false)]
    public void EncodedSlotFlagOverridesPlainFlagWithNativeLengthRounding(int length, bool usesVector)
    {
        PersonalityRecord p = Game().Personalities.Single(p => p.Index == 77);
        BinaryPrimitives.WriteUInt64LittleEndian(p.Data.AsSpan(0xC8), 0x1000);
        BinaryPrimitives.WriteUInt64LittleEndian(p.Data.AsSpan(0xD0), (ulong)(0x1000 + length));
        p.Data[0xBF] = 0;
        ushort xored = 16 ^ 77;
        byte[] encoded = BitConverter.GetBytes((ushort)((xored >> 1) | (xored << 15)));
        Assert.Equal(usesVector, UnitConditions.ReadSlotEnabled(p, (_, _) => encoded));
        if (usesVector) Assert.Null(UnitConditions.ReadSlotEnabled(p, (_, _) => null));
    }

    [Fact]
    public void TerrainAndFireRefreshWithoutStaleOrUnknownDataClaimingSafety()
    {
        GameSnapshot data = Game();
        UnitRecord poison = On(data, 7);
        Assert.True(UnitConditions.Read(data, poison)[0].Warning);
        Assert.Equal(L.Terms.None, UnitConditions.Read(data, On(data, 2))[0].Value);
        Assert.Equal(L.Unit.FireDamage, UnitConditions.Read(data, On(data, 2, 100))[1].Value);
        Trait(data, 53);
        Assert.Equal(L.Unit.FireImmune, UnitConditions.Read(data, On(data, 2, 100))[1].Value);
        Assert.Equal(L.Terms.None, UnitConditions.Read(data, On(data, 2))[1].Value);
        UnitRecord stale = poison with { Terrain = new(poison.Tile + 1, 7, 0) };
        Assert.Equal(L.Unit.ConditionUnknown, UnitConditions.Read(data, stale)[0].Value);
        Assert.Equal(L.Unit.ConditionUnknown, UnitConditions.Read(data, data.Units.Single())[1].Value);
    }

    [Fact]
    public void MissingEncodedImmunityIsUnknownAndNeverClaimedImmune()
    {
        GameSnapshot data = Game(); Trait(data, 77);
        PersonalityRecord p = data.Personalities.Single(p => p.Index == 77);
        BinaryPrimitives.WriteUInt64LittleEndian(p.Data.AsSpan(0xC8), 0x1000);
        BinaryPrimitives.WriteUInt64LittleEndian(p.Data.AsSpan(0xD0), 0x1004);
        Assert.Equal(L.Unit.PoisonUnknown, UnitConditions.Read(data, On(data, 7))[0].Value);
    }

    [Fact]
    public void SupplyUsesNativeConnectionFlagForArmiesAndTransports()
    {
        GameSnapshot data = Game(); UnitRecord unit = On(data, 2);
        Assert.Equal(L.Unit.SupplyCut, UnitConditions.Read(data, unit)[2].Value);
        unit.Data[0x26] = 2;
        Assert.Equal(L.Unit.SupplyConnected, UnitConditions.Read(data, unit)[2].Value);
        unit.Data[0x10] = 2; unit.Data[0x26] = 0;
        Assert.Equal(L.Unit.SupplyCut, UnitConditions.Read(data, unit)[2].Value);
        unit.Data[0x26] = 2;
        Assert.Equal(L.Unit.SupplyConnected, UnitConditions.Read(data, unit)[2].Value);
    }

    [Fact]
    public void TileReadRejectsInvalidIndexAndPreservesPoisonAndFire()
    {
        byte[] tiles = new byte[GameMemory.TileStride * 2];
        tiles[GameMemory.TileStride + 0x10] = 7;
        BinaryPrimitives.WriteUInt16LittleEndian(tiles.AsSpan(GameMemory.TileStride + 0x16), 720);
        Assert.Equal(new UnitTerrainState(1, 7, 720), UnitTerrainState.Read(tiles, 1));
        Assert.Null(UnitTerrainState.Read(tiles, -1));
        Assert.Null(UnitTerrainState.Read(tiles, 2));
        Assert.Null(UnitTerrainState.Read(tiles, GameMemory.TileCount));
    }
}
